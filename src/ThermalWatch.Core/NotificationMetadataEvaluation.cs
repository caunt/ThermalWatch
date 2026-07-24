namespace ThermalWatch.Core;

public readonly record struct NotificationMetadataEvaluation(
    bool IsEligible,
    NotificationRejectionReason? RejectionReason)
{
    public static NotificationMetadataEvaluation Eligible { get; } = new(IsEligible: true, RejectionReason: null);

    public static NotificationMetadataEvaluation Reject(NotificationRejectionReason reason) =>
        new(IsEligible: false, reason);
}
