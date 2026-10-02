using System.IO.Compression;
using System.Xml.Linq;
using SolutionExtender;

namespace SolutionExtender.Tests;

internal static class NativeFixture
{
    private static string ManifestPath => Path.Combine(AppContext.BaseDirectory, "Fixtures", "ExtendedSolution.xml");
    public static ExtendedManifest Manifest() => SolutionPackage.Read(ManifestPath);

    // A fresh PAC-style export, with no extended metadata unless explicitly attached by our tool.
    public static string CreatePackage(string folder, bool extended = false)
    {
        var zip = Path.Combine(folder, "export.zip");
        using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
        {
            Write("solution.xml", "<ImportExportXml><SolutionManifest><UniqueName>Example</UniqueName><Managed>0</Managed></SolutionManifest></ImportExportXml>");
            var customizations = new XElement("ImportExportXml", new XElement("Entities", new XElement("Entity", new XElement("SavedQueries",
                Manifest().States.Select(s => new XElement("savedquery", new XElement("isprivate", 0), new XElement("isdefault", 0), new XElement("savedqueryid", s.Id)))))));
            Write("customizations.xml", customizations.ToString());
            Write("WebResources/ctx_example.js", "console.log('example');");
            using var binary = archive.CreateEntry("PluginAssemblies/Example.dll").Open();
            binary.Write([0, 1, 2, 255]);
            void Write(string name, string text)
            {
                using var writer = new StreamWriter(archive.CreateEntry(name).Open());
                writer.Write(text);
            }
        }
        if (extended) SolutionPackage.Attach(zip, ManifestPath, zip, overwrite: true);
        return zip;
    }
}
