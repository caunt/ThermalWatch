namespace ThermalWatch.Telegram;

internal readonly record struct VisibilityFilterResult(
    bool IsAccepted,
    VisibilityRejectionReason? RejectionReason)
{
    public static VisibilityFilterResult Accepted { get; } = new(IsAccepted: true, RejectionReason: null);

    public static VisibilityFilterResult Reject(VisibilityRejectionReason reason) => new(IsAccepted: false, reason);
}
