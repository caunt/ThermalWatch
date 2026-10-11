using System.Text.Json;

using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Time.Testing;

using ThermalWatch.Core;

namespace ThermalWatch.Tests.Core.Notifications;

/// <summary>Verifies selection, enrichment, and the automatic notification lifecycle.</summary>
public sealed class NotificationCandidateEngineTests
{
    private static readonly DateTimeOffset ObservedAt = new(year: 2026, month: 7, day: 23, hour: 8, minute: 0, second: 0, TimeSpan.Zero);

    /// <summary>Checks enrichment coordinates for automatic and manual selection.</summary>
    [Fact]
    public async Task AutomaticAndManualCandidatesEnrichOnlyTheirSelectedRepresentatives()
    {
        using var automaticGibs = new NotFoundHandler();

        using var automaticNearby = new NearbyHandler(CreateSettlementResponse(name: "Automatic City"));

        using var automaticCache = new MemoryCache(new MemoryCacheOptions { SizeLimit = 64 * 1024 * 1024 });

        using NotificationCandidateEngineFixture automaticFixture = CreateFixture(automaticGibs, automaticCache, automaticNearby);

        NotificationCandidateEngine automaticEngine = automaticFixture.Engine;

        AnomalySnapshot automaticSnapshot = Snapshot(CreateAnomaly(id: "lower", longitude: 30, frpMegawatts: 100), CreateAnomaly(id: "higher", longitude: 30.02, frpMegawatts: 200));

        PreparedNotificationCandidate? automaticCandidate = null;

        AutomaticNotificationProcessingResult automatic = await automaticEngine.ProcessAutomaticNotificationsAsync(
            automaticSnapshot,
            (candidate, cancellationToken) =>
            {
                automaticCandidate = candidate;

                return Task.FromResult(NotificationDeliveryOutcome.Delivered);
            },
            TestContext.Current.CancellationToken
        );

        Assert.Equal(expected: 1, automatic.Summary.DeliveredClusterCount);
        Assert.Equal(expected: "Automatic City", Assert.IsType<PreparedNotificationCandidate>(automaticCandidate).SettlementName);
        string automaticQuery = Assert.Single(automaticNearby.Queries);
        Assert.Contains(expectedSubstring: "around:2000,50.000000,30.020000", automaticQuery, StringComparison.Ordinal);
        Assert.Contains(expectedSubstring: "is_in(50.000000,30.020000)", automaticQuery, StringComparison.Ordinal);

        using var manualGibs = new NotFoundHandler();

        using var manualNearby = new NearbyHandler(CreateSettlementResponse(name: "Manual Town"));

        using var manualCache = new MemoryCache(new MemoryCacheOptions { SizeLimit = 64 * 1024 * 1024 });

        using NotificationCandidateEngineFixture manualFixture = CreateFixture(manualGibs, manualCache, manualNearby);

        NotificationCandidateEngine manualEngine = manualFixture.Engine;

        AnomalySnapshot manualSnapshot = Snapshot(
            CreateAnomaly(id: "low", longitude: 30, frpMegawatts: 100),
            CreateAnomaly(id: "low-context", longitude: 30.01, frpMegawatts: 90),
            CreateAnomaly(id: "highest", longitude: 31, frpMegawatts: 300),
            CreateAnomaly(id: "highest-context", longitude: 31.01, frpMegawatts: 10),
            CreateAnomaly(id: "middle", longitude: 32, frpMegawatts: 200),
            CreateAnomaly(id: "middle-context", longitude: 32.01, frpMegawatts: 190)
        );

        ManualNotificationCandidateSelection manual = await manualEngine.PrepareManualCandidatesAsync(manualSnapshot, requestedClusterCount: 1, TestContext.Current.CancellationToken);

        Assert.Equal(expected: "Manual Town", Assert.Single(manual.SelectedCandidates.ToArray()).SettlementName);
        string manualQuery = Assert.Single(manualNearby.Queries);
        Assert.Contains(expectedSubstring: "around:2000,50.000000,32.000000", manualQuery, StringComparison.Ordinal);
        Assert.Contains(expectedSubstring: "is_in(50.000000,32.000000)", manualQuery, StringComparison.Ordinal);
    }

    /// <summary>Checks preview recovery and subsequent delivery deduplication.</summary>
    [Fact]
    public async Task AutomaticReevaluatesActiveClusterUntilRequiredPreviewIsAvailable()
    {
        using var handler = new RecoveringPreviewHandler();

        using var cache = new MemoryCache(new MemoryCacheOptions { SizeLimit = 64 * 1024 * 1024 });

        NotificationOptions options = DefaultOptions() with
        {
            Visibility = DefaultOptions().Visibility with { RequirePreview = true }
        };

        using NotificationCandidateEngineFixture fixture = CreateFixture(handler, cache, options: options);

        NotificationCandidateEngine engine = fixture.Engine;

        AnomalySnapshot snapshot = Snapshot(CreateAnomaly(id: "first", longitude: 30, frpMegawatts: 100), CreateAnomaly(id: "second", longitude: 30.02, frpMegawatts: 200));

        int deliveryCount = 0;

        AutomaticNotificationProcessingResult unavailable = await engine.ProcessAutomaticNotificationsAsync(
            snapshot,
            (candidate, cancellationToken) =>
            {
                deliveryCount++;

                return Task.FromResult(NotificationDeliveryOutcome.Delivered);
            },
            TestContext.Current.CancellationToken
        );

        handler.IsPreviewAvailable = true;

        AutomaticNotificationProcessingResult available = await engine.ProcessAutomaticNotificationsAsync(
            snapshot,
            (candidate, cancellationToken) =>
            {
                deliveryCount++;

                return Task.FromResult(NotificationDeliveryOutcome.Delivered);
            },
            TestContext.Current.CancellationToken
        );

        AutomaticNotificationProcessingResult delivered = await engine.ProcessAutomaticNotificationsAsync(
            snapshot,
            (candidate, cancellationToken) =>
            {
                deliveryCount++;

                return Task.FromResult(NotificationDeliveryOutcome.Delivered);
            },
            TestContext.Current.CancellationToken
        );

        Assert.Equal(expected: 0, unavailable.Summary.DeliveredClusterCount);
        Assert.Equal(expected: 1, unavailable.Summary.RejectionCount(NotificationRejectionReason.PreviewUnavailable));
        Assert.Equal(expected: 1, available.Summary.DeliveredClusterCount);
        Assert.Equal(expected: 1, delivered.Summary.DuplicateEpisodeCount);
        Assert.Equal(expected: 1, deliveryCount);
    }

