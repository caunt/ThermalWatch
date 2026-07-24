using System.Collections.Immutable;
using System.Globalization;

namespace ThermalWatch.Core;

public sealed class NotificationCandidateEngine(
    NotificationOptions options,
    GibsClient gibsClient,
    NearbyFeatureClient nearbyFeatureClient,
    TimeProvider timeProvider)
{
    private readonly NotificationEpisodeHistory _startupIncidentHistory = new(
        options.ClusterRadiusKilometers,
        options.ClusterTimeWindow,
        options.EpisodeRetention);
    private readonly NotificationEpisodeHistory _deliveryHistory = new(
        options.ClusterRadiusKilometers,
        options.ClusterTimeWindow,
        options.EpisodeRetention);
    private bool _firstReadySnapshot = true;

    public async Task<NotificationAutomaticProcessingResult> ProcessAutomaticAsync(
        AnomalySnapshot snapshot,
        Func<PreparedNotificationCandidate, CancellationToken, Task<NotificationDeliveryOutcome>> deliverAsync,
        CancellationToken cancellationToken)
    {
        var summary = new ProcessingSummaryBuilder();
        if (!snapshot.IsReady)
            return new(ContinueProcessing: true, summary.Build());

        DateTimeOffset now = timeProvider.GetUtcNow();
        _startupIncidentHistory.Expire(now);
        _deliveryHistory.Expire(now);

        ImmutableArray<NotificationCluster> clusters = NotificationClustering.Create(
            snapshot.Items,
            options.ClusterRadiusKilometers,
            options.ClusterTimeWindow);
        summary.ActiveClusterCount = clusters.Length;

        bool isFirstReadySnapshot = _firstReadySnapshot;
        _firstReadySnapshot = false;
        if (isFirstReadySnapshot && !options.NotifyExistingOnStartup)
        {
            await PrimeStartupIncidentsAsync(
                clusters,
                summary,
                now,
                cancellationToken).ConfigureAwait(false);
            return new(ContinueProcessing: true, summary.Build());
        }

        return await ProcessClustersAsync(
            clusters,
            summary,
            now,
            deliverAsync,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<NotificationAutomaticProcessingResult> ProcessClustersAsync(
        ImmutableArray<NotificationCluster> clusters,
        ProcessingSummaryBuilder summary,
        DateTimeOffset now,
        Func<PreparedNotificationCandidate, CancellationToken, Task<NotificationDeliveryOutcome>> deliverAsync,
        CancellationToken cancellationToken)
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

            PreparedNotificationCandidate? candidate = await PrepareAutomaticCandidateAsync(
                cluster,
                summary,
                cancellationToken).ConfigureAwait(false);
            if (candidate is null)
                continue;

            NotificationDeliveryOutcome outcome = await deliverAsync(
                candidate,
                cancellationToken).ConfigureAwait(false);
            if (outcome == NotificationDeliveryOutcome.Delivered)
            {
                _deliveryHistory.RecordIncident(cluster, timeProvider.GetUtcNow());
                summary.AcceptedClusterCount++;
                continue;
            }

            summary.SendFailureCount++;
            return new(outcome != NotificationDeliveryOutcome.Stop, summary.Build());
        }

        return new(ContinueProcessing: true, summary.Build());
    }

    private async Task PrimeStartupIncidentsAsync(
        ImmutableArray<NotificationCluster> clusters,
        ProcessingSummaryBuilder summary,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        foreach (NotificationCluster cluster in clusters)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (await EvaluateCandidateReadinessAsync(
                cluster,
                summary,
                includeOptionalPreview: false,
                cancellationToken).ConfigureAwait(false) is null)
            {
                continue;
            }

            _startupIncidentHistory.RecordIncident(cluster, now);
            summary.StartupSuppressedIncidentCount++;
        }
    }

    private async Task<PreparedNotificationCandidate?> PrepareAutomaticCandidateAsync(
        NotificationCluster cluster,
        ProcessingSummaryBuilder summary,
        CancellationToken cancellationToken)
    {
        NotificationCandidateReadiness? readiness = await EvaluateCandidateReadinessAsync(
            cluster,
            summary,
            includeOptionalPreview: true,
            cancellationToken).ConfigureAwait(false);
        if (readiness is null)
            return null;

        ImmutableArray<NearbyFeature> nearbyFeatures = await nearbyFeatureClient.FindNearbyAsync(
            cluster.Representative,
            cancellationToken).ConfigureAwait(false);
        return new(
            cluster,
            readiness.Preview,
            readiness.PreviewSelection,
            readiness.LandCoverSummary,
            nearbyFeatures);
    }

    private async Task<NotificationCandidateReadiness?> EvaluateCandidateReadinessAsync(
        NotificationCluster cluster,
        ProcessingSummaryBuilder summary,
        bool includeOptionalPreview,
        CancellationToken cancellationToken)
    {
        summary.EvaluatedClusterCount++;
        NotificationClusterEvaluation evaluation = await EvaluateClusterAsync(
            cluster,
            cancellationToken).ConfigureAwait(false);
        summary.RecordLandCover(evaluation.LandCoverResult);
        if (!evaluation.IsAccepted)
        {
            if (evaluation.RejectionReason is { } rejectionReason)
                summary.Reject(rejectionReason);
            else
                summary.RejectedClusterCount++;
            return null;
        }

        NotificationPreviewSelection previewSelection = SelectPreview(cluster);
        GibsPreview preview = IsPreviewRequired || includeOptionalPreview
            ? await gibsClient.GetPreviewAsync(
                cluster.Representative,
                previewSelection.Dimensions,
                cancellationToken).ConfigureAwait(false)
            : GibsPreview.Unavailable;
        if (!preview.IsAvailable && IsPreviewRequired)
        {
            summary.Reject(NotificationRejectionReason.PreviewUnavailable);
            return null;
        }

        return new(
            preview,
            previewSelection,
            evaluation.LandCoverResult?.FormattingSummary);
    }

    public async Task<ManualNotificationCandidates> PrepareManualAsync(
        AnomalySnapshot snapshot,
        int requestedCount,
        CancellationToken cancellationToken)
    {
        ImmutableArray<NotificationCluster> clusters = NotificationClustering.Create(
            snapshot.Items,
            options.ClusterRadiusKilometers,
            options.ClusterTimeWindow);
        var eligible = new List<PreparedNotificationCandidate>();
        foreach (NotificationCluster cluster in clusters)
        {
            NotificationClusterEvaluation evaluation = await EvaluateClusterAsync(
                cluster,
                cancellationToken).ConfigureAwait(false);
            if (!evaluation.IsAccepted)
                continue;

            NotificationPreviewSelection previewSelection = SelectPreview(cluster);
            GibsPreview preview = await gibsClient.GetPreviewAsync(
                cluster.Representative,
                previewSelection.Dimensions,
                cancellationToken).ConfigureAwait(false);
            if (!preview.IsAvailable && IsPreviewRequired)
                continue;

            eligible.Add(new(
                cluster,
                preview,
                previewSelection,
                evaluation.LandCoverResult?.FormattingSummary,
                NearbyFeatures: []));
        }

        PreparedNotificationCandidate[] selectedWithoutNearby =
        [
            .. OrderByNotificationPriority(
                    candidates: eligible,
                    frpSelector: static candidate => candidate.Cluster.Representative.FrpMegawatts,
                    detectionCountSelector: static candidate => candidate.Cluster.Members.Length,
                    diameterSelector: static candidate => candidate.PreviewSelection.ClusterDiameterKilometers,
                    acquisitionSelector: static candidate => candidate.Cluster.Representative.AcquiredAtUtc,
                    clusterIdSelector: static candidate => candidate.Cluster.Id)
                .Take(requestedCount)
        ];
        ImmutableArray<PreparedNotificationCandidate>.Builder selected =
            ImmutableArray.CreateBuilder<PreparedNotificationCandidate>(
            selectedWithoutNearby.Length);
        foreach (PreparedNotificationCandidate candidate in selectedWithoutNearby)
        {
            ImmutableArray<NearbyFeature> nearbyFeatures = await nearbyFeatureClient.FindNearbyAsync(
                candidate.Cluster.Representative,
                cancellationToken).ConfigureAwait(false);
            selected.Add(candidate with { NearbyFeatures = nearbyFeatures });
        }

        return new(eligible.Count, selected.MoveToImmutable());
    }

    public async Task<EligibleNotificationClusters> GetEligibleClustersAsync(
        AnomalySnapshot snapshot,
        CancellationToken cancellationToken)
    {
        ImmutableArray<NotificationCluster> clusters = NotificationClustering.Create(
            snapshot.Items,
            options.ClusterRadiusKilometers,
            options.ClusterTimeWindow);
        ImmutableArray<EligibleNotificationCluster>.Builder eligible =
            ImmutableArray.CreateBuilder<EligibleNotificationCluster>();
        foreach (NotificationCluster cluster in clusters)
        {
            cancellationToken.ThrowIfCancellationRequested();
            NotificationClusterEvaluation evaluation = await EvaluateClusterAsync(
                cluster,
                cancellationToken).ConfigureAwait(false);
            if (!evaluation.IsAccepted)
                continue;

            NotificationPreviewSelection previewSelection = SelectPreview(cluster);
            if (IsPreviewRequired)
            {
                GibsPreview preview = await gibsClient.GetPreviewAsync(
                    cluster.Representative,
                    previewSelection.Dimensions,
                    cancellationToken).ConfigureAwait(false);
                if (!preview.IsAvailable)
                    continue;
            }

            Anomaly representative = cluster.Representative;
            eligible.Add(new(
                cluster.Id,
                representative.Id,
                representative.CountryCode,
                representative.Source,
                representative.Satellite,
                representative.Latitude,
                representative.Longitude,
                representative.AcquiredAtUtc,
                representative.FrpMegawatts,
                cluster.Members.Length,
                previewSelection.ClusterDiameterKilometers));
        }

        ImmutableArray<EligibleNotificationCluster> ordered =
        [
            .. OrderByNotificationPriority(
                candidates: eligible,
                frpSelector: static candidate => candidate.FrpMegawatts,
                detectionCountSelector: static candidate => candidate.DetectionCount,
                diameterSelector: static candidate => candidate.ClusterDiameterKilometers,
                acquisitionSelector: static candidate => candidate.AcquiredAtUtc,
                clusterIdSelector: static candidate => candidate.ClusterId)
        ];
        return new(
            snapshot.GeneratedAtUtc,
            clusters.Length,
            ordered.Length,
            ordered);
    }

    public async Task<NotificationDiagnostic?> DiagnoseAsync(
        AnomalySnapshot snapshot,
        string anomalyId,
        CancellationToken cancellationToken)
    {
        (Anomaly SelectedAnomaly, NotificationCluster Cluster)? target =
            FindDiagnosticTarget(snapshot, anomalyId);
        if (target is not { } found)
            return null;

        NotificationCluster cluster = found.Cluster;

        var criteria = NotificationPolicy.ExplainMetadata(cluster, options.Visibility).ToBuilder();
        NotificationLandCoverResult? landCover = null;
        if (!options.LandCover.Enabled)
        {
            criteria.Add(NotificationCriterionResult.Disabled(
                code: "land-cover",
                label: "Land-cover filter"));
        }
        else
        {
            landCover = await NotificationLandCoverPolicy.EvaluateAsync(
                cluster,
                options.LandCover,
                gibsClient,
                cancellationToken).ConfigureAwait(false);
            criteria.Add(ExplainLandCover(landCover.Value));
        }

        NotificationPreviewSelection previewSelection = SelectPreview(cluster);
        GibsPreviewSource? previewBaseSource = null;
        if (!IsPreviewRequired)
        {
            criteria.Add(NotificationCriterionResult.Disabled(
                code: "exact-preview",
                label: "Exact-date preview"));
        }
        else
        {
            GibsPreview preview = await gibsClient.GetPreviewAsync(
                cluster.Representative,
                previewSelection.Dimensions,
                cancellationToken).ConfigureAwait(false);
            previewBaseSource = preview.BaseSource;
            criteria.Add(ExplainPreview(preview));
        }

        ImmutableArray<NotificationCriterionResult> criterionResults = criteria.ToImmutable();
        ImmutableArray<NearbyFeature> nearbyFeatures = await nearbyFeatureClient.FindNearbyAsync(
            found.SelectedAnomaly,
            cancellationToken).ConfigureAwait(false);
        return new(
            anomalyId,
            cluster.Id,
            cluster.Representative.Id,
            [.. cluster.Members.Select(member => member.Id)],
            cluster.Members.Length,
            previewSelection.ClusterDiameterKilometers,
            IsEligible: !criterionResults.Any(criterion => criterion.IsBlocking),
            criterionResults,
            previewBaseSource,
            nearbyFeatures);
    }

    private (Anomaly SelectedAnomaly, NotificationCluster Cluster)? FindDiagnosticTarget(
        AnomalySnapshot snapshot,
        string anomalyId)
    {
        Anomaly? selectedAnomaly = snapshot.Items.FirstOrDefault(candidate =>
            candidate.Id.Equals(anomalyId, StringComparison.Ordinal));
        if (selectedAnomaly is null)
            return null;

        NotificationCluster? cluster = NotificationClustering.Create(
                snapshot.Items,
                options.ClusterRadiusKilometers,
                options.ClusterTimeWindow)
            .FirstOrDefault(candidate => candidate.Members.Any(member =>
                member.Id.Equals(anomalyId, StringComparison.Ordinal)));
        return cluster is null ? null : (selectedAnomaly, cluster);
    }

    private bool IsPreviewRequired =>
        options.Visibility.Enabled && options.Visibility.RequirePreview;

    private static IOrderedEnumerable<candidateType> OrderByNotificationPriority<candidateType>(
        IEnumerable<candidateType> candidates,
        Func<candidateType, double?> frpSelector,
        Func<candidateType, int> detectionCountSelector,
        Func<candidateType, double> diameterSelector,
        Func<candidateType, DateTimeOffset> acquisitionSelector,
        Func<candidateType, string> clusterIdSelector) =>
        candidates
            .OrderByDescending(candidate => frpSelector(candidate).HasValue)
            .ThenByDescending(frpSelector)
            .ThenByDescending(detectionCountSelector)
            .ThenByDescending(diameterSelector)
            .ThenByDescending(acquisitionSelector)
            .ThenBy(clusterIdSelector, StringComparer.Ordinal);

    private async Task<NotificationClusterEvaluation> EvaluateClusterAsync(
        NotificationCluster cluster,
        CancellationToken cancellationToken)
    {
        NotificationMetadataEvaluation visibility = NotificationPolicy.EvaluateMetadata(
            cluster,
            options.Visibility);
        if (!visibility.IsAccepted)
            return new(IsAccepted: false, visibility.RejectionReason, LandCoverResult: null);

        if (!options.LandCover.Enabled)
            return NotificationClusterEvaluation.Accepted;

        NotificationLandCoverResult landCover = await NotificationLandCoverPolicy.EvaluateAsync(
            cluster,
            options.LandCover,
            gibsClient,
            cancellationToken).ConfigureAwait(false);
        return new(
            landCover.Decision != NotificationLandCoverDecision.Suppressed,
            RejectionReason: null,
            landCover);
    }

    private NotificationPreviewSelection SelectPreview(NotificationCluster cluster)
    {
        Anomaly representative = cluster.Representative;
        double clusterDiameterKilometers = Geography.ClusterDiameterKilometers(cluster.Members);
        NotificationPreviewOptions previewOptions = options.Preview;
        bool isLargePreview =
            cluster.Members.Length >= previewOptions.LargeClusterMinimumDetections
            || representative.FrpMegawatts is { } frp
                && frp >= previewOptions.LargeClusterMinimumFrpMegawatts
            || clusterDiameterKilometers >= previewOptions.LargeClusterMinimumDiameterKilometers;
        NotificationPreviewSize previewSize = isLargePreview
            ? previewOptions.LargePreviewSize
            : previewOptions.PreviewSize;
        var dimensions = new GibsPreviewDimensions(
            previewSize.WidthKilometers,
            previewSize.HeightKilometers,
            previewOptions.PixelWidth,
            previewOptions.PixelHeight);
        return new(dimensions, clusterDiameterKilometers, isLargePreview);
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
                Outcome: NotificationCriterionOutcomes.Passed,
                actual,
                requirement,
                landCover.Reason,
                IsBlocking: false),
            NotificationLandCoverDecision.Suppressed => new(
                Code: "land-cover",
                Label: "Land-cover filter",
                Outcome: NotificationCriterionOutcomes.Failed,
                actual,
                requirement,
                landCover.Reason,
                IsBlocking: true),
            _ => new(
                Code: "land-cover",
                Label: "Land-cover filter",
                Outcome: NotificationCriterionOutcomes.Unavailable,
                actual,
                requirement,
                Explanation: $"{landCover.Reason}; the policy fails open.",
                IsBlocking: false)
        };
    }

    private static NotificationCriterionResult ExplainPreview(GibsPreview preview)
    {
        if (!preview.IsAvailable)
        {
            return new(
                Code: "exact-preview",
                Label: "Exact-date preview",
                Outcome: NotificationCriterionOutcomes.Unavailable,
                ActualValue: "Not available",
                Requirement: "Exact acquisition-date imagery",
                Explanation: "No exact-date preview is currently available; automatic processing reevaluates the active cluster after later snapshot publications.",
                IsBlocking: true);
        }

        string actual = preview.BaseSource is { } source
            ? $"{source.Instrument} {source.Satellite} contextual base"
            : "Exact-date imagery available";
        return new(
            Code: "exact-preview",
            Label: "Exact-date preview",
            Outcome: NotificationCriterionOutcomes.Passed,
            actual,
            Requirement: "Exact acquisition-date imagery",
            Explanation: "An exact-date preview is currently available.",
            IsBlocking: false);
    }

    private readonly record struct NotificationClusterEvaluation(
        bool IsAccepted,
        NotificationRejectionReason? RejectionReason,
        NotificationLandCoverResult? LandCoverResult)
    {
        public static NotificationClusterEvaluation Accepted { get; } = new(
            IsAccepted: true,
            RejectionReason: null,
            LandCoverResult: null);
    }

    private sealed record NotificationCandidateReadiness(
        GibsPreview Preview,
        NotificationPreviewSelection PreviewSelection,
        string? LandCoverSummary);

    private sealed class ProcessingSummaryBuilder
    {
        private readonly Dictionary<NotificationRejectionReason, int> _rejectionCounts = [];

        public int ActiveClusterCount { get; set; }

        public int EvaluatedClusterCount { get; set; }

        public int AcceptedClusterCount { get; set; }

        public int RejectedClusterCount { get; set; }

        public int StartupSuppressedIncidentCount { get; set; }

        public int DuplicateEpisodeCount { get; set; }

        public int SendFailureCount { get; set; }

        public int LandCoverCandidateCount { get; set; }

        public int VegetationSuppressedCount { get; set; }

        public int LandCoverUnavailableCount { get; set; }

        public int? LandCoverYear { get; set; }

        public void Reject(NotificationRejectionReason reason)
        {
            RejectedClusterCount++;
            _rejectionCounts[reason] = _rejectionCounts.GetValueOrDefault(reason) + 1;
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

        public NotificationProcessingSummary Build() =>
            new(
                ActiveClusterCount,
                EvaluatedClusterCount,
                AcceptedClusterCount,
                RejectedClusterCount,
                StartupSuppressedIncidentCount,
                DuplicateEpisodeCount,
                SendFailureCount,
                LandCoverCandidateCount,
                VegetationSuppressedCount,
                LandCoverUnavailableCount,
                LandCoverYear,
                _rejectionCounts.ToImmutableDictionary());
    }
}
