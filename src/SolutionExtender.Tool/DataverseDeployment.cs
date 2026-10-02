using Microsoft.Crm.Sdk.Messages;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Query;
using SolutionExtender;

namespace SolutionExtender.Tool;

/// <summary>Solution-scoped, paged queries and ordered deployment actions. Authentication lives in DataverseConnection.</summary>
public sealed class DataverseDeployment(IOrganizationService service)
{
    public Guid? FindSolution(string name)
    {
        var q = new QueryExpression("solution") { ColumnSet = new("solutionid", "ismanaged") };
        q.Criteria.AddCondition("uniquename", ConditionOperator.Equal, name);
        var found = All(q);
        if (found.Count == 0) return null;
        if (found.Count != 1) throw new InvalidOperationException("Solution name was not unique.");
        if (found[0].GetAttributeValue<bool>("ismanaged")) throw new InvalidOperationException("Extended reconciliation is only supported for unmanaged solutions.");
        return found[0].Id;
    }

    public ExtendedManifest Snapshot(Guid solutionId, IEnumerable<EntityState>? states = null, bool owners = false, bool plugins = true, bool post = true)
    {
        var assemblies = plugins ? InSolution("pluginassembly", 91, solutionId) : [];
        var types = assemblies.SelectMany(a => Related("plugintype", "pluginassemblyid", a.Id)).ToList();
        var steps = plugins ? InSolution("sdkmessageprocessingstep", 92, solutionId)
            .Where(e => Code(e, "statecode") == 0 && Code(e, "statuscode") == 1).ToList() : [];
        var images = steps.SelectMany(s => Related("sdkmessageprocessingstepimage", "sdkmessageprocessingstepid", s.Id)
            .Select(i => new Component(i.Id, Name(s) + " " + Name(i)))).ToList();
        var workflows = post ? InSolution("workflow", 29, solutionId).Where(e => e.GetAttributeValue<OptionSetValue>("type")?.Value == 1).ToList() : [];
        return new ExtendedManifest
        {
            Assemblies = Components(assemblies), PluginTypes = Components(types), PluginSteps = Components(steps), PluginImages = images,
            CustomApis = plugins ? Components(InSolution("customapi", null, solutionId), "uniquename") : [],
            WebResources = post ? Components(InSolution("webresource", 61, solutionId)) : [],
            Workflows = workflows.Select(w => new Component(w.Id, Name(w), owners ? Owner(w) : null)).ToList(),
            States = states is null ? workflows.Where(HasState).Select(State).ToList()
                : states.Select(s => State(service.Retrieve(s.LogicalName, s.Id, new ColumnSet("statecode", "statuscode")))).ToList()
        };
    }

    public ExtendedManifest Capture(string zip, Guid solutionId)
    {
        var snapshot = Snapshot(solutionId, owners: true);
        snapshot.States.AddRange(SolutionPackage.ViewIds(zip).Select(id => service.Retrieve("savedquery", id, new ColumnSet("statecode", "statuscode")))
            .Where(HasState).Select(State));
        snapshot.Validate();
        return snapshot;
    }

    public DeploymentPlan Plan(string phase, ExtendedManifest source, Guid solutionId, bool reassign)
    {
        var target = Snapshot(solutionId, phase == "post-import" ? source.States : [], plugins: phase == "pre-import", post: phase == "post-import");
        var plan = Reconciliation.Compare(source, target, phase);
        if (!reassign || phase != "post-import") return plan;
        var actions = plan.Actions.ToList();
        // Match by domain name, not source environment user GUID. Missing/ambiguous owners fail before any mutation.
        var assignments = new List<DeploymentAction>();
        foreach (var workflow in source.Workflows.Where(w => !string.IsNullOrWhiteSpace(w.Owner)).OrderBy(w => w.Id))
        {
            var users = Related("systemuser", "domainname", workflow.Owner!).Where(u => !u.GetAttributeValue<bool>("isdisabled")).ToList();
            if (users.Count != 1) throw new InvalidOperationException($"Workflow owner '{workflow.Owner}' must match exactly one enabled target user.");
            var current = service.Retrieve("workflow", workflow.Id, new ColumnSet("ownerid", "statecode", "statuscode"));
            if (current.GetAttributeValue<EntityReference>("ownerid")?.Id == users[0].Id) continue;
            var desired = source.States.SingleOrDefault(s => s.LogicalName == "workflow" && s.Id == workflow.Id) ?? State(current);
            actions.RemoveAll(a => a.Kind == "set-state" && a.LogicalName == "workflow" && a.Id == workflow.Id);
            assignments.Add(new("set-state", "workflow", workflow.Id, workflow.Name, 0, 1));
            assignments.Add(new("assign", "workflow", workflow.Id, workflow.Name, OwnerId: users[0].Id));
            assignments.Add(new("set-state", "workflow", workflow.Id, workflow.Name, desired.StateCode, desired.StatusCode));
        }
        // Daxif performs workflow reassignment before restoring states.
        var firstState = actions.FindIndex(a => a.Kind == "set-state" && source.States.Any(s => s.Id == a.Id));
        actions.InsertRange(firstState < 0 ? actions.Count : firstState, assignments);
        return new(phase, actions);
    }

