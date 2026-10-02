using System.IO.Compression;
using System.Text;
using System.Xml.Linq;
using SolutionExtender;
using SolutionExtender.Tool;
using Xunit;

namespace SolutionExtender.Tests;

internal sealed class TemporaryDirectory : IDisposable
{
    public TemporaryDirectory() => Directory.CreateDirectory(Path);

    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "solutionextender-" + Guid.NewGuid());

    public void Dispose() => Directory.Delete(Path, true);
}