    /// <summary>Checks that preview-blocked startup incidents remain candidates.</summary>
    [Fact]
    public async Task AutomaticReevaluatesPreviewBlockedStartupIncidentUntilItIsEligible()
    {
        using var handler = new RecoveringPreviewHandler();

        using var cache = new MemoryCache(new MemoryCacheOptions { SizeLimit = 64 * 1024 * 1024 });

        NotificationOptions options = DefaultOptions() with
        {
            SendExistingOnStartup = false,
            Visibility = DefaultOptions().Visibility with { RequirePreview = true }
        };

        using NotificationCandidateEngineFixture fixture = CreateFixture(handler, cache, options: options);

        NotificationCandidateEngine engine = fixture.Engine;

        AnomalySnapshot snapshot = Snapshot(CreateAnomaly(id: "first", longitude: 30, frpMegawatts: 100), CreateAnomaly(id: "second", longitude: 30.02, frpMegawatts: 200));

        int deliveryCount = 0;

        AutomaticNotificationProcessingResult unavailable = await engine.ProcessAutomaticNotificationsAsync(
            snapshot,
            (candidate, cancellationToken) =>
            {
                deliveryCount++;

                return Task.FromResult(NotificationDeliveryOutcome.Delivered);
            },
            TestContext.Current.CancellationToken
        );

        handler.IsPreviewAvailable = true;

        AutomaticNotificationProcessingResult available = await engine.ProcessAutomaticNotificationsAsync(
            snapshot,
            (candidate, cancellationToken) =>
            {
                deliveryCount++;

                return Task.FromResult(NotificationDeliveryOutcome.Delivered);
            },
            TestContext.Current.CancellationToken
        );

        AutomaticNotificationProcessingResult delivered = await engine.ProcessAutomaticNotificationsAsync(
            snapshot,
            (candidate, cancellationToken) =>
            {
                deliveryCount++;

                return Task.FromResult(NotificationDeliveryOutcome.Delivered);
            },
            TestContext.Current.CancellationToken
        );

        Assert.Equal(expected: 0, unavailable.Summary.StartupSuppressedIncidentCount);
        Assert.Equal(expected: 1, unavailable.Summary.RejectionCount(NotificationRejectionReason.PreviewUnavailable));
        Assert.Equal(expected: 1, available.Summary.DeliveredClusterCount);
        Assert.Equal(expected: 1, delivered.Summary.DuplicateEpisodeCount);
        Assert.Equal(expected: 1, deliveryCount);
    }

    /// <summary>Checks retry after transient delivery failure.</summary>
    [Fact]
    public async Task AutomaticRetriesTransientDeliveryThroughNextSnapshotEvaluation()
    {
        using var handler = new NotFoundHandler();

        using var cache = new MemoryCache(new MemoryCacheOptions { SizeLimit = 64 * 1024 * 1024 });

        using NotificationCandidateEngineFixture fixture = CreateFixture(handler, cache);

        NotificationCandidateEngine engine = fixture.Engine;

        AnomalySnapshot snapshot = Snapshot(CreateAnomaly(id: "first", longitude: 30, frpMegawatts: 100), CreateAnomaly(id: "second", longitude: 30.02, frpMegawatts: 200));

        int deliveryCount = 0;

        AutomaticNotificationProcessingResult failed = await engine.ProcessAutomaticNotificationsAsync(
            snapshot,
            (candidate, cancellationToken) =>
            {
                deliveryCount++;

                return Task.FromResult(NotificationDeliveryOutcome.RetryLater);
            },
            TestContext.Current.CancellationToken
        );

        AutomaticNotificationProcessingResult retried = await engine.ProcessAutomaticNotificationsAsync(
            snapshot,
            (candidate, cancellationToken) =>
            {
                deliveryCount++;

                return Task.FromResult(NotificationDeliveryOutcome.Delivered);
            },
            TestContext.Current.CancellationToken
        );

        AutomaticNotificationProcessingResult delivered = await engine.ProcessAutomaticNotificationsAsync(
            snapshot,
            (candidate, cancellationToken) =>
            {
                deliveryCount++;

                return Task.FromResult(NotificationDeliveryOutcome.Delivered);
            },
            TestContext.Current.CancellationToken
        );

        Assert.True(failed.ContinueProcessing);
        Assert.Equal(expected: 1, failed.Summary.SendFailureCount);
        Assert.Equal(expected: 1, retried.Summary.DeliveredClusterCount);
        Assert.Equal(expected: 1, delivered.Summary.DuplicateEpisodeCount);
        Assert.Equal(expected: 2, deliveryCount);
    }

