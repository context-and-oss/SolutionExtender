using System.IO.Compression;
using System.Xml.Linq;

namespace SolutionExtender;

/// <summary>Preserves native extended metadata across solution ZIP and sidecar operations.</summary>
public static class SolutionPackage
{
    /// <summary>The root ZIP entry and sidecar filename for extended metadata.</summary>
    public const string ManifestFileName = "ExtendedSolution.xml";

    /// <summary>Reads a solution unique name and managed flag from its ZIP.</summary>
    /// <param name="zip">The solution ZIP path.</param>
    /// <returns>The package's solution identity.</returns>
    public static SolutionIdentity Identity(string zip)
    {
        using var archive = ZipFile.OpenRead(zip);
        using var stream = Required(archive, "solution.xml").Open();
        var root = ManifestXml.Load(stream);
        var manifest = root.Root?.Element("SolutionManifest") ?? throw new InvalidDataException("Missing SolutionManifest.");
        var name = manifest.Element("UniqueName")?.Value;
        var managed = manifest.Element("Managed")?.Value;
        if (string.IsNullOrWhiteSpace(name) || managed is not ("0" or "1"))
        {
            throw new InvalidDataException("Invalid solution name or Managed flag.");
        }

        return new(name, string.Equals(managed, "1", StringComparison.Ordinal));
    }

    /// <summary>Reads and validates native extended metadata.</summary>
    /// <param name="path">The ZIP, XML file, or sidecar directory path.</param>
    /// <returns>The validated metadata.</returns>
    public static ExtendedManifest Read(string path)
    {
        if (Directory.Exists(path))
        {
            path = Path.Combine(path, ManifestFileName);
        }

        if (!path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            using var file = File.OpenRead(path);
            return ManifestXml.Read(file);
        }

        using var archive = ZipFile.OpenRead(path);
        using var stream = Required(archive, ManifestFileName).Open();
        return ManifestXml.Read(stream);
    }

    /// <summary>Writes the package's native metadata to a sidecar directory.</summary>
    /// <param name="zip">The solution ZIP path.</param>
    /// <param name="folder">The destination sidecar directory.</param>
    /// <param name="overwrite">Whether an existing output may be replaced.</param>
    public static void Extract(string zip, string folder, bool overwrite)
    {
        var bytes = ManifestXml.Write(Read(zip));
        Directory.CreateDirectory(folder);
        AtomicWrite(Path.Combine(folder, ManifestFileName), bytes, overwrite);
    }

    /// <summary>Attaches validated metadata to a solution ZIP using atomic output replacement.</summary>
    /// <param name="zip">The solution ZIP path.</param>
    /// <param name="manifestPath">The native manifest file or sidecar directory.</param>
    /// <param name="output">The destination ZIP path.</param>
    /// <param name="overwrite">Whether an existing output may be replaced.</param>
    public static void Attach(string zip, string manifestPath, string output, bool overwrite) => Attach(zip, Read(manifestPath), output, overwrite);

    /// <summary>Attaches validated metadata to a solution ZIP using atomic output replacement.</summary>
    /// <param name="zip">The solution ZIP path.</param>
    /// <param name="manifest">The native extended metadata.</param>
    /// <param name="output">The destination ZIP path.</param>
    /// <param name="overwrite">Whether an existing output may be replaced.</param>
    public static void Attach(string zip, ExtendedManifest manifest, string output, bool overwrite)
    {
        _ = Identity(zip);
        var bytes = ManifestXml.Write(manifest);
        output = Path.GetFullPath(output);
        if (File.Exists(output) && !overwrite)
        {
            throw new IOException($"Output exists: {output}. Use --overwrite.");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        var temp = output + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.Copy(zip, temp);
            using (var archive = ZipFile.Open(temp, ZipArchiveMode.Update))
            {
                foreach (var entry in archive.Entries.Where(e => string.Equals(e.FullName, ManifestFileName, StringComparison.OrdinalIgnoreCase)).ToArray())
                {
                    entry.Delete();
                }

                using var stream = archive.CreateEntry(ManifestFileName, CompressionLevel.Optimal).Open();
                stream.Write(bytes);
            }

            File.Move(temp, output, overwrite);
        }
        finally
        {
            if (File.Exists(temp))
            {
                File.Delete(temp);
            }
        }
    }

    /// <summary>Reads the distinct non-private, non-default saved-query identifiers from an export.</summary>
    /// <param name="zip">The solution ZIP path.</param>
    /// <returns>The distinct view identifiers.</returns>
    public static IEnumerable<Guid> ViewIds(string zip)
    {
        using var archive = ZipFile.OpenRead(zip);
        using var stream = Required(archive, "customizations.xml").Open();
        return ManifestXml.Load(stream).Descendants("savedquery").Where(e => string.Equals(e.Element("isprivate")?.Value, "0", StringComparison.Ordinal) && string.Equals(e.Element("isdefault")?.Value, "0", StringComparison.Ordinal)).Select(e => Guid.Parse(e.Element("savedqueryid")?.Value ?? throw new InvalidDataException("Missing savedqueryid."))).Distinct().ToArray();
    }

    private static ZipArchiveEntry Required(ZipArchive archive, string name)
    {
        var matches = archive.Entries.Where(e => string.Equals(e.FullName, name, StringComparison.OrdinalIgnoreCase)).ToArray();
        return matches.Length == 1 ? matches[0] : throw new InvalidDataException($"Expected exactly one root '{name}' in ZIP; found {matches.Length}.");
    }

    private static void AtomicWrite(string path, byte[] bytes, bool overwrite)
    {
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllBytes(temp, bytes);
            File.Move(temp, path, overwrite);
        }
        finally
        {
            if (File.Exists(temp))
            {
                File.Delete(temp);
            }
        }
    }
}