    public void Apply(DeploymentPlan plan, Action<DeploymentAction>? progress = null)
    {
        // Fail fast: never continue deleting parents after a failed child deletion.
        foreach (var action in plan.Actions)
        {
            progress?.Invoke(action);
            switch (action.Kind)
            {
                case "delete": service.Delete(action.LogicalName, action.Id); break;
                case "set-state":
                    var request = new OrganizationRequest("SetState");
                    request["EntityMoniker"] = new EntityReference(action.LogicalName, action.Id);
                    request["State"] = new OptionSetValue(action.StateCode!.Value);
                    request["Status"] = new OptionSetValue(action.StatusCode!.Value);
                    service.Execute(request); break;
                case "assign":
                    service.Execute(new AssignRequest { Target = new(action.LogicalName, action.Id), Assignee = new("systemuser", action.OwnerId!.Value) }); break;
                default: throw new InvalidOperationException($"Unknown action {action.Kind}.");
            }
        }
    }

    private List<Entity> InSolution(string table, int? componentType, Guid solutionId)
    {
        var q = new QueryExpression(table) { ColumnSet = new(true), Distinct = true };
        q.Criteria.AddCondition("ismanaged", ConditionOperator.Equal, false);
        var link = q.AddLink("solutioncomponent", table + "id", "objectid");
        link.LinkCriteria.AddCondition("solutionid", ConditionOperator.Equal, solutionId);
        // Custom API component type is environment-assigned (10038 in the live test).
        // Its objectid join already scopes to customapi records; never assume a fixed type.
        if (componentType.HasValue) link.LinkCriteria.AddCondition("componenttype", ConditionOperator.Equal, componentType.Value);
        return All(q);
    }
    private List<Entity> Related(string table, string column, object value)
    {
        var q = new QueryExpression(table) { ColumnSet = new(true) };
        q.Criteria.AddCondition(column, ConditionOperator.Equal, value);
        if (table is "plugintype" or "sdkmessageprocessingstepimage") q.Criteria.AddCondition("ismanaged", ConditionOperator.Equal, false);
        return All(q);
    }
    private List<Entity> All(QueryExpression query)
    {
        var result = new List<Entity>();
        query.AddOrder(query.EntityName + "id", OrderType.Ascending);
        query.PageInfo = new PagingInfo { Count = 5000, PageNumber = 1 };
        while (true)
        {
            var page = service.RetrieveMultiple(query); result.AddRange(page.Entities);
            if (!page.MoreRecords) return result;
            query.PageInfo.PageNumber++; query.PageInfo.PagingCookie = page.PagingCookie;
        }
    }
    private string Owner(Entity workflow)
    {
        var owner = workflow.GetAttributeValue<EntityReference>("ownerid") ?? throw new InvalidDataException("Workflow has no owner.");
        if (owner.LogicalName != "systemuser") throw new InvalidDataException("Team-owned workflows cannot be represented by Daxif's domain-name owner format.");
        return service.Retrieve("systemuser", owner.Id, new ColumnSet("domainname")).GetAttributeValue<string>("domainname")
            ?? throw new InvalidDataException("Workflow owner has no domainname.");
    }
    private static string Name(Entity e) => e.GetAttributeValue<string>("name") ?? throw new InvalidDataException($"Missing name on {e.LogicalName} {e.Id}.");
    private static List<Component> Components(IEnumerable<Entity> entities, string name = "name") => entities.Select(e =>
        new Component(e.Id, e.GetAttributeValue<string>(name) ?? throw new InvalidDataException($"Missing {name} on {e.LogicalName}."))).ToList();
    private static bool HasState(Entity e) => e.Contains("statecode") && e.Contains("statuscode");
    private static int Code(Entity e, string attribute) => e.GetAttributeValue<OptionSetValue>(attribute)?.Value ?? throw new InvalidDataException($"Missing {attribute} on {e.LogicalName}.");
    private static EntityState State(Entity e) => new(e.Id, e.LogicalName, Code(e, "statecode"), Code(e, "statuscode"));
}
