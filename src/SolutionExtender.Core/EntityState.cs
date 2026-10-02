using System.Globalization;
using System.Xml;
using System.Xml.Linq;

namespace SolutionExtender;

/// <summary>Records the desired state and status of a Dataverse record.</summary>
/// <param name="Id">The component or record identifier.</param>
/// <param name="LogicalName">The Dataverse table logical name.</param>
/// <param name="StateCode">The desired state code.</param>
/// <param name="StatusCode">The desired status code.</param>
public sealed record EntityState(Guid Id, string LogicalName, int StateCode, int StatusCode);
