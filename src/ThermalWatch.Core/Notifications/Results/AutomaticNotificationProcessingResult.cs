namespace ThermalWatch.Core;

/// <summary>The outcome of processing one snapshot for automatic delivery.</summary>
/// <param name="ContinueProcessing">Whether the transport permits processing later snapshots.</param>
/// <param name="Summary">The candidate, rejection, and delivery counts for this pass.</param>
public sealed record AutomaticNotificationProcessingResult(bool ContinueProcessing, AutomaticNotificationProcessingSummary Summary);
