namespace SolutionExtender;

public sealed record DeploymentAction(string Kind, string LogicalName, Guid Id, string Name,
    int? StateCode = null, int? StatusCode = null, Guid? OwnerId = null);
public sealed record DeploymentPlan(string Phase, IReadOnlyList<DeploymentAction> Actions);

public static class Reconciliation
{
    public static DeploymentPlan Compare(ExtendedManifest source, ExtendedManifest target, string phase)
    {
        source.Validate(); target.Validate();
        var actions = new List<DeploymentAction>();
        void Delete(string logicalName, IEnumerable<Component> keep, IEnumerable<Component> existing, bool byName)
        {
            var names = keep.Select(c => c.Name).ToHashSet(StringComparer.Ordinal);
            var ids = keep.Select(c => c.Id).ToHashSet();
            foreach (var component in existing.Where(c => byName ? !names.Contains(c.Name) : !ids.Contains(c.Id)).OrderBy(c => c.Id))
            {
                if (logicalName == "workflow") actions.Add(new("set-state", logicalName, component.Id, component.Name, 0, 1));
                actions.Add(new("delete", logicalName, component.Id, component.Name));
            }
        }
        if (phase == "pre-import")
        {
            if (source.CustomApis is not null && target.CustomApis is not null) Delete("customapi", source.CustomApis, target.CustomApis, true);
            Delete("sdkmessageprocessingstepimage", source.PluginImages, target.PluginImages, false);
            Delete("sdkmessageprocessingstep", source.PluginSteps, target.PluginSteps, false);
            Delete("plugintype", source.PluginTypes, target.PluginTypes, true);
            Delete("pluginassembly", source.Assemblies, target.Assemblies, true);
        }
        else if (phase == "post-import")
        {
            Delete("webresource", source.WebResources, target.WebResources, true);
            Delete("workflow", source.Workflows, target.Workflows, false);
            var states = target.States.ToDictionary(s => s.Id);
            foreach (var state in source.States.OrderBy(s => s.Id))
            {
                if (!states.TryGetValue(state.Id, out var current) || current.LogicalName != state.LogicalName)
                    throw new InvalidDataException($"Cannot restore state: {state.LogicalName} {state.Id} is missing from target snapshot.");
                if (state.StateCode != current.StateCode || state.StatusCode != current.StatusCode)
                    actions.Add(new("set-state", state.LogicalName, state.Id, state.Id.ToString(), state.StateCode, state.StatusCode));
            }
        }
        else throw new ArgumentException("Phase must be pre-import or post-import.");
        return new(phase, actions);
    }
}