    /// <summary>Checks text delivery when optional imagery is unavailable.</summary>
    [Fact]
    public async Task AutomaticSendsTextImmediatelyWhenPreviewIsOptional()
    {
        using var handler = new NotFoundHandler();

        using var cache = new MemoryCache(new MemoryCacheOptions { SizeLimit = 64 * 1024 * 1024 });

        using NotificationCandidateEngineFixture fixture = CreateFixture(handler, cache);

        NotificationCandidateEngine engine = fixture.Engine;

        AnomalySnapshot snapshot = Snapshot(CreateAnomaly(id: "first", longitude: 30, frpMegawatts: 100), CreateAnomaly(id: "second", longitude: 30.02, frpMegawatts: 200));

        PreparedNotificationCandidate? delivered = null;

        AutomaticNotificationProcessingResult result = await engine.ProcessAutomaticNotificationsAsync(
            snapshot,
            (candidate, cancellationToken) =>
            {
                delivered = candidate;

                return Task.FromResult(NotificationDeliveryOutcome.Delivered);
            },
            TestContext.Current.CancellationToken
        );

        Assert.False(Assert.IsType<PreparedNotificationCandidate>(delivered).Preview.IsAvailable);
        Assert.Equal(expected: 1, result.Summary.DeliveredClusterCount);
        Assert.Equal(expected: 0, result.Summary.RejectedClusterCount);
    }

    /// <summary>Checks suppression of eligible startup incidents and extensions.</summary>
    [Fact]
    public async Task AutomaticSuppressesEligibleStartupIncidentAndRelatedExtensions()
    {
        using var handler = new NotFoundHandler();

        using var nearbyHandler = new NearbyHandler();

        using var cache = new MemoryCache(new MemoryCacheOptions { SizeLimit = 64 * 1024 * 1024 });

        NotificationOptions options = DefaultOptions() with { SendExistingOnStartup = false };

        using NotificationCandidateEngineFixture fixture = CreateFixture(handler, cache, nearbyHandler, options);

        NotificationCandidateEngine engine = fixture.Engine;

        Anomaly first = CreateAnomaly(id: "first", longitude: 30, frpMegawatts: 100);
        Anomaly second = CreateAnomaly(id: "second", longitude: 30.02, frpMegawatts: 200);
        AnomalySnapshot startupSnapshot = Snapshot(first, second);
        int deliveryCount = 0;

        AutomaticNotificationProcessingResult startup = await engine.ProcessAutomaticNotificationsAsync(
            startupSnapshot,
            (candidate, cancellationToken) =>
            {
                deliveryCount++;

                return Task.FromResult(NotificationDeliveryOutcome.Delivered);
            },
            TestContext.Current.CancellationToken
        );

        AutomaticNotificationProcessingResult unchanged = await engine.ProcessAutomaticNotificationsAsync(
            startupSnapshot,
            (candidate, cancellationToken) =>
            {
                deliveryCount++;

                return Task.FromResult(NotificationDeliveryOutcome.Delivered);
            },
            TestContext.Current.CancellationToken
        );

        AutomaticNotificationProcessingResult extended = await engine.ProcessAutomaticNotificationsAsync(
            Snapshot(first, second, CreateAnomaly(id: "later", longitude: 30.01, frpMegawatts: 150)),
            (candidate, cancellationToken) =>
            {
                deliveryCount++;

                return Task.FromResult(NotificationDeliveryOutcome.Delivered);
            },
            TestContext.Current.CancellationToken
        );

        Assert.Equal(expected: 1, startup.Summary.EvaluatedClusterCount);
        Assert.Equal(expected: 1, startup.Summary.StartupSuppressedIncidentCount);
        Assert.Equal(expected: 1, unchanged.Summary.StartupSuppressedIncidentCount);
        Assert.Equal(expected: 1, extended.Summary.StartupSuppressedIncidentCount);
        Assert.Equal(expected: 0, extended.Summary.DeliveredClusterCount);
        Assert.Equal(expected: 0, deliveryCount);
        Assert.Equal(expected: 0, handler.RequestCount);
        Assert.Empty(nearbyHandler.Queries);
    }

    /// <summary>Checks the concurrent preview limit and ranked results.</summary>
    [Fact]
    public async Task EligibleClusterQueryEvaluatesAtMostTwoClustersConcurrently()
    {
        using var handler = new RecoveringPreviewHandler(TimeSpan.FromMilliseconds(milliseconds: 25))
        {
            IsPreviewAvailable = true
        };

        using var cache = new MemoryCache(new MemoryCacheOptions { SizeLimit = 64 * 1024 * 1024 });

        NotificationOptions options = DefaultOptions() with
        {
            Visibility = DefaultOptions().Visibility with { RequirePreview = true }
        };

        using NotificationCandidateEngineFixture fixture = CreateFixture(handler, cache, options: options);

        NotificationCandidateEngine engine = fixture.Engine;

        AnomalySnapshot snapshot = Snapshot(
            CreateAnomaly(id: "first-low", longitude: 30, frpMegawatts: 100),
            CreateAnomaly(id: "second-low", longitude: 30.02, frpMegawatts: 110),
            CreateAnomaly(id: "first-middle", longitude: 31, frpMegawatts: 200),
            CreateAnomaly(id: "second-middle", longitude: 31.02, frpMegawatts: 210),
            CreateAnomaly(id: "first-high", longitude: 32, frpMegawatts: 300),
            CreateAnomaly(id: "second-high", longitude: 32.02, frpMegawatts: 310),
            CreateAnomaly(id: "first-highest", longitude: 33, frpMegawatts: 400),
            CreateAnomaly(id: "second-highest", longitude: 33.02, frpMegawatts: 410)
        );

        EligibleNotificationClusters result = await engine.GetEligibleNotificationClustersAsync(snapshot, TestContext.Current.CancellationToken);

        Assert.Equal(expected: 2, handler.MaximumConcurrentRequests);
        Assert.Equal(
            ["second-highest", "second-high", "second-middle", "second-low"],
            result.Clusters.Select(cluster => cluster.RepresentativeId),
            StringComparer.Ordinal
        );
    }

