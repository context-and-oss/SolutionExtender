using System.IO.Compression;
using System.Xml.Linq;

namespace SolutionExtender;

/// <summary>Identifies the solution and its managed status from the package manifest.</summary>
/// <param name="UniqueName">The solution unique name.</param>
/// <param name="Managed">Whether the solution is managed.</param>
public sealed record SolutionIdentity(string UniqueName, bool Managed);
