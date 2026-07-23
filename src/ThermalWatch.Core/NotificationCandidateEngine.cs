using System.Collections.Immutable;
using System.Globalization;

namespace ThermalWatch.Core;

public sealed class NotificationCandidateEngine(
    NotificationOptions options,
    GibsClient gibsClient,
    NearbyFeatureClient nearbyFeatureClient,
    TimeProvider timeProvider)
{
    private const int MaximumSeenIds = 100_000;
    private readonly Dictionary<string, DateTimeOffset> _seen = new(StringComparer.Ordinal);
    private readonly NotificationAutomaticState _automaticState = new(
        options.ClusterRadiusKilometers,
        options.ClusterTimeWindow,
        options.SeenRetention);
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
        ExpireSeen(now);
        _automaticState.Expire(now);

        if (_firstReadySnapshot && !options.NotifyExistingOnStartup)
        {
            PrimeExistingDetections(snapshot, now, summary);
            return new(ContinueProcessing: true, summary.Build());
        }

        _firstReadySnapshot = false;
        ImmutableArray<NotificationCluster> clusters = FindNewClusters(snapshot, now, summary);
        await QueueClustersAsync(clusters, now, summary, cancellationToken).ConfigureAwait(false);

        bool continueProcessing = await ProcessPendingAsync(
            deliverAsync,
            summary,
            cancellationToken).ConfigureAwait(false);
        return new(continueProcessing, summary.Build());
    }

    private void PrimeExistingDetections(
        AnomalySnapshot snapshot,
        DateTimeOffset now,
        ProcessingSummaryBuilder summary)
    {
        foreach (Anomaly detection in snapshot.Items)
            _seen[detection.Id] = now;

        _firstReadySnapshot = false;
        TrimSeen();
        summary.PrimedDetectionCount = snapshot.Items.Length;
    }

    private ImmutableArray<NotificationCluster> FindNewClusters(
        AnomalySnapshot snapshot,
        DateTimeOffset now,
        ProcessingSummaryBuilder summary)
    {
        Anomaly[] newDetections = [.. snapshot.Items.Where(detection => !_seen.ContainsKey(detection.Id))];
        summary.NewDetectionCount = newDetections.Length;
        foreach (Anomaly detection in newDetections)
            _seen[detection.Id] = now;

        TrimSeen();
        ImmutableArray<NotificationCluster> clusters = NotificationClustering.CreateCandidates(
            snapshot.Items,
            newDetections,
            options.ClusterRadiusKilometers,
            options.ClusterTimeWindow,
            options.Visibility.Enabled && options.Visibility.MinimumClusterDetections > 1);
        summary.CandidateClusterCount = clusters.Length;
        return clusters;
    }

    private async Task QueueClustersAsync(
        ImmutableArray<NotificationCluster> clusters,
        DateTimeOffset now,
        ProcessingSummaryBuilder summary,
        CancellationToken cancellationToken)
    {
        foreach (NotificationCluster cluster in clusters)
        {
            NotificationCandidatePreparation preparation = _automaticState.PrepareCandidate(cluster, now);
            if (preparation.ContinuesDeliveredEpisode)
            {
                summary.DuplicateEpisodeCount++;
                continue;
            }

            NotificationCluster preparedCluster = preparation.Cluster;
            NotificationClusterEvaluation evaluation = await EvaluateClusterAsync(
                preparedCluster,
                cancellationToken).ConfigureAwait(false);
            summary.RecordLandCover(evaluation.LandCoverResult);
            if (!evaluation.IsAccepted)
            {
                if (evaluation.RejectionReason is { } rejectionReason)
                    summary.Reject(rejectionReason);
                continue;
            }

            _automaticState.AddPending(new(
                preparedCluster,
                preparation.FirstSeenUtc,
                SelectPreview(preparedCluster),
                evaluation.LandCoverResult?.FormattingSummary));
        }
    }

    public async Task<ManualNotificationCandidates> PrepareManualAsync(
        AnomalySnapshot snapshot,
        int requestedCount,
        CancellationToken cancellationToken)
    {
        ImmutableArray<NotificationCluster> clusters = NotificationClustering.CreateCandidates(
            snapshot.Items,
            snapshot.Items,
            options.ClusterRadiusKilometers,
            options.ClusterTimeWindow,
            includeActiveContext: false);
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
            .. eligible
                .OrderByDescending(candidate => candidate.Cluster.Representative.FrpMegawatts.HasValue)
                .ThenByDescending(candidate => candidate.Cluster.Representative.FrpMegawatts)
                .ThenByDescending(candidate => candidate.Cluster.Members.Length)
                .ThenByDescending(candidate => candidate.PreviewSelection.ClusterDiameterKilometers)
                .ThenByDescending(candidate => candidate.Cluster.Representative.AcquiredAtUtc)
                .ThenBy(candidate => candidate.Cluster.Id, StringComparer.Ordinal)
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

    private async Task<bool> ProcessPendingAsync(
        Func<PreparedNotificationCandidate, CancellationToken, Task<NotificationDeliveryOutcome>> deliverAsync,
        ProcessingSummaryBuilder summary,
        CancellationToken cancellationToken)
    {
        for (int index = 0; index < _automaticState.PendingCount;)
        {
            DateTimeOffset now = timeProvider.GetUtcNow();
            PendingNotificationCandidate pending = _automaticState.GetPending(index);
            if (_automaticState.TrySuppressPending(index, now))
            {
                summary.DuplicateEpisodeCount++;
                continue;
            }

            GibsPreview preview = await gibsClient.GetPreviewAsync(
                pending.Cluster.Representative,
                pending.PreviewSelection.Dimensions,
                cancellationToken).ConfigureAwait(false);
            bool previewExpired = now - pending.FirstSeenUtc >= options.PreviewRetryWindow;

            if (!preview.IsAvailable && !previewExpired)
            {
                summary.PendingPreviewCount++;
                index++;
                continue;
            }

            if (!preview.IsAvailable && IsPreviewRequired)
            {
                summary.Reject(NotificationRejectionReason.PreviewUnavailable);
                summary.PreviewTimeoutCount++;
                _automaticState.RemovePendingAt(index);
                continue;
            }

            ImmutableArray<NearbyFeature> nearbyFeatures = await nearbyFeatureClient.FindNearbyAsync(
                pending.Cluster.Representative,
                cancellationToken).ConfigureAwait(false);
            var candidate = new PreparedNotificationCandidate(
                pending.Cluster,
                preview,
                pending.PreviewSelection,
                pending.LandCoverSummary,
                nearbyFeatures);
            NotificationDeliveryOutcome outcome = await deliverAsync(
                candidate,
                cancellationToken).ConfigureAwait(false);
            if (outcome == NotificationDeliveryOutcome.Delivered)
            {
                _automaticState.RecordDelivered(pending.Cluster, timeProvider.GetUtcNow());
                summary.AcceptedClusterCount++;
                _automaticState.RemovePendingAt(index);
                continue;
            }

            summary.SendFailureCount++;
            return outcome != NotificationDeliveryOutcome.Stop;
        }

        return true;
    }

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
                Explanation: "No exact-date preview is currently available; automatic processing waits until its retry window expires.",
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

    private void ExpireSeen(DateTimeOffset now)
    {
        DateTimeOffset cutoff = now - options.SeenRetention;
        foreach (string id in _seen.Where(pair => pair.Value < cutoff).Select(pair => pair.Key).ToArray())
            _seen.Remove(id);
    }

    private void TrimSeen()
    {
        int excess = _seen.Count - MaximumSeenIds;
        if (excess <= 0)
            return;

        foreach (string id in _seen.OrderBy(pair => pair.Value).Take(excess).Select(pair => pair.Key).ToArray())
            _seen.Remove(id);
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

    private sealed class ProcessingSummaryBuilder
    {
        private readonly Dictionary<NotificationRejectionReason, int> _rejectionCounts = [];

        public int PrimedDetectionCount { get; set; }

        public int NewDetectionCount { get; set; }

        public int CandidateClusterCount { get; set; }

        public int AcceptedClusterCount { get; set; }

        public int RejectedClusterCount { get; set; }

        public int DuplicateEpisodeCount { get; set; }

        public int PendingPreviewCount { get; set; }

        public int PreviewTimeoutCount { get; set; }

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
                PrimedDetectionCount,
                NewDetectionCount,
                CandidateClusterCount,
                AcceptedClusterCount,
                RejectedClusterCount,
                DuplicateEpisodeCount,
                PendingPreviewCount,
                PreviewTimeoutCount,
                SendFailureCount,
                LandCoverCandidateCount,
                VegetationSuppressedCount,
                LandCoverUnavailableCount,
                LandCoverYear,
                _rejectionCounts.ToImmutableDictionary());
    }
}
