using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using ThermalWatch.Api;
using ThermalWatch.Core;

namespace ThermalWatch.Tests;

public sealed class FirmsPollingServiceTests
{
    [Fact]
    public async Task ExecuteAsyncRunsImmediatelyAndWaitsFromCycleCompletion()
    {
        var timeProvider = new FakeTimeProvider();
        var firstRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstCompleted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var nextDelayCalculated = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var refreshCycle = new RecordingRefreshCycle(async (callCount, cancellationToken) =>
        {
            if (callCount == 1)
            {
                await firstRelease.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
                firstCompleted.SetResult();
            }

            return new(SuccessfulSegmentCount: 1, FailedSegmentCount: 0);
        });
        var schedule = new FirmsPollingSchedule(() =>
        {
            nextDelayCalculated.TrySetResult();
            return 0;
        });
        FirmsOptions options = Options(pollInterval: TimeSpan.FromMinutes(minutes: 5));
        var service = new FirmsPollingService(
            refreshCycle,
            options,
            schedule,
            timeProvider,
            NullLogger<FirmsPollingService>.Instance);

        await service.StartAsync(TestContext.Current.CancellationToken);
        await refreshCycle.WaitForCallCountAsync(expected: 1, TestContext.Current.CancellationToken);

        timeProvider.Advance(TimeSpan.FromHours(hours: 1));
        Assert.Equal(1, refreshCycle.CallCount);

        firstRelease.SetResult();
        await firstCompleted.Task.WaitAsync(TestContext.Current.CancellationToken);
        await nextDelayCalculated.Task.WaitAsync(TestContext.Current.CancellationToken);
        await Task.Delay(
            TimeSpan.FromMilliseconds(milliseconds: 10),
            TestContext.Current.CancellationToken);

        timeProvider.Advance(TimeSpan.FromMinutes(minutes: 4));
        Assert.Equal(1, refreshCycle.CallCount);
        timeProvider.Advance(TimeSpan.FromMinutes(minutes: 1));
        await refreshCycle.WaitForCallCountAsync(expected: 2, TestContext.Current.CancellationToken);

        await service.StopAsync(TestContext.Current.CancellationToken);
        Assert.Equal(1, refreshCycle.MaximumConcurrency);
    }

    private static FirmsOptions Options(TimeSpan pollInterval) =>
        new(
            MapKey: new string('A', count: 32),
            Countries: ["UKR"],
            pollInterval,
            ActiveWindow: TimeSpan.FromHours(hours: 24),
            RequestTimeout: TimeSpan.FromSeconds(seconds: 45),
            MaxConcurrency: 4);

    private sealed class RecordingRefreshCycle(
        Func<int, CancellationToken, Task<FirmsRefreshCycleResult>> refreshAsync) : IFirmsRefreshCycle, IDisposable
    {
        private readonly SemaphoreSlim _called = new(initialCount: 0);
        private int _activeCalls;
        private int _callCount;
        private int _maximumConcurrency;

        internal int CallCount => Volatile.Read(ref _callCount);

        internal int MaximumConcurrency => Volatile.Read(ref _maximumConcurrency);

        public async Task<FirmsRefreshCycleResult> RefreshAsync(CancellationToken cancellationToken)
        {
            int activeCalls = Interlocked.Increment(ref _activeCalls);
            InterlockedExtensions.Max(ref _maximumConcurrency, activeCalls);
            int callCount = Interlocked.Increment(ref _callCount);
            _called.Release();
            try
            {
                return await refreshAsync(callCount, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                Interlocked.Decrement(ref _activeCalls);
            }
        }

        internal async Task WaitForCallCountAsync(int expected, CancellationToken cancellationToken)
        {
            while (CallCount < expected)
                await _called.WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        public void Dispose() => _called.Dispose();
    }

    private static class InterlockedExtensions
    {
        internal static void Max(ref int location, int value)
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
}
