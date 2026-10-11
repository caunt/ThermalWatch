namespace ThermalWatch.Core;

/// <summary>The metadata policy decision and its first rejection reason.</summary>
/// <param name="IsEligible">Whether metadata permits further evaluation.</param>
/// <param name="RejectionReason">The blocking reason, when rejected.</param>
public readonly record struct NotificationMetadataEvaluation(bool IsEligible, NotificationRejectionReason? RejectionReason)
{
    /// <summary>A successful metadata evaluation.</summary>
    public static NotificationMetadataEvaluation Eligible { get; } = new(IsEligible: true, RejectionReason: null);

    /// <summary>Creates a rejected evaluation with the specified policy reason.</summary>
    public static NotificationMetadataEvaluation Reject(NotificationRejectionReason reason)
    {
        return new(IsEligible: false, reason);
    }
}
