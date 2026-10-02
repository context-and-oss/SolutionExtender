using System.IO.Compression;
using System.Text;
using System.Xml.Linq;
using SolutionExtender;
using SolutionExtender.Tool;
using Xunit;

namespace SolutionExtender.Tests;

public sealed class ManifestTests
{
    private static string Magnus => Path.Combine(AppContext.BaseDirectory, "magnus.zip");
    [Fact]
    public void ReadsRealDaxifExport()
    {
        var m = SolutionPackage.Read(Magnus);
        Assert.Single(m.Assemblies); Assert.Equal(3, m.PluginTypes.Count);
        Assert.Equal(2, m.PluginSteps.Count); Assert.Empty(m.PluginImages);
        Assert.Equal(7, m.WebResources.Count); Assert.Empty(m.Workflows);
        Assert.Single(m.CustomApis!); Assert.Equal(7, m.States.Count);
        Assert.All(m.States, s => { Assert.Equal("savedquery", s.LogicalName); Assert.Equal(0, s.StateCode); Assert.Equal(1, s.StatusCode); });
        Assert.Equal(new SolutionIdentity("Magnus", false), SolutionPackage.Identity(Magnus));
    }
    [Fact]
    public void XmlRoundTripIsStableAndPreservesOwners()
    {
        var m = SolutionPackage.Read(Magnus);
        m.Workflows.Add(new(Guid.NewGuid(), "Flow & <test>", "user@example.org"));
        m.States.Add(new(m.Workflows[0].Id, "workflow", 1, 2));
        var bytes = ManifestXml.Write(m);
        using var stream = new MemoryStream(bytes);
        var read = ManifestXml.Read(stream);
        Assert.Equal(m.Workflows, read.Workflows);
        Assert.Equal(bytes, ManifestXml.Write(read));
    }
    [Fact]
    public void PacSidecarAttachKeepsEveryOtherZipEntry()
    {
        using var temp = new TemporaryDirectory();
        SolutionPackage.Extract(Magnus, temp.Path, false);
        var output = Path.Combine(temp.Path, "packed.zip");
        SolutionPackage.Attach(Magnus, temp.Path, output, false);
        using var original = ZipFile.OpenRead(Magnus); using var attached = ZipFile.OpenRead(output);
        foreach (var entry in original.Entries.Where(e => e.FullName != SolutionPackage.ManifestFileName))
        {
            using var a = entry.Open(); using var b = attached.GetEntry(entry.FullName)!.Open();
            using var aa = new MemoryStream(); using var bb = new MemoryStream(); a.CopyTo(aa); b.CopyTo(bb);
            Assert.Equal(aa.ToArray(), bb.ToArray());
        }
        Assert.Single(attached.Entries, e => e.FullName == SolutionPackage.ManifestFileName);
        Assert.Equal(ManifestXml.Write(SolutionPackage.Read(Magnus)), ManifestXml.Write(SolutionPackage.Read(output)));
        Assert.Throws<IOException>(() => SolutionPackage.Attach(Magnus, temp.Path, output, false));
    }
    [Fact]
    public void CanReplaceZipInPlaceExplicitly()
    {
        using var temp = new TemporaryDirectory(); var zip = Path.Combine(temp.Path, "test.zip"); File.Copy(Magnus, zip);
        var manifest = SolutionPackage.Read(zip);
        SolutionPackage.Attach(zip, manifest, zip, true);
        Assert.Single(SolutionPackage.Read(zip).Assemblies);
    }
    [Fact]
    public void OlderManifestDoesNotDeleteCustomApis()
    {
        using var original = new MemoryStream(ManifestXml.Write(SolutionPackage.Read(Magnus)));
        var doc = ManifestXml.Load(original);
        doc.Root!.Elements().Single(e => e.Name.LocalName == "keepCustomAPIs_x0040_").Remove();
        var source = ManifestXml.Read(doc);
        Assert.Null(source.CustomApis);
        var target = new ExtendedManifest { CustomApis = [new(Guid.NewGuid(), "api")] };
        Assert.Empty(Reconciliation.Compare(source, target, "pre-import").Actions);
        using var written = new MemoryStream(ManifestXml.Write(source)); Assert.Null(ManifestXml.Read(written).CustomApis);
    }
    [Theory]
    [InlineData("<!DOCTYPE x [<!ENTITY y SYSTEM 'file:///etc/passwd'>]><x>&y;</x>")]
    [InlineData("<unknown />")]
    public void RejectsUnsafeOrInvalidXml(string xml)
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(xml));
        Assert.ThrowsAny<Exception>(() => ManifestXml.Read(stream));
    }
    [Fact]
    public void RejectsUnknownFieldsAndDuplicates()
    {
        using var stream = new MemoryStream(ManifestXml.Write(SolutionPackage.Read(Magnus)));
        var doc = ManifestXml.Load(stream); doc.Root!.Add(new XElement("unknown", "value"));
        Assert.Throws<InvalidDataException>(() => ManifestXml.Read(doc));
        var m = SolutionPackage.Read(Magnus); m.Assemblies.Add(m.Assemblies[0]);
        Assert.Throws<InvalidDataException>(() => m.Validate());
    }
    [Fact]
    public void RejectsDuplicateZipManifestEntries()
    {
        using var temp = new TemporaryDirectory(); var zip = Path.Combine(temp.Path, "test.zip"); File.Copy(Magnus, zip);
        using (var archive = ZipFile.Open(zip, ZipArchiveMode.Update)) archive.CreateEntry("ExtendedSolution.xml");
        Assert.Throws<InvalidDataException>(() => SolutionPackage.Read(zip));
    }
    [Fact]
    public void ParserRejectsTyposDuplicatesAndMissingValues()
    {
        Assert.Throws<ArgumentException>(() => Cli.Parse(["--environment"], ["environment"]));
        Assert.Throws<ArgumentException>(() => Cli.Parse(["--aply"], ["apply"]));
        Assert.Throws<ArgumentException>(() => Cli.Parse(["--apply", "--apply"], ["apply"]));
        Assert.Throws<ArgumentException>(() => Cli.Parse(["extra"], []));
        Assert.Null(Cli.Parse(["--apply"], ["apply"])["apply"]);
    }
}

internal sealed class TemporaryDirectory : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "solutionextender-" + Guid.NewGuid());
    public TemporaryDirectory() => Directory.CreateDirectory(Path);
    public void Dispose() => Directory.Delete(Path, true);
}
