using Microsoft.Extensions.Http.Resilience;
using Polly;

namespace ThermalWatch.Api;

internal static class FirmsResilience
{
    private const double AttemptTimeoutFraction = 0.4;
    private static readonly TimeSpan s_minimumAttemptTimeout = TimeSpan.FromSeconds(seconds: 1);

    internal static TimeSpan CalculateAttemptTimeout(TimeSpan totalTimeout)
    {
        TimeSpan attemptTimeout = totalTimeout * AttemptTimeoutFraction;
        return attemptTimeout < s_minimumAttemptTimeout
            ? s_minimumAttemptTimeout
            : attemptTimeout;
    }

    internal static void Configure(HttpStandardResilienceOptions options, TimeSpan totalTimeout)
    {
        TimeSpan attemptTimeout = CalculateAttemptTimeout(totalTimeout);
        options.TotalRequestTimeout.Timeout = totalTimeout;
        options.AttemptTimeout.Timeout = attemptTimeout;
        options.CircuitBreaker.SamplingDuration = TimeSpan.FromTicks(Math.Max(
            TimeSpan.FromSeconds(seconds: 30).Ticks,
            attemptTimeout.Ticks * 2));
        options.Retry.MaxRetryAttempts = 1;
        options.Retry.Delay = TimeSpan.FromSeconds(seconds: 1);
        options.Retry.BackoffType = DelayBackoffType.Exponential;
        options.Retry.UseJitter = true;
        options.Retry.ShouldRetryAfterHeader = true;
    }
}
