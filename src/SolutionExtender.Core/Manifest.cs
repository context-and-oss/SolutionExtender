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
    // null means API reconciliation was not captured; it must not imply deletion of every API.
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

/// <summary>Reads and writes SolutionExtender's versioned XML contract.</summary>
public static class ManifestXml
{
    public const string NamespaceUri = "urn:solutionextender:manifest";
    public const int CurrentVersion = 1;
    private static readonly XNamespace Ns = NamespaceUri;

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
        if (root.Name != Ns + "ExtendedSolution") throw new InvalidDataException("Not a SolutionExtender manifest.");
        Shape(root, ["Assemblies", "PluginTypes", "PluginSteps", "PluginImages", "Workflows", "WebResources", "CustomApis", "States"], ["version"]);
        if (Attribute(root, "version") != CurrentVersion.ToString(CultureInfo.InvariantCulture))
            throw new InvalidDataException($"Unsupported extended manifest version '{Attribute(root, "version")}'.");

        List<Component> Components(string name, bool owners = false)
        {
            var field = Child(root, name);
            Shape(field, ["Component"], []);
            return field.Elements().Select(e =>
            {
                Shape(e, [], owners ? ["id", "name", "owner"] : ["id", "name"]);
                return new Component(Id(Attribute(e, "id")), Attribute(e, "name"), owners ? e.Attribute("owner")?.Value : null);
            }).ToList();
        }
        var states = Child(root, "States");
        Shape(states, ["State"], []);
        var manifest = new ExtendedManifest
        {
            Assemblies = Components("Assemblies"), PluginTypes = Components("PluginTypes"),
            PluginSteps = Components("PluginSteps"), PluginImages = Components("PluginImages"),
            WebResources = Components("WebResources"), Workflows = Components("Workflows", true),
            CustomApis = root.Element(Ns + "CustomApis") is null ? null : Components("CustomApis"),
            States = states.Elements().Select(e =>
            {
                Shape(e, [], ["id", "logicalName", "stateCode", "statusCode"]);
                return new EntityState(Id(Attribute(e, "id")), Attribute(e, "logicalName"),
                    Number(Attribute(e, "stateCode")), Number(Attribute(e, "statusCode")));
            }).ToList()
        };
        manifest.Validate();
        return manifest;
    }

    public static byte[] Write(ExtendedManifest manifest)
    {
        manifest.Validate();
        XElement Field(string name, IEnumerable<Component> components, bool owners = false) =>
            new(Ns + name, components.OrderBy(c => c.Id).Select(c => new XElement(Ns + "Component",
                new XAttribute("id", c.Id), new XAttribute("name", c.Name),
                owners && c.Owner is not null ? new XAttribute("owner", c.Owner) : null)));
        var root = new XElement(Ns + "ExtendedSolution", new XAttribute("xmlns", NamespaceUri),
            new XAttribute("version", CurrentVersion),
            Field("Assemblies", manifest.Assemblies), Field("PluginTypes", manifest.PluginTypes),
            Field("PluginSteps", manifest.PluginSteps), Field("PluginImages", manifest.PluginImages),
            Field("Workflows", manifest.Workflows, true), Field("WebResources", manifest.WebResources));
        if (manifest.CustomApis is not null) root.Add(Field("CustomApis", manifest.CustomApis));
        root.Add(new XElement(Ns + "States", manifest.States.OrderBy(s => s.Id).Select(s => new XElement(Ns + "State",
            new XAttribute("id", s.Id), new XAttribute("logicalName", s.LogicalName),
            new XAttribute("stateCode", s.StateCode), new XAttribute("statusCode", s.StatusCode)))));
        using var stream = new MemoryStream();
        using (var writer = XmlWriter.Create(stream, new XmlWriterSettings { Encoding = new System.Text.UTF8Encoding(false), Indent = true, OmitXmlDeclaration = true }))
            root.Save(writer);
        return stream.ToArray();
    }

    private static XElement Child(XElement e, string name)
    {
        var children = e.Elements(Ns + name).ToArray();
        return children.Length == 1 ? children[0] : throw new InvalidDataException($"Expected exactly one '{name}' field.");
    }
    private static string Attribute(XElement e, string name) => e.Attribute(name)?.Value
        ?? throw new InvalidDataException($"Missing '{name}' on '{e.Name.LocalName}'.");
    private static Guid Id(string value) => Guid.TryParse(value, out var id) && id != Guid.Empty ? id
        : throw new InvalidDataException($"Invalid GUID '{value}' in extended manifest.");
    private static int Number(string value) => int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number) ? number
        : throw new InvalidDataException($"Invalid state/status code '{value}' in extended manifest.");
    private static void Shape(XElement element, string[] children, string[] attributes)
    {
        if (element.Elements().Any(e => e.Name.Namespace != Ns || !children.Contains(e.Name.LocalName)) ||
            element.Attributes().Any(a => !a.IsNamespaceDeclaration && (a.Name.Namespace != XNamespace.None || !attributes.Contains(a.Name.LocalName))) ||
            element.Nodes().OfType<XText>().Any(t => !string.IsNullOrWhiteSpace(t.Value)))
            throw new InvalidDataException($"Unsupported content in '{element.Name.LocalName}'; refusing to discard metadata.");
    }
}
