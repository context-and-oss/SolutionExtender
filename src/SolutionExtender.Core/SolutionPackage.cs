using System.IO.Compression;
using System.Xml.Linq;

namespace SolutionExtender;

public sealed record SolutionIdentity(string UniqueName, bool Managed);

public static class SolutionPackage
{
    public const string ManifestFileName = "ExtendedSolution.xml";
    public static SolutionIdentity Identity(string zip)
    {
        using var archive = ZipFile.OpenRead(zip);
        using var stream = Required(archive, "solution.xml").Open();
        var root = ManifestXml.Load(stream);
        var manifest = root.Root?.Element("SolutionManifest") ?? throw new InvalidDataException("Missing SolutionManifest.");
        var name = manifest.Element("UniqueName")?.Value;
        var managed = manifest.Element("Managed")?.Value;
        if (string.IsNullOrWhiteSpace(name) || managed is not ("0" or "1"))
            throw new InvalidDataException("Invalid solution name or Managed flag.");
        return new(name, managed == "1");
    }

    public static ExtendedManifest Read(string path)
    {
        if (Directory.Exists(path)) path = Path.Combine(path, ManifestFileName);
        if (!path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            using var file = File.OpenRead(path);
            return ManifestXml.Read(file);
        }
        using var archive = ZipFile.OpenRead(path);
        using var stream = Required(archive, ManifestFileName).Open();
        return ManifestXml.Read(stream);
    }

    public static void Extract(string zip, string folder, bool overwrite)
    {
        var bytes = ManifestXml.Write(Read(zip));
        Directory.CreateDirectory(folder);
        AtomicWrite(Path.Combine(folder, ManifestFileName), bytes, overwrite);
    }

    public static void Attach(string zip, string manifestPath, string output, bool overwrite)
        => Attach(zip, Read(manifestPath), output, overwrite);

    public static void Attach(string zip, ExtendedManifest manifest, string output, bool overwrite)
    {
        _ = Identity(zip);
        var bytes = ManifestXml.Write(manifest);
        output = Path.GetFullPath(output);
        if (File.Exists(output) && !overwrite) throw new IOException($"Output exists: {output}. Use --overwrite.");
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        var temp = output + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.Copy(zip, temp);
            using (var archive = ZipFile.Open(temp, ZipArchiveMode.Update))
            {
                foreach (var entry in archive.Entries.Where(e => string.Equals(e.FullName, ManifestFileName, StringComparison.OrdinalIgnoreCase)).ToArray())
                    entry.Delete();
                using var stream = archive.CreateEntry(ManifestFileName, CompressionLevel.Optimal).Open();
                stream.Write(bytes);
            }
            File.Move(temp, output, overwrite);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }

    public static IEnumerable<Guid> ViewIds(string zip)
    {
        using var archive = ZipFile.OpenRead(zip);
        using var stream = Required(archive, "customizations.xml").Open();
        return ManifestXml.Load(stream).Descendants("savedquery")
            .Where(e => e.Element("isprivate")?.Value == "0" && e.Element("isdefault")?.Value == "0")
            .Select(e => Guid.Parse(e.Element("savedqueryid")?.Value ?? throw new InvalidDataException("Missing savedqueryid.")))
            .Distinct().ToArray();
    }

    private static ZipArchiveEntry Required(ZipArchive archive, string name)
    {
        var matches = archive.Entries.Where(e => string.Equals(e.FullName, name, StringComparison.OrdinalIgnoreCase)).ToArray();
        return matches.Length == 1 ? matches[0] : throw new InvalidDataException($"Expected exactly one root '{name}' in ZIP; found {matches.Length}.");
    }

    private static void AtomicWrite(string path, byte[] bytes, bool overwrite)
    {
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.WriteAllBytes(temp, bytes); File.Move(temp, path, overwrite); }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
}
