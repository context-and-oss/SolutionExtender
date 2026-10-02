namespace SolutionExtender;

/// <summary>Contains the ordered actions for one deployment phase.</summary>
/// <param name="Phase">The pre-import or post-import phase.</param>
/// <param name="Actions">The ordered deployment actions.</param>
public sealed record DeploymentPlan(string Phase, IReadOnlyList<DeploymentAction> Actions);