    /// <summary>Checks eligibility after required imagery recovers.</summary>
    [Fact]
    public async Task EligibleClusterQueryFailsClosedAndReevaluatesRequiredPreview()
    {
        using var handler = new RecoveringPreviewHandler();

        using var cache = new MemoryCache(new MemoryCacheOptions { SizeLimit = 64 * 1024 * 1024 });

        NotificationOptions options = DefaultOptions() with
        {
            Visibility = DefaultOptions().Visibility with { RequirePreview = true }
        };

        using NotificationCandidateEngineFixture fixture = CreateFixture(handler, cache, options: options);

        NotificationCandidateEngine engine = fixture.Engine;

        AnomalySnapshot snapshot = Snapshot(CreateAnomaly(id: "first", longitude: 30, frpMegawatts: 100), CreateAnomaly(id: "second", longitude: 30.02, frpMegawatts: 200));

        EligibleNotificationClusters unavailable = await engine.GetEligibleNotificationClustersAsync(snapshot, TestContext.Current.CancellationToken);

        handler.IsPreviewAvailable = true;

        EligibleNotificationClusters available = await engine.GetEligibleNotificationClustersAsync(snapshot, TestContext.Current.CancellationToken);

        Assert.True(unavailable.Clusters.IsEmpty);
        Assert.Equal(expected: "second", Assert.Single(available.Clusters.ToArray()).RepresentativeId);
    }

    /// <summary>Checks the fail-open land-cover policy.</summary>
    [Fact]
    public async Task EligibleClusterQueryFailsOpenWhenLandCoverIsUnavailable()
    {
        using var handler = new NotFoundHandler();

        using var cache = new MemoryCache(new MemoryCacheOptions { SizeLimit = 64 * 1024 * 1024 });

        NotificationOptions options = DefaultOptions() with
        {
            LandCover = DefaultOptions().LandCover with { Enabled = true }
        };

        using NotificationCandidateEngineFixture fixture = CreateFixture(handler, cache, options: options);

        NotificationCandidateEngine engine = fixture.Engine;

        AnomalySnapshot snapshot = Snapshot(CreateAnomaly(id: "first", longitude: 30, frpMegawatts: 100), CreateAnomaly(id: "second", longitude: 30.02, frpMegawatts: 200));

        EligibleNotificationClusters result = await engine.GetEligibleNotificationClustersAsync(snapshot, TestContext.Current.CancellationToken);

        Assert.Equal(expected: "second", Assert.Single(result.Clusters.ToArray()).RepresentativeId);
        Assert.True(handler.RequestCount > 0);
    }

    /// <summary>Checks ranking without consuming automatic startup state.</summary>
    [Fact]
    public async Task EligibleClusterQueryFiltersOrdersAndDoesNotConsumeAutomaticLifecycle()
    {
        using var gibsHandler = new NotFoundHandler();

        using var nearbyHandler = new NearbyHandler();

        using var cache = new MemoryCache(new MemoryCacheOptions { SizeLimit = 64 * 1024 * 1024 });

        NotificationOptions options = DefaultOptions() with { SendExistingOnStartup = false };

        using NotificationCandidateEngineFixture fixture = CreateFixture(gibsHandler, cache, nearbyHandler, options);

        NotificationCandidateEngine engine = fixture.Engine;

        AnomalySnapshot snapshot = Snapshot(
            CreateAnomaly(id: "low", longitude: 30, frpMegawatts: 100),
            CreateAnomaly(id: "low-context", longitude: 30.01, frpMegawatts: 90),
            CreateAnomaly(id: "highest", longitude: 31, frpMegawatts: 300),
            CreateAnomaly(id: "highest-context", longitude: 31.01, frpMegawatts: 10),
            CreateAnomaly(id: "middle", longitude: 32, frpMegawatts: 200),
            CreateAnomaly(id: "middle-context", longitude: 32.01, frpMegawatts: 190),
            CreateAnomaly(id: "filtered-singleton", longitude: 33, frpMegawatts: 500)
        );

        EligibleNotificationClusters result = await engine.GetEligibleNotificationClustersAsync(snapshot, TestContext.Current.CancellationToken);

        Assert.Equal(snapshot.GeneratedAtUtc, result.SnapshotGeneratedAtUtc);
        Assert.Equal(expected: 4, result.EvaluatedClusterCount);
        Assert.Equal(expected: 3, result.EligibleClusterCount);
        Assert.Equal(["middle", "highest", "low"], result.Clusters.Select(cluster => cluster.RepresentativeId), StringComparer.Ordinal);
        NotificationClusterSummary first = result.Clusters[index: 0];
        Assert.Equal(expected: "RUS", first.CountryCode);
        Assert.Equal(expected: "VIIRS_SNPP_NRT", first.Source);
        Assert.Equal(expected: "N", first.Satellite);
        Assert.Equal(expected: 50, first.Latitude);
        Assert.Equal(expected: 32, first.Longitude);
        Assert.Equal(expected: 200, first.FrpMegawatts);
        Assert.Equal(expected: 390, first.TotalFrpMegawatts);
        Assert.Equal(expected: 2, first.DetectionCount);
        Assert.True(first.ClusterDiameterKilometers > 0);
        Assert.Equal(expected: 0, gibsHandler.RequestCount);
        Assert.Empty(nearbyHandler.Queries);

        int deliveryCount = 0;

        AutomaticNotificationProcessingResult processing = await engine.ProcessAutomaticNotificationsAsync(
            snapshot,
            (candidate, cancellationToken) =>
            {
                deliveryCount++;

                return Task.FromResult(NotificationDeliveryOutcome.Delivered);
            },
            TestContext.Current.CancellationToken
        );

        Assert.Equal(expected: 4, processing.Summary.EvaluatedClusterCount);
        Assert.Equal(expected: 3, processing.Summary.StartupSuppressedIncidentCount);
        Assert.Equal(expected: 1, processing.Summary.RejectedClusterCount);
        Assert.Equal(expected: 0, gibsHandler.RequestCount);
        Assert.Equal(expected: 0, deliveryCount);
    }

