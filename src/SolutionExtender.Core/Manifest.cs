using System.Globalization;
using System.Xml;
using System.Xml.Linq;

namespace SolutionExtender;

public sealed record Component(Guid Id, string Name, string? Owner = null);
public sealed record EntityState(Guid Id, string LogicalName, int StateCode, int StatusCode);

public sealed class ExtendedManifest
{
    public List<Component> Assemblies { get; init; } = [];
    public List<Component> PluginTypes { get; init; } = [];
    public List<Component> PluginSteps { get; init; } = [];
    public List<Component> PluginImages { get; init; } = [];
    public List<Component> Workflows { get; init; } = [];
    public List<Component> WebResources { get; init; } = [];
    // null means an older Daxif manifest: not an instruction to delete every custom API.
    public List<Component>? CustomApis { get; init; }
    public List<EntityState> States { get; init; } = [];

    public void Validate()
    {
        foreach (var group in new[] { Assemblies, PluginTypes, PluginSteps, PluginImages, Workflows, WebResources, CustomApis ?? [] })
        {
            if (group.Any(c => c.Id == Guid.Empty || string.IsNullOrWhiteSpace(c.Name)))
                throw new InvalidDataException("Components must have a non-empty ID and name.");
            if (group.GroupBy(c => c.Id).Any(g => g.Count() > 1))
                throw new InvalidDataException("Duplicate component IDs in extended manifest.");
        }
        if (States.Any(s => s.Id == Guid.Empty || s.LogicalName is not ("savedquery" or "workflow")))
            throw new InvalidDataException("States may only refer to savedquery or workflow records with non-empty IDs.");
        if (States.GroupBy(s => s.Id).Any(g => g.Count() > 1))
            throw new InvalidDataException("Duplicate entity states in extended manifest.");
    }
}

/// <summary>Reads/writes Daxif's F# DataContract XML without depending on FSharp.Core or Daxif.</summary>
public static class ManifestXml
{
    private static readonly XNamespace Domain = "http://schemas.datacontract.org/2004/07/DG.Daxif.Modules.Solution";
    private static readonly XNamespace SystemNs = "http://schemas.datacontract.org/2004/07/System";
    private static readonly XNamespace FSharp = "http://schemas.datacontract.org/2004/07/Microsoft.FSharp.Collections";
    private static readonly XNamespace Generic = "http://schemas.datacontract.org/2004/07/System.Collections.Generic";
    private static string Name(XElement e) => XmlConvert.DecodeName(e.Name.LocalName).TrimEnd('@');
    private static XElement? Child(XElement e, string name) => e.Elements().SingleOrDefault(x => Name(x) == name);
    private static string Text(XElement e, string name) => Child(e, name)?.Value
        ?? throw new InvalidDataException($"Missing '{name}' in extended manifest.");
    private static Guid Id(string value) => Guid.TryParse(value, out var id) && id != Guid.Empty ? id
        : throw new InvalidDataException($"Invalid GUID '{value}' in extended manifest.");

    public static XDocument Load(Stream stream)
    {
        using var reader = XmlReader.Create(stream, new XmlReaderSettings
        { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 64 * 1024 * 1024 });
        return XDocument.Load(reader);
    }

