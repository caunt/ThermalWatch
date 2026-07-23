namespace ThermalWatch.Core;

public readonly record struct NotificationMetadataEvaluation(
    bool IsAccepted,
    NotificationRejectionReason? RejectionReason)
{
    public static NotificationMetadataEvaluation Accepted { get; } = new(IsAccepted: true, RejectionReason: null);

    public static NotificationMetadataEvaluation Reject(NotificationRejectionReason reason) =>
        new(IsAccepted: false, reason);
}
