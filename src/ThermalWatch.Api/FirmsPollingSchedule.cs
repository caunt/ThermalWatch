namespace ThermalWatch.Api;

internal sealed class FirmsPollingSchedule(Func<double> nextJitterSample)
{
    private const double MaximumJitterFraction = 0.1;
    private static readonly TimeSpan s_minimumBackoffCap = TimeSpan.FromHours(hours: 1);

    internal FirmsPollingSchedule() : this(Random.Shared.NextDouble)
    {
    }

    internal TimeSpan CalculateDelay(TimeSpan pollInterval, int consecutiveTotalFailures)
    {
        long maximumBaseTicks = Math.Max(pollInterval.Ticks, s_minimumBackoffCap.Ticks);
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
