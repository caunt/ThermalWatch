using System.Collections.Immutable;
using System.Globalization;
using System.Runtime.CompilerServices;

namespace ThermalWatch.Core;

/// <summary>Prepares notification candidates and owns in-memory startup and delivery episode suppression.</summary>
public sealed class NotificationCandidateEngine(
    NotificationOptions options,
    GibsClient gibsClient,
    NearbyFeatureClient nearbyFeatureClient,
    TimeProvider timeProvider,
    FirmsHistoryStore? historyStore = null
)
{
    private const int MaximumCandidateNearbyFeatures = 5;
    private const int MaximumConcurrentEligibleClusterEvaluations = 2;
    private const int MaximumDiagnosticNearbyFeatures = 25;
    private readonly ConditionalWeakTable<AnomalySnapshot, ClusteredSnapshot> _clustersBySnapshot = [];
    private readonly ConditionalWeakTable<NotificationCluster, SelectedPreview> _previewsByCluster = [];
    private readonly NotificationEpisodeHistory _startupIncidentHistory = new(options.ClusterRadiusKilometers, options.ClusterTimeWindow, options.EpisodeRetention);
    private readonly NotificationEpisodeHistory _deliveryHistory = new(options.ClusterRadiusKilometers, options.ClusterTimeWindow, options.EpisodeRetention);
    private bool _firstReadySnapshot = true;

    private bool IsPreviewRequired =>
        options.Visibility.Enabled && options.Visibility.RequirePreview;

    /// <summary>Evaluates and orders eligible clusters from an immutable publication.</summary>
    public async Task<EligibleNotificationClusters> GetEligibleNotificationClustersAsync(AnomalySnapshot snapshot, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ImmutableArray<NotificationCluster> clusters = GetClusters(snapshot);
        var eligible = new NotificationClusterSummary?[clusters.Length];
        await Parallel.ForEachAsync(
            Enumerable.Range(start: 0, clusters.Length),
            new ParallelOptions
            {
                CancellationToken = cancellationToken,
                MaxDegreeOfParallelism = MaximumConcurrentEligibleClusterEvaluations
            },
            async (index, token) => eligible[index] = await EvaluateEligibleClusterAsync(clusters[index], token).ConfigureAwait(continueOnCapturedContext: false)
        ).ConfigureAwait(continueOnCapturedContext: false);

        ImmutableArray<NotificationClusterSummary> ordered =
        [
            .. OrderByNotificationPriority(
                eligible.OfType<NotificationClusterSummary>(),
                static candidate => candidate.TotalFrpMegawatts,
                static candidate => candidate.FrpMegawatts,
                static candidate => candidate.DetectionCount,
                static candidate => candidate.ClusterDiameterKilometers,
                static candidate => candidate.AcquiredAtUtc,
                static candidate => candidate.ClusterId
            )
        ];

        return new(snapshot.GeneratedAtUtc, clusters.Length, ordered.Length, ordered);
    }

    /// <summary>Explains the selected observation's cluster, or returns null when it is absent.</summary>
    public async Task<NotificationDiagnostic?> GetNotificationDiagnosticAsync(AnomalySnapshot snapshot, string anomalyId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        DiagnosticTarget? target =
            FindDiagnosticTarget(snapshot, anomalyId);

        if (target is not { } found)
            return null;

        NotificationCluster cluster = found.Cluster;

        DiagnosticPolicyEvaluation policy = await EvaluateDiagnosticPolicyAsync(cluster, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);

        ImmutableArray<NearbyFeature> nearbyFeatures = await nearbyFeatureClient.FindNearbyAsync(found.SelectedAnomaly, MaximumDiagnosticNearbyFeatures, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);

        return new(
            anomalyId,
            cluster.Id,
            cluster.Representative.Id,
            [.. cluster.Members.Select(member => member.Id)],
            cluster.Members.Length,
            policy.ClusterDiameterKilometers,
            cluster.TotalFrpMegawatts,
            !policy.Criteria.Any(criterion => criterion.IsBlocking),
            policy.Criteria,
            policy.PreviewBaseSource,
            nearbyFeatures
        );
    }

    /// <summary>Prepares the requested highest-priority candidates without automatic episode suppression.</summary>
    public async Task<ManualNotificationCandidateSelection> PrepareManualCandidatesAsync(AnomalySnapshot snapshot, int requestedClusterCount, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ImmutableArray<NotificationCluster> clusters = GetClusters(snapshot);
        var eligibleCandidates = new List<PreparedNotificationCandidate>();

        foreach (NotificationCluster cluster in clusters)
        {
            NotificationClusterEvaluation evaluation = await EvaluateClusterAsync(cluster, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);

            if (!evaluation.IsEligible)
                continue;

            NotificationPreviewSelection previewSelection = SelectPreview(cluster);

            GibsPreview preview = await gibsClient.GetPreviewAsync(cluster.Representative, previewSelection.Dimensions, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);

            if (!preview.IsAvailable && IsPreviewRequired)
                continue;

            eligibleCandidates.Add(new(cluster, preview, previewSelection, evaluation.LandCoverResult?.FormattingSummary, []));
        }

        PreparedNotificationCandidate[] selectedWithoutNearby =
        [
            .. OrderByNotificationPriority(
                eligibleCandidates,
                static candidate => candidate.Cluster.TotalFrpMegawatts,
                static candidate => candidate.Cluster.Representative.FrpMegawatts,
                static candidate => candidate.Cluster.Members.Length,
                static candidate => candidate.PreviewSelection.ClusterDiameterKilometers,
                static candidate => candidate.Cluster.Representative.AcquiredAtUtc,
                static candidate => candidate.Cluster.Id
            )
                .Take(requestedClusterCount)
        ];

        ImmutableArray<PreparedNotificationCandidate>.Builder selectedCandidates =
            ImmutableArray.CreateBuilder<PreparedNotificationCandidate>(selectedWithoutNearby.Length);

        foreach (PreparedNotificationCandidate candidate in selectedWithoutNearby)
        {
            NearbyMappedContext context = await FindCandidateContextAsync(candidate.Cluster.Representative, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);

            selectedCandidates.Add(candidate with { NearbyFeatures = context.NearbyFeatures, SettlementName = context.SettlementName });
        }

        return new(eligibleCandidates.Count, selectedCandidates.MoveToImmutable());
    }

    /// <summary>Processes a publication, recording episode delivery only after successful transport.</summary>
    public async Task<AutomaticNotificationProcessingResult> ProcessAutomaticNotificationsAsync(
        AnomalySnapshot snapshot,
        Func<PreparedNotificationCandidate, CancellationToken, Task<NotificationDeliveryOutcome>> deliverAsync,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(deliverAsync);

        var summary = new AutomaticProcessingSummaryBuilder();

        if (!snapshot.IsReady)
            return new(ContinueProcessing: true, summary.Build());

        DateTimeOffset now = timeProvider.GetUtcNow();
        _startupIncidentHistory.Expire(now);
        _deliveryHistory.Expire(now);

        ImmutableArray<NotificationCluster> clusters = GetClusters(snapshot);
        summary.ActiveClusterCount = clusters.Length;

        if (options.HistoricalFrpFilterEnabled && (historyStore is null || !historyStore.Current.IsReady))
        {
            summary.EvaluatedClusterCount = clusters.Length;

            for (int index = 0; index < clusters.Length; index++)
                summary.Reject(NotificationRejectionReason.HistoryUnavailable);

            return new(ContinueProcessing: true, summary.Build());
        }

        bool isFirstReadySnapshot = _firstReadySnapshot;
        _firstReadySnapshot = false;

        if (isFirstReadySnapshot && !options.SendExistingOnStartup)
        {
            await PrimeStartupIncidentsAsync(clusters, summary, now, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);

            return new(ContinueProcessing: true, summary.Build());
        }

        return await ProcessClustersAsync(clusters, summary, now, deliverAsync, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
    }

    private static NotificationCriterionResult ExplainPreview(GibsPreview preview)
    {
        if (!preview.IsAvailable)
        {
            return new(
                Code: "exact-preview",
                Label: "Exact-date preview",
                NotificationCriterionOutcomes.Unavailable,
                ActualValue: "Not available",
                Requirement: "Exact acquisition-date imagery",
                Explanation: "No exact-date preview is currently available; automatic processing reevaluates the active cluster after later snapshot publications.",
                IsBlocking: true
            );
        }

        string actual = preview.BaseSource is { } source
            ? $"{source.Instrument} {source.Satellite} contextual base"
            : "Exact-date imagery available";

        return new(
            Code: "exact-preview",
            Label: "Exact-date preview",
            NotificationCriterionOutcomes.Passed,
            actual,
            Requirement: "Exact acquisition-date imagery",
            Explanation: "An exact-date preview is currently available.",
            IsBlocking: false
        );
    }

    private static IOrderedEnumerable<TCandidateType> OrderByNotificationPriority<TCandidateType>(
        IEnumerable<TCandidateType> candidates,
        Func<TCandidateType, double?> totalFrpSelector,
        Func<TCandidateType, double?> peakFrpSelector,
        Func<TCandidateType, int> detectionCountSelector,
        Func<TCandidateType, double> diameterSelector,
        Func<TCandidateType, DateTimeOffset> acquisitionSelector,
        Func<TCandidateType, string> clusterIdSelector
    )
    {
        return candidates
            .OrderByDescending(candidate => totalFrpSelector(candidate).HasValue)
            .ThenByDescending(totalFrpSelector)
            .ThenByDescending(candidate => peakFrpSelector(candidate).HasValue)
            .ThenByDescending(peakFrpSelector)
            .ThenByDescending(detectionCountSelector)
            .ThenByDescending(diameterSelector)
            .ThenByDescending(acquisitionSelector)
            .ThenBy(clusterIdSelector, StringComparer.Ordinal);
    }

    private async Task<NotificationCandidateReadiness?> EvaluateCandidateReadinessAsync(
        NotificationCluster cluster,
        AutomaticProcessingSummaryBuilder summary,
        bool includeOptionalPreview,
        CancellationToken cancellationToken
    )
    {
        summary.EvaluatedClusterCount++;

        NotificationClusterEvaluation evaluation = await EvaluateClusterAsync(cluster, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);

        summary.RecordLandCover(evaluation.LandCoverResult);

        if (!evaluation.IsEligible)
        {
            if (evaluation.RejectionReason is { } rejectionReason)
                summary.Reject(rejectionReason);
            else
                summary.RejectedClusterCount++;

            return null;
        }

        NotificationPreviewSelection previewSelection = SelectPreview(cluster);

        GibsPreview preview = IsPreviewRequired || includeOptionalPreview
            ? await gibsClient.GetPreviewAsync(cluster.Representative, previewSelection.Dimensions, cancellationToken).ConfigureAwait(continueOnCapturedContext: false)
            : GibsPreview.Unavailable;

        if (!preview.IsAvailable && IsPreviewRequired)
        {
            summary.Reject(NotificationRejectionReason.PreviewUnavailable);

            return null;
        }

        return new(preview, previewSelection, evaluation.LandCoverResult?.FormattingSummary);
    }

    private async Task<NotificationClusterEvaluation> EvaluateClusterAsync(NotificationCluster cluster, CancellationToken cancellationToken)
    {
        NotificationMetadataEvaluation visibility = NotificationPolicy.EvaluateMetadata(cluster, options.Visibility);

        if (!visibility.IsEligible)
            return new(IsEligible: false, visibility.RejectionReason, LandCoverResult: null);

        NotificationCriterionResult historicalFrp = NotificationHistoricalFrpPolicy.Explain(cluster, historyStore?.Current, options);

        if (historicalFrp.IsBlocking)
        {
            NotificationRejectionReason reason = string.Equals(historicalFrp.Outcome, NotificationCriterionOutcomes.Unavailable, StringComparison.Ordinal)
                ? NotificationRejectionReason.HistoryUnavailable
                : NotificationRejectionReason.HistoricalFrpNotHigher;

            return new(IsEligible: false, reason, LandCoverResult: null);
        }

        if (!options.LandCover.Enabled)
            return NotificationClusterEvaluation.Eligible;

        NotificationLandCoverResult landCover = await NotificationLandCoverPolicy.EvaluateAsync(cluster, options.LandCover, gibsClient, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);

        return new(landCover.Decision != NotificationLandCoverDecision.Suppressed, RejectionReason: null, landCover);
    }

    private async Task<DiagnosticPolicyEvaluation> EvaluateDiagnosticPolicyAsync(NotificationCluster cluster, CancellationToken cancellationToken)
    {
        var criteria = NotificationPolicy.ExplainMetadata(cluster, options.Visibility).ToBuilder();
        criteria.Add(NotificationHistoricalFrpPolicy.Explain(cluster, historyStore?.Current, options));

        if (!options.LandCover.Enabled)
        {
            criteria.Add(NotificationCriterionResult.Disabled(code: "land-cover", label: "Land-cover filter"));
        }
        else
        {
            NotificationLandCoverResult landCover = await NotificationLandCoverPolicy.EvaluateAsync(cluster, options.LandCover, gibsClient, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);

            criteria.Add(ExplainLandCover(landCover));
        }

        NotificationPreviewSelection previewSelection = SelectPreview(cluster);

        GibsPreviewSource? previewBaseSource = await ExplainDiagnosticPreviewAsync(cluster, previewSelection, criteria, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);

        return new(criteria.ToImmutable(), previewBaseSource, previewSelection.ClusterDiameterKilometers);
    }

    private async ValueTask<NotificationClusterSummary?> EvaluateEligibleClusterAsync(NotificationCluster cluster, CancellationToken cancellationToken)
    {
        NotificationClusterEvaluation evaluation = await EvaluateClusterAsync(cluster, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);

        if (!evaluation.IsEligible)
            return null;

        NotificationPreviewSelection previewSelection = SelectPreview(cluster);

        if (IsPreviewRequired)
        {
            GibsPreview preview = await gibsClient.GetPreviewAsync(cluster.Representative, previewSelection.Dimensions, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);

            if (!preview.IsAvailable)
                return null;
        }

        return NotificationClusterSummary.FromCluster(cluster, previewSelection.ClusterDiameterKilometers);
    }

    private async Task<GibsPreviewSource?> ExplainDiagnosticPreviewAsync(
        NotificationCluster cluster,
        NotificationPreviewSelection previewSelection,
        ImmutableArray<NotificationCriterionResult>.Builder criteria,
        CancellationToken cancellationToken
    )
    {
        if (!IsPreviewRequired)
        {
            criteria.Add(NotificationCriterionResult.Disabled(code: "exact-preview", label: "Exact-date preview"));

            return null;
        }

        GibsPreview preview = await gibsClient.GetPreviewAsync(cluster.Representative, previewSelection.Dimensions, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);

        criteria.Add(ExplainPreview(preview));

        return preview.BaseSource;
    }

    private NotificationCriterionResult ExplainLandCover(NotificationLandCoverResult landCover)
    {
        string actual = landCover.VegetationPercent is { } vegetationPercent
            ? $"{NotificationPolicy.FormatNumber(vegetationPercent)}% vegetation"
                + (landCover.LandCoverYear is { } year
                    ? $" ({year.ToString(CultureInfo.InvariantCulture)})"
                    : string.Empty)
            : "Not available";

        string requirement = $"Suppress at least {NotificationPolicy.FormatNumber(options.LandCover.VegetationPercentThreshold)}% vegetation unless built-up proximity or an enabled exception retains it";

        return landCover.Decision switch
        {
            NotificationLandCoverDecision.Retained => new(
                Code: "land-cover",
                Label: "Land-cover filter",
                NotificationCriterionOutcomes.Passed,
                actual,
                requirement,
                landCover.Reason,
                IsBlocking: false
            ),
            NotificationLandCoverDecision.Suppressed => new(
                Code: "land-cover",
                Label: "Land-cover filter",
                NotificationCriterionOutcomes.Failed,
                actual,
                requirement,
                landCover.Reason,
                IsBlocking: true
            ),
            NotificationLandCoverDecision.Unavailable => new(
                Code: "land-cover",
                Label: "Land-cover filter",
                NotificationCriterionOutcomes.Unavailable,
                actual,
                requirement,
                $"{landCover.Reason}; the policy fails open.",
                IsBlocking: false
            ),
            _ => new(
                Code: "land-cover",
                Label: "Land-cover filter",
                NotificationCriterionOutcomes.Unavailable,
                actual,
                requirement,
                $"{landCover.Reason}; the policy fails open.",
                IsBlocking: false
            )
        };
    }

    private async Task<NearbyMappedContext> FindCandidateContextAsync(Anomaly anomaly, CancellationToken cancellationToken)
    {
        NearbyMappedContext context = await nearbyFeatureClient.FindContextAsync(anomaly, MaximumDiagnosticNearbyFeatures, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);

        return context with
        {
            NearbyFeatures =
            [
                .. Enumerable.Range(start: 0, context.NearbyFeatures.Length)
                    .Select(index => context.NearbyFeatures[index])
                    .DistinctBy(feature => feature.Name, StringComparer.OrdinalIgnoreCase)
                    .Take(MaximumCandidateNearbyFeatures)
            ]
        };
    }

    private DiagnosticTarget? FindDiagnosticTarget(AnomalySnapshot snapshot, string anomalyId)
    {
        Anomaly? selectedAnomaly = snapshot.Anomalies.FirstOrDefault(candidate => candidate.Id.Equals(anomalyId, StringComparison.Ordinal));

        if (selectedAnomaly is null)
            return null;

        NotificationCluster? cluster = GetClusters(snapshot)
            .FirstOrDefault(candidate => candidate.Members.Any(member => member.Id.Equals(anomalyId, StringComparison.Ordinal)));

        return cluster is null ? null : new(selectedAnomaly, cluster);
    }

    private ImmutableArray<NotificationCluster> GetClusters(AnomalySnapshot snapshot)
    {
        return _clustersBySnapshot.GetValue(snapshot, value => new(value, options.ClusterRadiusKilometers, options.ClusterTimeWindow)).Clusters;
    }

    private async Task<PreparedNotificationCandidate?> PrepareAutomaticCandidateAsync(NotificationCluster cluster, AutomaticProcessingSummaryBuilder summary, CancellationToken cancellationToken)
    {
        NotificationCandidateReadiness? readiness = await EvaluateCandidateReadinessAsync(cluster, summary, includeOptionalPreview: true, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);

        if (readiness is null)
            return null;

        NearbyMappedContext context = await FindCandidateContextAsync(cluster.Representative, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);

        return new(cluster, readiness.Preview, readiness.PreviewSelection, readiness.LandCoverSummary, context.NearbyFeatures)
        {
            SettlementName = context.SettlementName
        };
    }

    private async Task PrimeStartupIncidentsAsync(
        ImmutableArray<NotificationCluster> clusters,
        AutomaticProcessingSummaryBuilder summary,
        DateTimeOffset now,
        CancellationToken cancellationToken
    )
    {
        foreach (NotificationCluster cluster in clusters)
        {
            cancellationToken.ThrowIfCancellationRequested();

            NotificationCandidateReadiness? readiness = await EvaluateCandidateReadinessAsync(cluster, summary, includeOptionalPreview: false, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);

            if (readiness is null)
                continue;

            _startupIncidentHistory.RecordIncident(cluster, now);
            summary.StartupSuppressedIncidentCount++;
        }
    }

    private async Task<AutomaticNotificationProcessingResult> ProcessClustersAsync(
        ImmutableArray<NotificationCluster> clusters,
        AutomaticProcessingSummaryBuilder summary,
        DateTimeOffset now,
        Func<PreparedNotificationCandidate, CancellationToken, Task<NotificationDeliveryOutcome>> deliverAsync,
        CancellationToken cancellationToken
    )
    {
        foreach (NotificationCluster cluster in clusters)
        {
            if (_startupIncidentHistory.TrySuppressAndExtend(cluster, now))
            {
                summary.StartupSuppressedIncidentCount++;

                continue;
            }

            if (_deliveryHistory.TrySuppressAndExtend(cluster, now))
            {
                summary.DuplicateEpisodeCount++;

                continue;
            }

            PreparedNotificationCandidate? candidate = await PrepareAutomaticCandidateAsync(cluster, summary, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);

            if (candidate is null)
                continue;

            NotificationDeliveryOutcome outcome = await deliverAsync(candidate, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);

            if (outcome == NotificationDeliveryOutcome.Delivered)
            {
                _deliveryHistory.RecordIncident(cluster, timeProvider.GetUtcNow());
                summary.DeliveredClusterCount++;

                continue;
            }

            summary.SendFailureCount++;

            return new(outcome != NotificationDeliveryOutcome.Stop, summary.Build());
        }

        return new(ContinueProcessing: true, summary.Build());
    }

    private NotificationPreviewSelection SelectPreview(NotificationCluster cluster)
    {
        return _previewsByCluster.GetValue(cluster, value => new(value, options.Preview)).Selection;
    }

    private readonly record struct DiagnosticPolicyEvaluation(ImmutableArray<NotificationCriterionResult> Criteria, GibsPreviewSource? PreviewBaseSource, double ClusterDiameterKilometers);

    private readonly record struct NotificationClusterEvaluation(bool IsEligible, NotificationRejectionReason? RejectionReason, NotificationLandCoverResult? LandCoverResult)
    {
        public static NotificationClusterEvaluation Eligible { get; } = new(IsEligible: true, RejectionReason: null, LandCoverResult: null);
    }

    private sealed class AutomaticProcessingSummaryBuilder
    {
        private readonly Dictionary<NotificationRejectionReason, int> _rejectionCounts = [];

        public int ActiveClusterCount { get; set; }

        public int DeliveredClusterCount { get; set; }

        public int DuplicateEpisodeCount { get; set; }

        public int EvaluatedClusterCount { get; set; }

        public int LandCoverCandidateCount { get; set; }

        public int LandCoverUnavailableCount { get; set; }

        public int? LandCoverYear { get; set; }

        public int RejectedClusterCount { get; set; }

        public int SendFailureCount { get; set; }

        public int StartupSuppressedIncidentCount { get; set; }

        public int VegetationSuppressedCount { get; set; }

        public AutomaticNotificationProcessingSummary Build()
        {
            return new(
                ActiveClusterCount,
                EvaluatedClusterCount,
                DeliveredClusterCount,
                RejectedClusterCount,
                StartupSuppressedIncidentCount,
                DuplicateEpisodeCount,
                SendFailureCount,
                LandCoverCandidateCount,
                VegetationSuppressedCount,
                LandCoverUnavailableCount,
                LandCoverYear,
                _rejectionCounts.ToImmutableDictionary()
            );
        }

        public void RecordLandCover(NotificationLandCoverResult? result)
        {
            if (result is not { } landCover)
                return;

            LandCoverCandidateCount++;

            if (landCover.LandCoverYear is { } year)
                LandCoverYear = LandCoverYear is { } current ? Math.Max(current, year) : year;

            if (landCover.Decision == NotificationLandCoverDecision.Unavailable)
                LandCoverUnavailableCount++;
            else if (landCover.Decision == NotificationLandCoverDecision.Suppressed)
                VegetationSuppressedCount++;
        }

        public void Reject(NotificationRejectionReason reason)
        {
            RejectedClusterCount++;
            _rejectionCounts[reason] = _rejectionCounts.GetValueOrDefault(reason) + 1;
        }
    }

    private sealed class ClusteredSnapshot(AnomalySnapshot snapshot, double radiusKilometers, TimeSpan timeWindow)
    {
        public ImmutableArray<NotificationCluster> Clusters { get; } = NotificationClustering.Create(snapshot.Anomalies, radiusKilometers, timeWindow);
    }

    private sealed record DiagnosticTarget(Anomaly SelectedAnomaly, NotificationCluster Cluster);

    private sealed record NotificationCandidateReadiness(GibsPreview Preview, NotificationPreviewSelection PreviewSelection, string? LandCoverSummary);

    private sealed class SelectedPreview
    {
        public SelectedPreview(NotificationCluster cluster, NotificationPreviewOptions previewOptions)
        {
            Anomaly representative = cluster.Representative;
            double clusterDiameterKilometers = Geography.ClusterDiameterKilometers(cluster.Members);

            bool isLargePreview =
                cluster.Members.Length >= previewOptions.LargeClusterMinimumDetections
                || (representative.FrpMegawatts is { } frp
                    && frp >= previewOptions.LargeClusterMinimumFrpMegawatts)
                || clusterDiameterKilometers >= previewOptions.LargeClusterMinimumDiameterKilometers;

            NotificationPreviewSize previewSize = isLargePreview
                ? previewOptions.LargePreviewSize
                : previewOptions.PreviewSize;

            var dimensions = new GibsPreviewDimensions(previewSize.WidthKilometers, previewSize.HeightKilometers, previewOptions.PixelWidth, previewOptions.PixelHeight);

            Selection = new(dimensions, clusterDiameterKilometers, isLargePreview);
        }

        public NotificationPreviewSelection Selection { get; }
    }
}