    /// <summary>Checks historical evaluation at the expected data scale.</summary>
    [Fact(Timeout = 10_000)]
    public async Task EligibleClusterQueryScalesAcrossLargeCompleteHistory()
    {
        const int historicalAnomaliesPerDay = 1_000;
        const int currentClusterCount = 2_000;

        NotificationOptions options = DefaultOptions() with
        {
            HistoricalFrpFilterEnabled = true
        };

        Anomaly[] historicalAnomalies =
        [
            .. Enumerable.Range(start: 0, FirmsHistoryStore.CompletedDayCount * historicalAnomaliesPerDay)
                .Select(
                    index => CreateAnomaly(
                        $"historical-{index}",
                        -100 + (index % historicalAnomaliesPerDay * 0.2),
                        frpMegawatts: 100,
                        ObservedAt.AddDays(-1 - (index / historicalAnomaliesPerDay))
                    )
                )
        ];

        FirmsHistoryStore historyStore = CreateHistoryStore(options, historicalAnomalies);

        using var handler = new NotFoundHandler();

        using var cache = new MemoryCache(new MemoryCacheOptions { SizeLimit = 64 * 1024 * 1024 });

        using NotificationCandidateEngineFixture fixture = CreateFixture(handler, cache, options: options, historyStore: historyStore);

        NotificationCandidateEngine engine = fixture.Engine;

        Anomaly[] currentAnomalies =
        [
            .. Enumerable.Range(start: 0, currentClusterCount)
                .SelectMany(
                    index => new[]
                {
                    CreateAnomaly($"current-{index}-first", -100 + (index * 0.1), frpMegawatts: 100),
                    CreateAnomaly($"current-{index}-second", -99.99 + (index * 0.1), frpMegawatts: 100)
                }
                )
        ];

        EligibleNotificationClusters result = await engine.GetEligibleNotificationClustersAsync(Snapshot(currentAnomalies), TestContext.Current.CancellationToken);

        Assert.Equal(currentClusterCount, result.EvaluatedClusterCount);
        Assert.Equal(currentClusterCount, result.EligibleClusterCount);
        Assert.Equal(expected: 0, handler.RequestCount);
    }

    /// <summary>Checks transitive diagnostics without consuming delivery state.</summary>
    [Fact]
    public async Task GetNotificationDiagnosticAsyncBuildsTransitiveClusterAndDoesNotConsumeAutomaticCandidates()
    {
        using var handler = new NotFoundHandler();

        using var cache = new MemoryCache(new MemoryCacheOptions { SizeLimit = 64 * 1024 * 1024 });

        using NotificationCandidateEngineFixture fixture = CreateFixture(handler, cache);

        NotificationCandidateEngine engine = fixture.Engine;

        AnomalySnapshot snapshot = Snapshot(
            CreateAnomaly(id: "first", longitude: 30, frpMegawatts: 100),
            CreateAnomaly(id: "bridge", longitude: 30.03, frpMegawatts: 200),
            CreateAnomaly(id: "last", longitude: 30.06, frpMegawatts: 150)
        );

        NotificationDiagnostic diagnostic = Assert.IsType<NotificationDiagnostic>(await engine.GetNotificationDiagnosticAsync(snapshot, anomalyId: "first", TestContext.Current.CancellationToken));

        Assert.Equal(expected: 3, diagnostic.DetectionCount);
        Assert.Equal(expected: 450, diagnostic.TotalFrpMegawatts);
        Assert.Equal(expected: "bridge", diagnostic.RepresentativeId);
        Assert.Equal(["bridge", "first", "last"], diagnostic.MemberIds.ToArray().Order(StringComparer.Ordinal), StringComparer.Ordinal);
        Assert.Equal(expected: 9, diagnostic.Criteria.Length);
        Assert.True(diagnostic.IsEligible);
        Assert.Equal(expected: 0, handler.RequestCount);

        PreparedNotificationCandidate? delivered = null;

        AutomaticNotificationProcessingResult processing = await engine.ProcessAutomaticNotificationsAsync(
            snapshot,
            (candidate, cancellationToken) =>
            {
                delivered = candidate;

                return Task.FromResult(NotificationDeliveryOutcome.Delivered);
            },
            TestContext.Current.CancellationToken
        );

        Assert.True(processing.ContinueProcessing);
        Assert.NotNull(delivered);
        Assert.Equal(expected: 3, delivered.Cluster.Members.Length);
        Assert.Equal(expected: 1, processing.Summary.DeliveredClusterCount);
    }

