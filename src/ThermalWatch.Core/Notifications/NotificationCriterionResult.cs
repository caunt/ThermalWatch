namespace ThermalWatch.Core;

/// <summary>A displayable explanation of one notification criterion.</summary>
/// <param name="Code">The stable criterion identifier.</param>
/// <param name="Label">The human-readable criterion name.</param>
/// <param name="Outcome">The passed, failed, disabled, or unavailable state.</param>
/// <param name="ActualValue">The evaluated measurement or availability state.</param>
/// <param name="Requirement">The condition required for eligibility.</param>
/// <param name="Explanation">The reason for the outcome.</param>
/// <param name="IsBlocking">Whether this result prevents notification.</param>
public sealed record NotificationCriterionResult(string Code, string Label, string Outcome, string ActualValue, string Requirement, string Explanation, bool IsBlocking)
{
    internal static NotificationCriterionResult Disabled(string code, string label)
    {
        return new(
            code,
            label,
            NotificationCriterionOutcomes.Disabled,
            ActualValue: "Not evaluated",
            Requirement: "Disabled by configuration",
            Explanation: "This criterion is disabled.",
            IsBlocking: false
        );
    }
}
