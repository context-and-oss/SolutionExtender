using System.Globalization;
using System.Xml;
using System.Xml.Linq;

namespace SolutionExtender;

/// <summary>Contains the component keep-lists and record states for a release.</summary>
public sealed class ExtendedManifest
{
    /// <summary>Gets the plugin assemblies to retain.</summary>
    public IList<Component> Assemblies { get; init; } = [];

    /// <summary>Gets the plugin types to retain.</summary>
    public IList<Component> PluginTypes { get; init; } = [];

    /// <summary>Gets the active plugin steps to retain.</summary>
    public IList<Component> PluginSteps { get; init; } = [];

    /// <summary>Gets the plugin images to retain.</summary>
    public IList<Component> PluginImages { get; init; } = [];

    /// <summary>Gets the workflow definitions to retain and their optional owner domain names.</summary>
    public IList<Component> Workflows { get; init; } = [];

    /// <summary>Gets the web resources to retain.</summary>
    public IList<Component> WebResources { get; init; } = [];

    /// <summary>Gets the APIs to retain, or null when API reconciliation was not captured.</summary>
    public IList<Component>? CustomApis
    {
        get; init;
    }

    /// <summary>Gets the desired record states.</summary>
    public IList<EntityState> States { get; init; } = [];

    /// <summary>Rejects invalid or duplicate component identities and unsupported state records.</summary>
    public void Validate()
    {
        foreach (var group in new[]
        {
            Assemblies,
            PluginTypes,
            PluginSteps,
            PluginImages,
            Workflows,
            WebResources,
            CustomApis ?? [],
        })
        {
            if (group.Any(c => c.Id == Guid.Empty || string.IsNullOrWhiteSpace(c.Name)))
            {
                throw new InvalidDataException("Components must have a non-empty ID and name.");
            }

            if (group.GroupBy(c => c.Id).Any(g => g.Count() > 1))
            {
                throw new InvalidDataException("Duplicate component IDs in extended manifest.");
            }
        }

        if (States.Any(s => s.Id == Guid.Empty || s.LogicalName is not ("savedquery" or "workflow")))
        {
            throw new InvalidDataException("States may only refer to savedquery or workflow records with non-empty IDs.");
        }

        if (States.GroupBy(s => s.Id).Any(g => g.Count() > 1))
        {
            throw new InvalidDataException("Duplicate entity states in extended manifest.");
        }
    }
}
