using System.IO.Compression;
using System.Text;
using System.Xml.Linq;
using SolutionExtender;
using SolutionExtender.Tool;
using Xunit;

namespace SolutionExtender.Tests;

public sealed class ManifestTests : IDisposable
{
    private readonly TemporaryDirectory fixture = new();

    public ManifestTests() => Package = NativeFixture.CreatePackage(fixture.Path, extended: true);

    private string Package
    {
        get;
    }

    public void Dispose() => fixture.Dispose();

    [Fact]
    public void ReadsNativeFixture()
    {
        var m = SolutionPackage.Read(Package);
        Assert.Single(m.Assemblies);
        Assert.Equal(3, m.PluginTypes.Count);
        Assert.Equal(2, m.PluginSteps.Count);
        Assert.Empty(m.PluginImages);
        Assert.Equal(7, m.WebResources.Count);
        Assert.Empty(m.Workflows);
        Assert.Single(m.CustomApis!);
        Assert.Equal(7, m.States.Count);
        Assert.All(m.States, s =>
        {
            Assert.Equal("savedquery", s.LogicalName);
            Assert.Equal(0, s.StateCode);
            Assert.Equal(1, s.StatusCode);
        });
        Assert.Equal(new SolutionIdentity("Example", false), SolutionPackage.Identity(Package));
    }

    [Fact]
    public void XmlRoundTripIsStableAndPreservesOwners()
    {
        var m = SolutionPackage.Read(Package);
        m.Workflows.Add(new(Guid.NewGuid(), "Flow & <test>", "user@example.org"));
        m.States.Add(new(m.Workflows[0].Id, "workflow", 1, 2));
        var bytes = ManifestXml.Write(m);
        using var stream = new MemoryStream(bytes);
        var read = ManifestXml.Read(stream);
        Assert.Equal(m.Workflows, read.Workflows);
        Assert.Equal(bytes, ManifestXml.Write(read));
    }

    [Fact]
    public void NewExportsUseOurVersionedContract()
    {
        var id = Guid.NewGuid();
        var m = new ExtendedManifest
        {
            Assemblies = [new(id, "Plugins")],
            CustomApis = [],
            States = [new(Guid.NewGuid(), "savedquery", 0, 1)],
        };
        var bytes = ManifestXml.Write(m);
        var text = Encoding.UTF8.GetString(bytes);
        Assert.DoesNotContain("Daxif", text, StringComparison.Ordinal);
        Assert.DoesNotContain("FSharp", text, StringComparison.Ordinal);
        Assert.DoesNotContain("m_Item", text, StringComparison.Ordinal);
        using var stream = new MemoryStream(bytes);
        var doc = ManifestXml.Load(stream);
        Assert.Equal(XName.Get("ExtendedSolution", ManifestXml.NamespaceUri), doc.Root!.Name);
        Assert.Equal("1", doc.Root.Attribute("version")!.Value);
        var read = ManifestXml.Read(doc);
        Assert.Equal(m.Assemblies, read.Assemblies);
        Assert.Empty(read.CustomApis!);
    }

    [Fact]
    public void ExtractPreservesNativeManifest()
    {
        using var temp = new TemporaryDirectory();
        SolutionPackage.Extract(Package, temp.Path, false);
        var text = File.ReadAllText(Path.Combine(temp.Path, SolutionPackage.ManifestFileName));
        Assert.Contains(ManifestXml.NamespaceUri, text, StringComparison.Ordinal);
        Assert.DoesNotContain("Daxif", text, StringComparison.Ordinal);
        Assert.Single(SolutionPackage.Read(temp.Path).CustomApis!);
    }

    [Theory]
    [InlineData("version", "2")]
    [InlineData("unknown", "value")]
    public void RejectsUnsupportedVersionAndAttributes(string attribute, string value)
    {
        using var stream = new MemoryStream(ManifestXml.Write(new ExtendedManifest()));
        var doc = ManifestXml.Load(stream);
        doc.Root!.SetAttributeValue(attribute, value);
        Assert.Throws<InvalidDataException>(() => ManifestXml.Read(doc));
    }

    [Fact]
    public void RejectsMissingOrDuplicateNativeSections()
    {
        using var stream = new MemoryStream(ManifestXml.Write(new ExtendedManifest()));
        var doc = ManifestXml.Load(stream);
        var section = doc.Root!.Element(XName.Get("Assemblies", ManifestXml.NamespaceUri))!;
        doc.Root.Add(new XElement(section));
        Assert.Throws<InvalidDataException>(() => ManifestXml.Read(doc));
        doc.Root.Elements(section.Name).Remove();
        Assert.Throws<InvalidDataException>(() => ManifestXml.Read(doc));
    }

    [Fact]
    public void NativeManifestPreservesUncapturedApiScope()
    {
        using var stream = new MemoryStream(ManifestXml.Write(new ExtendedManifest()));
        Assert.Null(ManifestXml.Read(stream).CustomApis);
    }

    [Fact]
    public void PacSidecarAttachKeepsEveryOtherZipEntry()
    {
        using var temp = new TemporaryDirectory();
        SolutionPackage.Extract(Package, temp.Path, false);
        var output = Path.Combine(temp.Path, "packed.zip");
        SolutionPackage.Attach(Package, temp.Path, output, false);
        using var original = ZipFile.OpenRead(Package);
        using var attached = ZipFile.OpenRead(output);
        foreach (var entry in original.Entries.Where(e => !string.Equals(e.FullName, SolutionPackage.ManifestFileName, StringComparison.Ordinal)))
        {
            using var a = entry.Open();
            using var b = attached.GetEntry(entry.FullName)!.Open();
            using var aa = new MemoryStream();
            using var bb = new MemoryStream();
            a.CopyTo(aa);
            b.CopyTo(bb);
            Assert.Equal(aa.ToArray(), bb.ToArray());
        }

        Assert.Single(attached.Entries, e => string.Equals(e.FullName, SolutionPackage.ManifestFileName, StringComparison.Ordinal));
        Assert.Equal(ManifestXml.Write(SolutionPackage.Read(Package)), ManifestXml.Write(SolutionPackage.Read(output)));
        Assert.Throws<IOException>(() => SolutionPackage.Attach(Package, temp.Path, output, false));
    }