    /// <summary>Checks the explanation for unavailable required imagery.</summary>
    [Fact]
    public async Task GetNotificationDiagnosticAsyncExplainsLaterSnapshotReevaluationForUnavailableRequiredPreview()
    {
        using var handler = new NotFoundHandler();

        using var cache = new MemoryCache(new MemoryCacheOptions { SizeLimit = 64 * 1024 * 1024 });

        NotificationOptions options = DefaultOptions() with
        {
            Visibility = DefaultOptions().Visibility with { RequirePreview = true }
        };

        using NotificationCandidateEngineFixture fixture = CreateFixture(handler, cache, options: options);

        NotificationCandidateEngine engine = fixture.Engine;

        AnomalySnapshot snapshot = Snapshot(CreateAnomaly(id: "first", longitude: 30, frpMegawatts: 100), CreateAnomaly(id: "second", longitude: 30.02, frpMegawatts: 200));

        NotificationDiagnostic diagnostic = Assert.IsType<NotificationDiagnostic>(await engine.GetNotificationDiagnosticAsync(snapshot, anomalyId: "first", TestContext.Current.CancellationToken));

        NotificationCriterionResult preview = Assert.Single(diagnostic.Criteria.ToArray(), criterion => criterion.Code.Equals(value: "exact-preview", StringComparison.Ordinal));

        Assert.False(diagnostic.IsEligible);
        Assert.Equal(NotificationCriterionOutcomes.Unavailable, preview.Outcome);
        Assert.Equal(
            expected: "No exact-date preview is currently available; automatic processing reevaluates the active cluster after later snapshot publications.",
            preview.Explanation
        );
    }

    /// <summary>Checks enrichment of the selected diagnostic anomaly.</summary>
    [Fact]
    public async Task GetNotificationDiagnosticAsyncLooksUpNearbyFeaturesForSelectedAnomalyInsteadOfRepresentative()
    {
        using var gibsHandler = new NotFoundHandler();

        using var nearbyHandler = new NearbyHandler();

        using var cache = new MemoryCache(new MemoryCacheOptions { SizeLimit = 64 * 1024 * 1024 });

        using NotificationCandidateEngineFixture fixture = CreateFixture(gibsHandler, cache, nearbyHandler);

        NotificationCandidateEngine engine = fixture.Engine;

        AnomalySnapshot snapshot = Snapshot(
            CreateAnomaly(id: "selected", longitude: 30, frpMegawatts: 100),
            CreateAnomaly(id: "representative", longitude: 30.02, frpMegawatts: 200)
        );

        NotificationDiagnostic diagnostic = Assert.IsType<NotificationDiagnostic>(await engine.GetNotificationDiagnosticAsync(snapshot, anomalyId: "selected", TestContext.Current.CancellationToken));

        Assert.Equal(expected: "representative", diagnostic.RepresentativeId);
        string query = Assert.Single(nearbyHandler.Queries);
        Assert.Contains(expectedSubstring: "around:2000,50.000000,30.000000", query, StringComparison.Ordinal);
    }

    /// <summary>Checks missing diagnostic selections without provider requests.</summary>
    [Fact]
    public async Task GetNotificationDiagnosticAsyncReturnsNullForAnomalyOutsideTheSnapshot()
    {
        using var handler = new NotFoundHandler();

        using var cache = new MemoryCache(new MemoryCacheOptions { SizeLimit = 64 * 1024 * 1024 });

        using NotificationCandidateEngineFixture fixture = CreateFixture(handler, cache);

        NotificationCandidateEngine engine = fixture.Engine;

        NotificationDiagnostic? diagnostic = await engine.GetNotificationDiagnosticAsync(
            Snapshot(CreateAnomaly(id: "present", longitude: 30, frpMegawatts: 100)),
            anomalyId: "missing",
            TestContext.Current.CancellationToken
        );

        Assert.Null(diagnostic);
        Assert.Equal(expected: 0, handler.RequestCount);
    }

