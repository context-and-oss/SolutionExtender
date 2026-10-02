using System.Globalization;
using System.Xml;
using System.Xml.Linq;

namespace SolutionExtender;

/// <summary>Reads and writes SolutionExtender's versioned XML contract.</summary>
public static class ManifestXml
{
    /// <summary>The namespace identifying the native manifest contract.</summary>
    public const string NamespaceUri = "urn:solutionextender:manifest";

    /// <summary>The supported native manifest schema version.</summary>
    public const int CurrentVersion = 1;
    private static readonly XNamespace Ns = NamespaceUri;

    /// <summary>Loads XML with DTDs prohibited, external resolution disabled, and a document size limit.</summary>
    /// <param name="stream">The readable XML stream.</param>
    /// <returns>The parsed XML document.</returns>
    public static XDocument Load(Stream stream)
    {
        using var reader = XmlReader.Create(stream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 64 * 1024 * 1024 });
        return XDocument.Load(reader);
    }

    /// <summary>Reads and validates native extended metadata.</summary>
    /// <param name="stream">The readable XML stream.</param>
    /// <returns>The validated metadata.</returns>
    public static ExtendedManifest Read(Stream stream) => Read(Load(stream));

    /// <summary>Reads and validates native extended metadata.</summary>
    /// <param name="document">The parsed XML document.</param>
    /// <returns>The validated metadata.</returns>
    public static ExtendedManifest Read(XDocument document)
    {
        var root = document.Root ?? throw new InvalidDataException("Empty extended manifest.");
        if (root.Name != Ns + "ExtendedSolution")
        {
            throw new InvalidDataException("Not a SolutionExtender manifest.");
        }

        Shape(root, ["Assemblies", "PluginTypes", "PluginSteps", "PluginImages", "Workflows", "WebResources", "CustomApis", "States"], ["version"]);
        if (!string.Equals(Attribute(root, "version"), CurrentVersion.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal))
        {
            throw new InvalidDataException($"Unsupported extended manifest version '{Attribute(root, "version")}'.");
        }

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
            Assemblies = Components("Assemblies"),
            PluginTypes = Components("PluginTypes"),
            PluginSteps = Components("PluginSteps"),
            PluginImages = Components("PluginImages"),
            WebResources = Components("WebResources"),
            Workflows = Components("Workflows", true),
            CustomApis = root.Element(Ns + "CustomApis") is null ? null : Components("CustomApis"),
            States = states.Elements().Select(e =>
            {
                Shape(e, [], ["id", "logicalName", "stateCode", "statusCode"]);
                return new EntityState(Id(Attribute(e, "id")), Attribute(e, "logicalName"), Number(Attribute(e, "stateCode")), Number(Attribute(e, "statusCode")));
            }).ToList(),
        };
        manifest.Validate();
        return manifest;
    }

    /// <summary>Serializes validated native metadata deterministically as UTF-8 XML.</summary>
    /// <param name="manifest">The native extended metadata.</param>
    /// <returns>The UTF-8 manifest bytes.</returns>
    public static byte[] Write(ExtendedManifest manifest)
    {
        manifest.Validate();
        XElement Field(string name, IEnumerable<Component> components, bool owners = false) => new(Ns + name, components.OrderBy(c => c.Id).Select(c => new XElement(Ns + "Component", new XAttribute("id", c.Id), new XAttribute("name", c.Name), owners && c.Owner is not null ? new XAttribute("owner", c.Owner) : null)));
        var root = new XElement(Ns + "ExtendedSolution", new XAttribute("xmlns", NamespaceUri), new XAttribute("version", CurrentVersion), Field("Assemblies", manifest.Assemblies), Field("PluginTypes", manifest.PluginTypes), Field("PluginSteps", manifest.PluginSteps), Field("PluginImages", manifest.PluginImages), Field("Workflows", manifest.Workflows, true), Field("WebResources", manifest.WebResources));
        if (manifest.CustomApis is not null)
        {
            root.Add(Field("CustomApis", manifest.CustomApis));
        }

        root.Add(new XElement(Ns + "States", manifest.States.OrderBy(s => s.Id).Select(s => new XElement(Ns + "State", new XAttribute("id", s.Id), new XAttribute("logicalName", s.LogicalName), new XAttribute("stateCode", s.StateCode), new XAttribute("statusCode", s.StatusCode)))));
        using var stream = new MemoryStream();
        using (var writer = XmlWriter.Create(stream, new XmlWriterSettings { Encoding = new System.Text.UTF8Encoding(false), Indent = true, OmitXmlDeclaration = true }))
        {
            root.Save(writer);
        }

        return stream.ToArray();
    }

    private static XElement Child(XElement e, string name)
    {
        var children = e.Elements(Ns + name).ToArray();
        return children.Length == 1 ? children[0] : throw new InvalidDataException($"Expected exactly one '{name}' field.");
    }

    private static string Attribute(XElement e, string name) => e.Attribute(name)?.Value ?? throw new InvalidDataException($"Missing '{name}' on '{e.Name.LocalName}'.");

    private static Guid Id(string value) => Guid.TryParse(value, out var id) && id != Guid.Empty ? id : throw new InvalidDataException($"Invalid GUID '{value}' in extended manifest.");

    private static int Number(string value) => int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number) ? number : throw new InvalidDataException($"Invalid state/status code '{value}' in extended manifest.");

    private static void Shape(XElement element, string[] children, string[] attributes)
    {
        if (element.Elements().Any(e => e.Name.Namespace != Ns || !children.Contains(e.Name.LocalName)) || element.Attributes().Any(a => !a.IsNamespaceDeclaration && (a.Name.Namespace != XNamespace.None || !attributes.Contains(a.Name.LocalName))) || element.Nodes().OfType<XText>().Any(t => !string.IsNullOrWhiteSpace(t.Value)))
        {
            throw new InvalidDataException($"Unsupported content in '{element.Name.LocalName}'; refusing to discard metadata.");
        }
    }
}
