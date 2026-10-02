using SolutionExtender;
using Xunit;

namespace SolutionExtender.Tests;

public sealed class ReconciliationTests
{
    private static Component C(string name) => new(Guid.NewGuid(), name);
    [Fact]
    public void UsesDaxifIdentityRulesAndChildFirstOrder()
    {
        var source = new ExtendedManifest { Assemblies = [C("assembly")], PluginTypes = [C("type")], PluginSteps = [C("step")],
            PluginImages = [C("image")], CustomApis = [C("api")] };
        var oldStep = C("step"); var oldImage = C("image");
        var target = new ExtendedManifest { Assemblies = [C("assembly"), C("old assembly")], PluginTypes = [C("type"), C("old type")],
            PluginSteps = [oldStep], PluginImages = [oldImage], CustomApis = [C("api"), C("old api")] };
        var plan = Reconciliation.Compare(source, target, "pre-import");
        Assert.Equal(new[] { "customapi", "sdkmessageprocessingstepimage", "sdkmessageprocessingstep", "plugintype", "pluginassembly" }, plan.Actions.Select(a => a.LogicalName));
        Assert.Equal(oldStep.Id, plan.Actions[2].Id); Assert.Equal(oldImage.Id, plan.Actions[1].Id);
    }
    [Fact]
    public void PostDeactivatesObsoleteWorkflowsAndRestoresStates()
    {
        var kept = C("flow"); var obsolete = C("flow"); var view = Guid.NewGuid();
        var source = new ExtendedManifest { Workflows = [kept], WebResources = [C("resource")], States = [new(view, "savedquery", 1, 2)] };
        var target = new ExtendedManifest { Workflows = [kept, obsolete], WebResources = [C("resource"), C("old resource")], States = [new(view, "savedquery", 0, 1)] };
        var actions = Reconciliation.Compare(source, target, "post-import").Actions;
        Assert.Equal(new[] { "delete", "set-state", "delete", "set-state" }, actions.Select(a => a.Kind));
        Assert.Equal(obsolete.Id, actions[1].Id); Assert.Equal(0, actions[1].StateCode); Assert.Equal(1, actions[1].StatusCode);
        Assert.Equal(view, actions[3].Id); Assert.Equal(1, actions[3].StateCode);
    }
    [Fact]
    public void IdenticalSnapshotsProduceNoActions()
    {
        var m = SolutionPackage.Read(Path.Combine(AppContext.BaseDirectory, "magnus.zip"));
        Assert.Empty(Reconciliation.Compare(m, m, "pre-import").Actions);
        Assert.Empty(Reconciliation.Compare(m, m, "post-import").Actions);
    }
    [Fact]
    public void MissingStateTargetFailsPlanning()
    {
        var source = new ExtendedManifest { States = [new(Guid.NewGuid(), "workflow", 1, 2)] };
        Assert.Throws<InvalidDataException>(() => Reconciliation.Compare(source, new(), "post-import"));
    }
    [Fact]
    public void ExplicitlyEmptyCustomApisMeansDeleteAll()
    {
        var target = new ExtendedManifest { CustomApis = [C("obsolete")] };
        Assert.Single(Reconciliation.Compare(new() { CustomApis = [] }, target, "pre-import").Actions);
    }
}
