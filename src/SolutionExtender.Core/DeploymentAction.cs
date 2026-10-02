namespace SolutionExtender;

/// <summary>Describes one ordered Dataverse mutation in a deployment plan.</summary>
/// <param name="Kind">The mutation kind.</param>
/// <param name="LogicalName">The Dataverse table logical name.</param>
/// <param name="Id">The component or record identifier.</param>
/// <param name="Name">The component name.</param>
/// <param name="StateCode">The desired state code.</param>
/// <param name="StatusCode">The desired status code.</param>
/// <param name="OwnerId">The target user identifier for assignment.</param>
public sealed record DeploymentAction(string Kind, string LogicalName, Guid Id, string Name, int? StateCode = null, int? StatusCode = null, Guid? OwnerId = null);
