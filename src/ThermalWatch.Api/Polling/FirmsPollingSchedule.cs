namespace ThermalWatch.Api.Polling;

internal sealed class FirmsPollingSchedule(Func<double> nextJitterSample)
{
    private const double MaximumJitterFraction = 0.1;
    private static readonly TimeSpan MinimumBackoffCap = TimeSpan.FromHours(hours: 1);

    internal FirmsPollingSchedule() : this(Random.Shared.NextDouble)
    {
    }

    internal TimeSpan CalculateDelay(TimeSpan pollInterval, int consecutiveTotalFailures)
    {
        long maximumBaseTicks = Math.Max(pollInterval.Ticks, MinimumBackoffCap.Ticks);
        long baseTicks = pollInterval.Ticks;

        for (int failure = 0; failure < consecutiveTotalFailures && baseTicks < maximumBaseTicks; failure++)
        {
            baseTicks = baseTicks > maximumBaseTicks / 2
                ? maximumBaseTicks
                : Math.Min(baseTicks * 2, maximumBaseTicks);
        }

        double sample = Math.Clamp(nextJitterSample(), min: 0, max: 1);
        long jitterTicks = (long)(baseTicks * MaximumJitterFraction * sample);

        return TimeSpan.FromTicks(baseTicks + jitterTicks);
    }
}
