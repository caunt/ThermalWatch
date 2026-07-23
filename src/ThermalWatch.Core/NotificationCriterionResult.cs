namespace ThermalWatch.Core;

public sealed record NotificationCriterionResult(
    string Code,
    string Label,
    string Outcome,
    string ActualValue,
    string Requirement,
    string Explanation,
    bool IsBlocking)
{
    internal static NotificationCriterionResult Disabled(string code, string label) =>
        new(
            code,
            label,
            NotificationCriterionOutcomes.Disabled,
            ActualValue: "Not evaluated",
            Requirement: "Disabled by configuration",
            Explanation: "This criterion is disabled.",
            IsBlocking: false);
}
