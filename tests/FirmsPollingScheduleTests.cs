using ThermalWatch.Api;

namespace ThermalWatch.Tests;

public sealed class FirmsPollingScheduleTests
{
    [Fact]
    public void CalculateDelayUsesFullIntervalAndPositiveJitter()
    {
        var pollInterval = TimeSpan.FromMinutes(minutes: 5);

        Assert.Equal(
            pollInterval,
            new FirmsPollingSchedule(static () => 0).CalculateDelay(
                pollInterval,
                consecutiveTotalFailures: 0));
        Assert.Equal(
            TimeSpan.FromSeconds(seconds: 330),
            new FirmsPollingSchedule(static () => 1).CalculateDelay(
                pollInterval,
                consecutiveTotalFailures: 0));
    }

    [Fact]
    public void CalculateDelayBacksOffTotalFailuresCapsAndResets()
    {
        var schedule = new FirmsPollingSchedule(static () => 0);
        var pollInterval = TimeSpan.FromMinutes(minutes: 5);

        Assert.Equal(TimeSpan.FromMinutes(minutes: 10), schedule.CalculateDelay(pollInterval, consecutiveTotalFailures: 1));
        Assert.Equal(TimeSpan.FromMinutes(minutes: 20), schedule.CalculateDelay(pollInterval, consecutiveTotalFailures: 2));
        Assert.Equal(TimeSpan.FromMinutes(minutes: 40), schedule.CalculateDelay(pollInterval, consecutiveTotalFailures: 3));
        Assert.Equal(TimeSpan.FromHours(hours: 1), schedule.CalculateDelay(pollInterval, consecutiveTotalFailures: 4));
        Assert.Equal(TimeSpan.FromHours(hours: 1), schedule.CalculateDelay(pollInterval, consecutiveTotalFailures: 100));
        Assert.Equal(pollInterval, schedule.CalculateDelay(pollInterval, consecutiveTotalFailures: 0));
    }

    [Fact]
    public void CalculateDelayNeverCapsBelowConfiguredInterval()
    {
        var schedule = new FirmsPollingSchedule(static () => 0);
        var pollInterval = TimeSpan.FromHours(hours: 2);

        Assert.Equal(pollInterval, schedule.CalculateDelay(pollInterval, consecutiveTotalFailures: 5));
    }
}