    /// <summary>Checks historical policy consistency across all selection paths.</summary>
    [Fact]
    public async Task HistoricalFrpCriterionBlocksAutomaticManualEligibleAndDiagnosticPaths()
    {
        using var handler = new NotFoundHandler();

        using var cache = new MemoryCache(new MemoryCacheOptions { SizeLimit = 64 * 1024 * 1024 });

        NotificationOptions options = DefaultOptions() with { HistoricalFrpFilterEnabled = true };

        Anomaly historical = CreateAnomaly(id: "historical", longitude: 30, frpMegawatts: 400) with
        {
            AcquiredAtUtc = ObservedAt.AddDays(days: -1)
        };

        FirmsHistoryStore historyStore = CreateHistoryStore(options, historical);

        using NotificationCandidateEngineFixture fixture = CreateFixture(handler, cache, options: options, historyStore: historyStore);

        NotificationCandidateEngine engine = fixture.Engine;

        AnomalySnapshot snapshot = Snapshot(
            CreateAnomaly(id: "current", longitude: 30, frpMegawatts: 200),
            CreateAnomaly(id: "current-context", longitude: 30.01, frpMegawatts: 100)
        );

        int deliveryCount = 0;

        EligibleNotificationClusters eligible = await engine.GetEligibleNotificationClustersAsync(snapshot, TestContext.Current.CancellationToken);

        ManualNotificationCandidateSelection manual = await engine.PrepareManualCandidatesAsync(snapshot, requestedClusterCount: 1, TestContext.Current.CancellationToken);

        NotificationDiagnostic diagnostic = Assert.IsType<NotificationDiagnostic>(await engine.GetNotificationDiagnosticAsync(snapshot, anomalyId: "current", TestContext.Current.CancellationToken));

        AutomaticNotificationProcessingResult automatic = await engine.ProcessAutomaticNotificationsAsync(
            snapshot,
            (candidate, cancellationToken) =>
            {
                deliveryCount++;

                return Task.FromResult(NotificationDeliveryOutcome.Delivered);
            },
            TestContext.Current.CancellationToken
        );

        Assert.True(eligible.Clusters.IsEmpty);
        Assert.Equal(expected: 0, manual.EligibleClusterCount);

        NotificationCriterionResult criterion = Assert.Single(
            diagnostic.Criteria.ToArray(),
            result => result.Code.Equals(NotificationHistoricalFrpPolicy.CriterionCode, StringComparison.Ordinal)
        );

        Assert.Equal(NotificationCriterionOutcomes.Failed, criterion.Outcome);
        Assert.False(diagnostic.IsEligible);
        Assert.Equal(expected: 1, automatic.Summary.RejectionCount(NotificationRejectionReason.HistoricalFrpNotHigher));
        Assert.Equal(expected: 0, deliveryCount);
    }

    /// <summary>Checks startup suppression after history becomes ready.</summary>
    [Fact]
    public async Task IncompleteHistoryDoesNotConsumeFirstReadyStartupSuppression()
    {
        using var handler = new NotFoundHandler();

        using var cache = new MemoryCache(new MemoryCacheOptions { SizeLimit = 64 * 1024 * 1024 });

        NotificationOptions options = DefaultOptions() with
        {
            SendExistingOnStartup = false,
            HistoricalFrpFilterEnabled = true
        };

        FirmsHistoryStore historyStore = CreateIncompleteHistoryStore(options);

        using NotificationCandidateEngineFixture fixture = CreateFixture(handler, cache, options: options, historyStore: historyStore);

        NotificationCandidateEngine engine = fixture.Engine;

        AnomalySnapshot snapshot = Snapshot(CreateAnomaly(id: "first", longitude: 30, frpMegawatts: 200), CreateAnomaly(id: "second", longitude: 30.01, frpMegawatts: 100));

        int deliveryCount = 0;

        AutomaticNotificationProcessingResult unavailable = await engine.ProcessAutomaticNotificationsAsync(
            snapshot,
            (candidate, cancellationToken) =>
            {
                deliveryCount++;

                return Task.FromResult(NotificationDeliveryOutcome.Delivered);
            },
            TestContext.Current.CancellationToken
        );

        CompleteHistory(historyStore);

        AutomaticNotificationProcessingResult primed = await engine.ProcessAutomaticNotificationsAsync(
            snapshot,
            (candidate, cancellationToken) =>
            {
                deliveryCount++;

                return Task.FromResult(NotificationDeliveryOutcome.Delivered);
            },
            TestContext.Current.CancellationToken
        );

        Assert.Equal(expected: 1, unavailable.Summary.RejectionCount(NotificationRejectionReason.HistoryUnavailable));
        Assert.Equal(expected: 1, primed.Summary.StartupSuppressedIncidentCount);
        Assert.Equal(expected: 0, deliveryCount);
    }

    /// <summary>Checks unique delivery names and the larger diagnostic limit.</summary>
    [Fact]
    public async Task NearbyFeatureLimitsKeepFiveUniqueCandidateNamesAndAllDiagnosticFeatures()
    {
        using var gibsHandler = new NotFoundHandler();

        using var nearbyHandler = new NearbyHandler(CreateNearbyResponse(featureCount: 26));

        using var cache = new MemoryCache(new MemoryCacheOptions { SizeLimit = 64 * 1024 * 1024 });

        using NotificationCandidateEngineFixture fixture = CreateFixture(gibsHandler, cache, nearbyHandler);

        NotificationCandidateEngine engine = fixture.Engine;

        AnomalySnapshot snapshot = Snapshot(
            CreateAnomaly(id: "selected", longitude: 30, frpMegawatts: 100),
            CreateAnomaly(id: "representative", longitude: 30.02, frpMegawatts: 200)
        );

        PreparedNotificationCandidate? delivered = null;

        AutomaticNotificationProcessingResult processing = await engine.ProcessAutomaticNotificationsAsync(
            snapshot,
            (candidate, cancellationToken) =>
            {
                delivered = candidate;

                return Task.FromResult(NotificationDeliveryOutcome.Delivered);
            },
            TestContext.Current.CancellationToken
        );

        ManualNotificationCandidateSelection manual = await engine.PrepareManualCandidatesAsync(snapshot, requestedClusterCount: 1, TestContext.Current.CancellationToken);

        NotificationDiagnostic diagnostic = Assert.IsType<NotificationDiagnostic>(await engine.GetNotificationDiagnosticAsync(snapshot, anomalyId: "representative", TestContext.Current.CancellationToken));

        PreparedNotificationCandidate automaticCandidate =
            Assert.IsType<PreparedNotificationCandidate>(delivered);

        Assert.Equal(expected: 1, processing.Summary.DeliveredClusterCount);
        PreparedNotificationCandidate manualCandidate = Assert.Single(manual.SelectedCandidates.ToArray());
        Assert.Equal([1, 3, 4, 5, 6], automaticCandidate.NearbyFeatures.Select(feature => feature.OsmId));
        Assert.Equal([1, 3, 4, 5, 6], manualCandidate.NearbyFeatures.Select(feature => feature.OsmId));
        Assert.Equal(
            ["Shared source", "Nearby 3", "Nearby 4", "Nearby 5", "Nearby 6"],
            automaticCandidate.NearbyFeatures.Select(feature => feature.Name),
            StringComparer.Ordinal
        );
        Assert.Equal(expected: 25, diagnostic.NearbyFeatures.Length);
        Assert.Equal(Enumerable.Range(start: 1, count: 25), diagnostic.NearbyFeatures.Select(feature => (int)feature.OsmId));
        Assert.Equal(expected: "Shared source", diagnostic.NearbyFeatures[index: 0].Name);
        Assert.Equal(expected: "SHARED SOURCE", diagnostic.NearbyFeatures[index: 1].Name);
        Assert.Contains(expectedSubstring: "around:2000,50.000000,30.020000", Assert.Single(nearbyHandler.Queries), StringComparison.Ordinal);
    }