    public static ExtendedManifest Read(Stream stream) => Read(Load(stream));
    public static ExtendedManifest Read(XDocument document)
    {
        var root = document.Root ?? throw new InvalidDataException("Empty extended manifest.");
        if (root.Name != Domain + "Domain.ExtendedSolution")
            throw new InvalidDataException("Not a Daxif ExtendedSolution.xml document.");
        var allowed = new HashSet<string> { "keepAssemblies", "keepPluginTypes", "keepPluginSteps", "keepPluginImages", "keepWorkflows", "keepWebresources", "keepCustomAPIs", "states" };
        if (root.Elements().Any(e => !allowed.Contains(Name(e))))
            throw new InvalidDataException("Unsupported extended manifest field; refusing to silently discard metadata.");
        List<Component> Components(string name, bool owners = false)
        {
            var field = Child(root, name) ?? throw new InvalidDataException($"Missing '{name}' in extended manifest.");
            return field.Elements().Select(e => new Component(Id(Text(e, "m_Item1")), Text(e, "m_Item2"),
                owners ? Text(e, "m_Item3") : null)).ToList();
        }
        var states = Child(root, "states") ?? throw new InvalidDataException("Missing states in extended manifest.");
        var data = Child(states, "serializedData") ?? throw new InvalidDataException("Missing states serializedData.");
        var manifest = new ExtendedManifest
        {
            Assemblies = Components("keepAssemblies"), PluginTypes = Components("keepPluginTypes"),
            PluginSteps = Components("keepPluginSteps"), PluginImages = Components("keepPluginImages"),
            WebResources = Components("keepWebresources"), Workflows = Components("keepWorkflows", true),
            CustomApis = Child(root, "keepCustomAPIs") is null ? null : Components("keepCustomAPIs"),
            States = data.Elements().Select(e =>
            {
                var v = Child(e, "value") ?? throw new InvalidDataException("Missing state value.");
                var id = Id(Text(v, "id"));
                if (Id(Text(e, "key")) != id) throw new InvalidDataException("State map key differs from entity ID.");
                return new EntityState(id, Text(v, "logicalName"),
                    int.Parse(Text(v, "stateCode"), CultureInfo.InvariantCulture),
                    int.Parse(Text(v, "statusCode"), CultureInfo.InvariantCulture));
            }).ToList()
        };
        manifest.Validate();
        return manifest;
    }

    public static byte[] Write(ExtendedManifest manifest)
    {
        manifest.Validate();
        XElement Field(string name, IEnumerable<Component> components, bool owners = false) =>
            new(Domain + name + "_x0040_", new XAttribute(XNamespace.Xmlns + "a", SystemNs),
                components.OrderBy(c => c.Id).Select(c => new XElement(SystemNs + (owners ? "TupleOfguidstringstring" : "TupleOfguidstring"),
                    new XElement(SystemNs + "m_Item1", c.Id), new XElement(SystemNs + "m_Item2", c.Name),
                    owners ? new XElement(SystemNs + "m_Item3", c.Owner ?? "") : null)));
        var root = new XElement(Domain + "Domain.ExtendedSolution",
            new XAttribute("xmlns", Domain.NamespaceName),
            new XAttribute(XNamespace.Xmlns + "i", "http://www.w3.org/2001/XMLSchema-instance"),
            Field("keepAssemblies", manifest.Assemblies));
        if (manifest.CustomApis is not null) root.Add(Field("keepCustomAPIs", manifest.CustomApis));
        root.Add(Field("keepPluginImages", manifest.PluginImages), Field("keepPluginSteps", manifest.PluginSteps),
            Field("keepPluginTypes", manifest.PluginTypes), Field("keepWebresources", manifest.WebResources),
            Field("keepWorkflows", manifest.Workflows, true),
            new XElement(Domain + "states_x0040_", new XAttribute(XNamespace.Xmlns + "a", FSharp),
                new XElement(FSharp + "serializedData", new XAttribute(XNamespace.Xmlns + "b", Generic),
                    manifest.States.OrderBy(s => s.Id).Select(s => new XElement(Generic + "KeyValuePairOfstringDomain.EntityState97c0x1za",
                        new XElement(Generic + "key", s.Id), new XElement(Generic + "value",
                            new XElement(Domain + "id_x0040_", s.Id), new XElement(Domain + "logicalName_x0040_", s.LogicalName),
                            new XElement(Domain + "stateCode_x0040_", s.StateCode), new XElement(Domain + "statusCode_x0040_", s.StatusCode)))))));
        using var stream = new MemoryStream();
        using (var writer = XmlWriter.Create(stream, new XmlWriterSettings { Encoding = new System.Text.UTF8Encoding(false), Indent = true, OmitXmlDeclaration = true }))
            root.Save(writer);
        return stream.ToArray();
    }
}
