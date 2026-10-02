using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;
using SolutionExtender;
using SolutionExtender.Tool;
using Xunit;

namespace SolutionExtender.Tests;

public sealed class DeploymentTests
{
    [Fact]
    public void AppliesInOrderAndStopsOnFailure()
    {
        var fake = new FakeService();
        var deployment = new DataverseDeployment(fake);
        var id = Guid.NewGuid();
        var plan = new DeploymentPlan("post-import", [new("set-state", "workflow", id, "flow", 0, 1), new("delete", "workflow", id, "flow"), new("delete", "webresource", Guid.NewGuid(), "resource")]);
        fake.FailDelete = true;
        Assert.Throws<InvalidOperationException>(() => deployment.Apply(plan));
        Assert.Equal(new[] { "SetState", "delete:workflow" }, fake.Calls);
    }

    [Fact]
    public void FindsMissingSolutionWithoutCreatingIt()
    {
        var fake = new FakeService();
        Assert.Null(new DataverseDeployment(fake).FindSolution("new"));
        Assert.Empty(fake.Calls);
    }

    [Fact]
    public void RefusesManagedTarget()
    {
        var fake = new FakeService
        {
            Query = q => new EntityCollection([new Entity("solution", Guid.NewGuid()) { ["ismanaged"] = true }]),
        };
        Assert.Throws<InvalidOperationException>(() => new DataverseDeployment(fake).FindSolution("managed"));
    }

    [Fact]
    public void QueriesArePagedAndScopedToSolutionAndUnmanagedComponents()
    {
        var solution = Guid.NewGuid();
        var queries = new List<QueryExpression>();
        var fake = new FakeService
        {
            Query = q =>
            {
                queries.Add(q);
                return new();
            },
        };
        _ = new DataverseDeployment(fake).Snapshot(solution, []);
        Assert.Equal(5, queries.Count);
        foreach (var q in queries)
        {
            Assert.Equal(5000, q.PageInfo.Count);
            Assert.Contains(q.Criteria.Conditions, c => string.Equals(c.AttributeName, "ismanaged", StringComparison.Ordinal) && Equals(c.Values[0], false));
            var link = Assert.Single(q.LinkEntities);
            Assert.Contains(link.LinkCriteria.Conditions, c => string.Equals(c.AttributeName, "solutionid", StringComparison.Ordinal) && Equals(c.Values[0], solution));
            if (!string.Equals(q.EntityName, "customapi", StringComparison.Ordinal))
            {
                Assert.Contains(link.LinkCriteria.Conditions, c => string.Equals(c.AttributeName, "componenttype", StringComparison.Ordinal));
            }
        }
    }

    [Fact]
    public void CustomApisDoNotAssumeAUniversalComponentType()
    {
        var id = Guid.NewGuid();
        var fake = new FakeService
        {
            Query = q =>
            {
                if (!string.Equals(q.EntityName, "customapi", StringComparison.Ordinal))
                {
                    return new();
                }

                var link = Assert.Single(q.LinkEntities);
                Assert.DoesNotContain(link.LinkCriteria.Conditions, c => string.Equals(c.AttributeName, "componenttype", StringComparison.Ordinal));
                Assert.Equal("objectid", link.LinkToAttributeName);
                return new EntityCollection([new Entity("customapi", id) { ["uniquename"] = "ctx_BedrockEcho" }]);
            },
        };
        var snapshot = new DataverseDeployment(fake).Snapshot(Guid.NewGuid(), []);
        Assert.Equal(new Component(id, "ctx_BedrockEcho"), Assert.Single(snapshot.CustomApis!));
    }

    [Fact]
    public void FollowsPagingCookieWhenFindingSolution()
    {
        var id = Guid.NewGuid();
        var pages = new List<(int Page, string? Cookie)>();
        var fake = new FakeService
        {
            Query = q =>
            {
                pages.Add((q.PageInfo.PageNumber, q.PageInfo.PagingCookie));
                return q.PageInfo.PageNumber == 1 ? new EntityCollection
                {
                    MoreRecords = true,
                    PagingCookie = "next-page",
                }

                : new EntityCollection([new Entity("solution", id) { ["ismanaged"] = false }]);
            },
        };
        Assert.Equal(id, new DataverseDeployment(fake).FindSolution("test"));
        Assert.Equal(new[] { (1, (string?)null), (2, (string?)"next-page") }, pages);
    }

    [Fact]
    public void CaptureIncludesViewStatesFromFreshExport()
    {
        var fake = new FakeService
        {
            RetrieveHandler = (table, id) => new Entity(table, id)
            {
                ["statecode"] = new OptionSetValue(0),
                ["statuscode"] = new OptionSetValue(1),
            },
        };
        using var temp = new TemporaryDirectory();
        var snapshot = new DataverseDeployment(fake).Capture(NativeFixture.CreatePackage(temp.Path), Guid.NewGuid());
        Assert.Equal(7, snapshot.States.Count);
        Assert.All(snapshot.States, s => Assert.Equal("savedquery", s.LogicalName));
    }

    [Fact]
    public void ReassignmentDraftsAssignsAndRestoresEvenWhenOriginalStateWasAlreadyCorrect()
    {
        var id = Guid.NewGuid();
        var user = Guid.NewGuid();
        var workflow = new Entity("workflow", id)
        {
            ["name"] = "flow",
            ["type"] = new OptionSetValue(1),
            ["statecode"] = new OptionSetValue(1),
            ["statuscode"] = new OptionSetValue(2),
            ["ownerid"] = new EntityReference("systemuser", Guid.NewGuid()),
        };
        var fake = new FakeService
        {
            RetrieveEntity = workflow,
            Query = q => q.EntityName switch
            {
                "workflow" => new EntityCollection([workflow]),
                "systemuser" => new EntityCollection([new Entity("systemuser", user) { ["isdisabled"] = false }]),
                _ => new(),
            },
        };
        var source = new ExtendedManifest
        {
            Workflows = [new(id, "flow", "user@org")],
            States = [new(id, "workflow", 1, 2)],
        };
        var deployment = new DataverseDeployment(fake);
        var plan = deployment.Plan("post-import", source, Guid.NewGuid(), true);
        Assert.Equal(new[] { "set-state", "assign", "set-state" }, plan.Actions.Select(a => a.Kind));
        Assert.Equal(0, plan.Actions[0].StateCode);
        Assert.Equal(user, plan.Actions[1].OwnerId);
        Assert.Equal(1, plan.Actions[2].StateCode);
        Assert.Empty(fake.Calls); // Planning never mutates.
        deployment.Apply(plan);
        Assert.Equal(new[] { "SetState", "Assign", "SetState" }, fake.Calls);
    }

    [Fact]
    public void MissingOwnerFailsBeforeMutations()
    {
        var fake = new FakeService();
        var source = new ExtendedManifest
        {
            Workflows = [new(Guid.NewGuid(), "flow", "missing@org")],
        };
        Assert.Throws<InvalidOperationException>(() => new DataverseDeployment(fake).Plan("post-import", source, Guid.NewGuid(), true));
        Assert.Empty(fake.Calls);
    }
}