    private static void CompleteHistory(FirmsHistoryStore store, params Anomaly[] anomalies)
    {
        DateOnly today = store.Current.RetainedThroughDate;

        SegmentRefreshResult[] results =
        [
            .. FirmsSources.All.Select(
                source => SegmentRefreshResult.Success(
                    new(CountryCode: "RUS", source),
                    ObservedAt,
                    ObservedAt,
                    source.Equals(value: "VIIRS_SNPP_NRT", StringComparison.Ordinal) ? [.. anomalies] : [],
                    IngestionModes.Country
                )
            )
        ];

        FirmsHistory published = store.Publish(today.AddDays(-FirmsHistoryStore.CompletedDayCount), FirmsHistoryStore.CompletedDayCount, results);
        Assert.Same(published, store.Current);
        Assert.True(published.IsReady);
    }

    private static Anomaly CreateAnomaly(string id, double longitude, double frpMegawatts, DateTimeOffset? acquiredAtUtc = null)
    {
        return new(
            id,
            CountryCode: "RUS",
            Source: "VIIRS_SNPP_NRT",
            Satellite: "N",
            Instrument: "VIIRS",
            Latitude: 50,
            longitude,
            acquiredAtUtc ?? ObservedAt,
            DayNight: "D",
            BrightnessKelvin: 350,
            SecondaryBrightnessKelvin: 300,
            frpMegawatts,
            ScanKilometers: 0.4,
            TrackKilometers: 0.4,
            ConfidenceRaw: "n",
            ConfidencePercent: null,
            ConfidenceCategory: "nominal",
            Version: "2.0NRT",
            new Uri($"https://example.test/{id}")
        );
    }

    private static NotificationCandidateEngineFixture CreateFixture(
        HttpMessageHandler handler,
        IMemoryCache cache,
        HttpMessageHandler? nearbyHandler = null,
        NotificationOptions? options = null,
        FirmsHistoryStore? historyStore = null
    )
    {
        return new(handler, cache, nearbyHandler, options ?? DefaultOptions(), new FakeTimeProvider(ObservedAt.AddHours(hours: 1)), historyStore);
    }

    private static FirmsHistoryStore CreateHistoryStore(NotificationOptions options, params Anomaly[] anomalies)
    {
        FirmsHistoryStore store = CreateIncompleteHistoryStore(options);
        CompleteHistory(store, anomalies);

        return store;
    }

    private static FirmsHistoryStore CreateIncompleteHistoryStore(NotificationOptions options)
    {
        FirmsOptions firmsOptions = new(
            new string(c: 'A', count: 32),
            ["RUS"],
            TimeSpan.FromMinutes(minutes: 5),
            TimeSpan.FromHours(hours: 24),
            TimeSpan.FromSeconds(seconds: 45),
            MaximumConcurrency: 4
        );

        return new(firmsOptions, options, new FakeTimeProvider(ObservedAt.AddHours(hours: 1)));
    }

    private static string CreateNearbyResponse(int featureCount)
    {
        return JsonSerializer.Serialize(
            new
            {
                elements = Enumerable.Range(start: 1, featureCount).Select(
                    id => new
                    {
                        type = "node",
                        id,
                        lat = 50,
                        lon = 30.02,
                        tags = new
                        {
                            name = id switch
                            {
                                1 => "Shared source",
                                2 => "SHARED SOURCE",
                                _ => $"Nearby {id}"
                            }
                        }
                    }
                )
            }
        );
    }

    private static string CreateSettlementResponse(string name)
    {
        return JsonSerializer.Serialize(new { elements = new[] { new { type = "area", id = 3_600_000_001L, tags = new { name, place = "city", admin_level = "8" } } } });
    }

    private static NotificationOptions DefaultOptions()
    {
        return NotificationCandidateEngineFixture.DefaultOptions();
    }

    private static AnomalySnapshot Snapshot(params Anomaly[] anomalies)
    {
        return new(
            ObservedAt.AddHours(hours: 1),
            ActiveWindowHours: 24,
            IsReady: true,
            IsPartiallyStale: false,
            ["RUS"],
            [],
            anomalies.Length,
            [.. anomalies]
        );
    }
}
