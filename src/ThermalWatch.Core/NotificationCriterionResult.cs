namespace ThermalWatch.Core;

public sealed record NotificationCriterionResult(
    string Code,
    string Label,
    string Outcome,
    string ActualValue,
    string Requirement,
    string Explanation,
    bool IsBlocking);
