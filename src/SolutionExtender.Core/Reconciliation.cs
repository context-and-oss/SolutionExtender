namespace SolutionExtender;

/// <summary>Compares release metadata with a target snapshot without mutating Dataverse.</summary>
public static class Reconciliation
{
    /// <summary>Builds an ordered reconciliation plan from source and target snapshots.</summary>
    /// <param name="source">The release metadata.</param>
    /// <param name="target">The target environment snapshot.</param>
    /// <param name="phase">The pre-import or post-import phase.</param>
    /// <returns>The ordered deployment plan.</returns>
    public static DeploymentPlan Compare(ExtendedManifest source, ExtendedManifest target, string phase)
    {
        source.Validate();
        target.Validate();
        var actions = new List<DeploymentAction>();
        void Delete(string logicalName, IEnumerable<Component> keep, IEnumerable<Component> existing, bool byName)
        {
            var names = keep.Select(c => c.Name).ToHashSet(StringComparer.Ordinal);
            var ids = keep.Select(c => c.Id).ToHashSet();
            foreach (var component in existing.Where(c => byName ? !names.Contains(c.Name) : !ids.Contains(c.Id)).OrderBy(c => c.Id))
            {
                if (string.Equals(logicalName, "workflow", StringComparison.Ordinal))
                {
                    actions.Add(new("set-state", logicalName, component.Id, component.Name, 0, 1));
                }

                actions.Add(new("delete", logicalName, component.Id, component.Name));
            }
        }

        if (string.Equals(phase, "pre-import", StringComparison.Ordinal))
        {
            if (source.CustomApis is not null && target.CustomApis is not null)
            {
                Delete("customapi", source.CustomApis, target.CustomApis, true);
            }

            Delete("sdkmessageprocessingstepimage", source.PluginImages, target.PluginImages, false);
            Delete("sdkmessageprocessingstep", source.PluginSteps, target.PluginSteps, false);
            Delete("plugintype", source.PluginTypes, target.PluginTypes, true);
            Delete("pluginassembly", source.Assemblies, target.Assemblies, true);
        }
        else if (string.Equals(phase, "post-import", StringComparison.Ordinal))
        {
            Delete("webresource", source.WebResources, target.WebResources, true);
            Delete("workflow", source.Workflows, target.Workflows, false);
            var states = target.States.ToDictionary(s => s.Id);
            foreach (var state in source.States.OrderBy(s => s.Id))
            {
                if (!states.TryGetValue(state.Id, out var current) || !string.Equals(current.LogicalName, state.LogicalName, StringComparison.Ordinal))
                {
                    throw new InvalidDataException($"Cannot restore state: {state.LogicalName} {state.Id} is missing from target snapshot.");
                }

                if (state.StateCode != current.StateCode || state.StatusCode != current.StatusCode)
                {
                    actions.Add(new("set-state", state.LogicalName, state.Id, state.Id.ToString(), state.StateCode, state.StatusCode));
                }
            }
        }
        else
        {
            throw new ArgumentException("Phase must be pre-import or post-import.", nameof(phase));
        }

        return new(phase, actions);
    }
}
