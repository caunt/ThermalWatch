using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

using Nito.AsyncEx;

using ThermalWatch.Api.Polling;
using ThermalWatch.Core;

namespace ThermalWatch.Tests.Api;

/// <summary>Verifies polling order, delays, and exclusion of overlapping cycles.</summary>
public sealed class FirmsPollingServiceTests
{
    /// <summary>Publishes the current snapshot before backfilling historical data.</summary>
    [Fact]
    public async Task ExecuteAsyncRefreshesCurrentSnapshotBeforeHistoryBackfill()
    {
        var order = new List<string>();

        var refreshCycle = new RecordingRefreshCycle(
            (callCount, cancellationToken) =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.Equal(expected: 1, callCount);
            order.Add(item: "current");

            return Task.FromResult(new FirmsRefreshCycleResult(SuccessfulSegmentCount: 1, FailedSegmentCount: 0));
        }
        );

        var historyBackfill = new RecordingHistoryBackfill(() => order.Add(item: "history"));
        var timeProvider = new FakeTimeProvider();

        using var service = new FirmsPollingService(
            refreshCycle,
            historyBackfill,
            Options(TimeSpan.FromMinutes(minutes: 5)),
            new FirmsPollingSchedule(static () => 0),
            timeProvider,
            NullLogger<FirmsPollingService>.Instance
        );

        await service.StartAsync(TestContext.Current.CancellationToken);
        await historyBackfill.Completed.Task.WaitAsync(TestContext.Current.CancellationToken);
        await service.StopAsync(TestContext.Current.CancellationToken);

        Assert.Equal(["current", "history"], order, StringComparer.Ordinal);
    }

    /// <summary>Starts immediately and measures the next interval from completion.</summary>
    [Fact]
    public async Task ExecuteAsyncRunsImmediatelyAndWaitsFromCycleCompletion()
    {
        var timeProvider = new FakeTimeProvider();
        var firstRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstCompleted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var nextDelayCalculated = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        async Task<FirmsRefreshCycleResult> RefreshAsync(int callCount, CancellationToken cancellationToken)
        {
            if (callCount == 1)
            {
                await firstRelease.Task.WaitAsync(cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
                firstCompleted.SetResult();
            }

            return new(SuccessfulSegmentCount: 1, FailedSegmentCount: 0);
        }

        var refreshCycle = new RecordingRefreshCycle(RefreshAsync);

        double SampleJitter()
        {
            if (!nextDelayCalculated.Task.IsCompleted)
                nextDelayCalculated.SetResult();

            return 0;
        }

        var schedule = new FirmsPollingSchedule(SampleJitter);

        FirmsOptions options = Options(TimeSpan.FromMinutes(minutes: 5));

        using var service = new FirmsPollingService(refreshCycle, options, schedule, timeProvider, NullLogger<FirmsPollingService>.Instance);

        await service.StartAsync(TestContext.Current.CancellationToken);
        await refreshCycle.WaitForCallCountAsync(expected: 1, TestContext.Current.CancellationToken);

        timeProvider.Advance(TimeSpan.FromHours(hours: 1));
        Assert.Equal(expected: 1, refreshCycle.CallCount);

        firstRelease.SetResult();
        await firstCompleted.Task.WaitAsync(TestContext.Current.CancellationToken);
        await nextDelayCalculated.Task.WaitAsync(TestContext.Current.CancellationToken);
        await Task.Delay(TimeSpan.FromMilliseconds(milliseconds: 10), TestContext.Current.CancellationToken);

        timeProvider.Advance(TimeSpan.FromMinutes(minutes: 4));
        Assert.Equal(expected: 1, refreshCycle.CallCount);
        timeProvider.Advance(TimeSpan.FromMinutes(minutes: 1));
        await refreshCycle.WaitForCallCountAsync(expected: 2, TestContext.Current.CancellationToken);

        await service.StopAsync(TestContext.Current.CancellationToken);
        Assert.Equal(expected: 1, refreshCycle.MaximumConcurrency);
    }

    private static FirmsOptions Options(TimeSpan pollInterval)
    {
        return new(
            new string(c: 'A', count: 32),
            ["UKR"],
            pollInterval,
            TimeSpan.FromHours(hours: 24),
            TimeSpan.FromSeconds(seconds: 45),
            MaximumConcurrency: 4
        );
    }

    private static class InterlockedExtensions
    {
        internal static void RecordMaximum(ref int location, int value)
        {
            int current = Volatile.Read(ref location);

            while (value > current)
            {
                int previous = Interlocked.CompareExchange(ref location, value, current);

                if (previous == current)
                    return;

                current = previous;
            }
        }
    }

    private sealed class RecordingHistoryBackfill(Action record) : IFirmsHistoryBackfill
    {
        public TaskCompletionSource Completed { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<FirmsHistoryBackfillResult> RefreshAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            record();
            Completed.SetResult();

            return Task.FromResult(new FirmsHistoryBackfillResult(SuccessfulRequestCount: 1, FailedRequestCount: 0));
        }
    }

    private sealed class RecordingRefreshCycle(Func<int, CancellationToken, Task<FirmsRefreshCycleResult>> refreshAsync) : IFirmsRefreshCycle
    {
        private readonly AsyncSemaphore _called = new(initialCount: 0);
        private int _activeCalls;
        private int _callCount;
        private int _maximumConcurrency;

        internal int CallCount => Volatile.Read(ref _callCount);

        internal int MaximumConcurrency => Volatile.Read(ref _maximumConcurrency);

        public async Task<FirmsRefreshCycleResult> RefreshAsync(CancellationToken cancellationToken)
        {
            int activeCalls = Interlocked.Increment(ref _activeCalls);
            InterlockedExtensions.RecordMaximum(ref _maximumConcurrency, activeCalls);
            int callCount = Interlocked.Increment(ref _callCount);
            _called.Release();

            try
            {
                return await refreshAsync(callCount, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
            }
            finally
            {
                int remainingCalls = Interlocked.Decrement(ref _activeCalls);
                Assert.True(remainingCalls >= 0);
            }
        }

        internal async Task WaitForCallCountAsync(int expected, CancellationToken cancellationToken)
        {
            while (CallCount < expected)
                await _called.WaitAsync(cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
        }
    }
}
