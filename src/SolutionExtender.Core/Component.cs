using System.Globalization;
using System.Xml;
using System.Xml.Linq;

namespace SolutionExtender;

/// <summary>Identifies a component to retain, with an optional workflow owner domain name.</summary>
/// <param name="Id">The component or record identifier.</param>
/// <param name="Name">The component name.</param>
/// <param name="Owner">The optional workflow owner domain name.</param>
public sealed record Component(Guid Id, string Name, string? Owner = null);