    [Fact]
    public void FreshExportCanBeExtendedUnpackedAndReattachedUsingOnlyOurFormat()
    {
        using var temp = new TemporaryDirectory();
        var fresh = NativeFixture.CreatePackage(temp.Path);
        Assert.Throws<InvalidDataException>(() => SolutionPackage.Read(fresh));
        var extended = Path.Combine(temp.Path, "extended.zip");
        SolutionPackage.Attach(fresh, NativeFixture.Manifest(), extended, false);
        var sidecar = Path.Combine(temp.Path, "unpacked");
        SolutionPackage.Extract(extended, sidecar, false);
        var repacked = Path.Combine(temp.Path, "repacked.zip");
        SolutionPackage.Attach(fresh, sidecar, repacked, false);
        Assert.Equal(ManifestXml.Write(NativeFixture.Manifest()), ManifestXml.Write(SolutionPackage.Read(repacked)));
        Assert.Empty(Reconciliation.Compare(SolutionPackage.Read(extended), SolutionPackage.Read(repacked), "pre-import").Actions);
        Assert.Empty(Reconciliation.Compare(SolutionPackage.Read(extended), SolutionPackage.Read(repacked), "post-import").Actions);
    }

    [Fact]
    public void CanReplaceZipInPlaceExplicitly()
    {
        using var temp = new TemporaryDirectory();
        var zip = Path.Combine(temp.Path, "test.zip");
        File.Copy(Package, zip);
        var manifest = SolutionPackage.Read(zip);
        SolutionPackage.Attach(zip, manifest, zip, true);
        Assert.Single(SolutionPackage.Read(zip).Assemblies);
    }

    [Fact]
    public void UncapturedApiScopeDoesNotDeleteCustomApis()
    {
        using var archive = ZipFile.OpenRead(Package);
        using var original = archive.GetEntry(SolutionPackage.ManifestFileName)!.Open();
        var doc = ManifestXml.Load(original);
        doc.Root!.Elements().Single(e => string.Equals(e.Name.LocalName, "CustomApis", StringComparison.Ordinal)).Remove();
        var source = ManifestXml.Read(doc);
        Assert.Null(source.CustomApis);
        var target = new ExtendedManifest
        {
            CustomApis = [new(Guid.NewGuid(), "api")],
        };
        Assert.Empty(Reconciliation.Compare(source, target, "pre-import").Actions);
        using var written = new MemoryStream(ManifestXml.Write(source));
        Assert.Null(ManifestXml.Read(written).CustomApis);
    }

    [Theory]
    [InlineData("<!DOCTYPE x [<!ENTITY y SYSTEM 'file:///etc/passwd'>]><x>&y;</x>")]
    [InlineData("<unknown />")]
    [InlineData("<Domain.ExtendedSolution xmlns=\"http://schemas.datacontract.org/2004/07/DG.Daxif.Modules.Solution\" />")]
    public void RejectsUnsafeOrInvalidXml(string xml)
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(xml));
        Assert.ThrowsAny<Exception>(() => ManifestXml.Read(stream));
    }

    [Fact]
    public void RejectsUnknownFieldsAndDuplicates()
    {
        using var stream = new MemoryStream(ManifestXml.Write(SolutionPackage.Read(Package)));
        var doc = ManifestXml.Load(stream);
        doc.Root!.Add(new XElement("unknown", "value"));
        Assert.Throws<InvalidDataException>(() => ManifestXml.Read(doc));
        var m = SolutionPackage.Read(Package);
        m.Assemblies.Add(m.Assemblies[0]);
        Assert.Throws<InvalidDataException>(() => m.Validate());
    }

    [Fact]
    public void RejectsDuplicateZipManifestEntries()
    {
        using var temp = new TemporaryDirectory();
        var zip = Path.Combine(temp.Path, "test.zip");
        File.Copy(Package, zip);
        using (var archive = ZipFile.Open(zip, ZipArchiveMode.Update))
        {
            archive.CreateEntry("ExtendedSolution.xml");
        }

        Assert.Throws<InvalidDataException>(() => SolutionPackage.Read(zip));
    }

    [Fact]
    public void CliOfflineCommandsRoundTripNativeMetadata()
    {
        using var temp = new TemporaryDirectory();
        var fresh = NativeFixture.CreatePackage(temp.Path);
        var manifest = Path.Combine(temp.Path, SolutionPackage.ManifestFileName);
        File.WriteAllBytes(manifest, ManifestXml.Write(NativeFixture.Manifest()));
        var extended = Path.Combine(temp.Path, "extended.zip");
        Assert.Equal(0, Cli.Run(["attach", "--zip", fresh, "--manifest", manifest, "--output", extended]));
        var sidecar = Path.Combine(temp.Path, "sidecar");
        Assert.Equal(0, Cli.Run(["extract", "--zip", extended, "--folder", sidecar]));
        var plan = Path.Combine(temp.Path, "plan.json");
        Assert.Equal(0, Cli.Run(["plan", "--source", extended, "--target", sidecar, "--phase", "post-import", "--output", plan]));
        Assert.Contains("\"Actions\": []", File.ReadAllText(plan), StringComparison.Ordinal);
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
