using ThermalWatch.Api;

namespace ThermalWatch.Tests;

public sealed class ApplicationConfigurationTests
{
    private const string MapKey = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";

    [Theory]
    [InlineData("3.00:00:00")]
    [InlineData("72:00:00")]
    public void FromEnvironmentAllowsSeventyTwoHourActiveWindowAndRaisesDefaultEpisodeRetention(
        string activeWindow)
    {
        var configuration = ApplicationConfiguration.FromEnvironment(name => name switch
        {
            "FIRMS_MAP_KEY" => MapKey,
            "FIRMS_COUNTRIES" => "UKR",
            "FIRMS_ACTIVE_WINDOW" => activeWindow,
            _ => null
        });

        Assert.Equal(TimeSpan.FromHours(hours: 72), configuration.Firms.ActiveWindow);
        Assert.Equal(configuration.Firms.ActiveWindow, configuration.Notifications.EpisodeRetention);
    }

    [Fact]
    public void FromEnvironmentRejectsActiveWindowLongerThanSeventyTwoHours()
    {
        ApplicationConfigurationException exception = Assert.Throws<ApplicationConfigurationException>(() =>
            ApplicationConfiguration.FromEnvironment(name => name switch
            {
                "FIRMS_MAP_KEY" => MapKey,
                "FIRMS_COUNTRIES" => "UKR",
                "FIRMS_ACTIVE_WINDOW" => "3.00:00:01",
                _ => null
            }));

        Assert.Equal(
            "FIRMS_ACTIVE_WINDOW must be a duration between 00:01:00 and 3.00:00:00.",
            exception.Message);
    }

    [Fact]
    public void FromEnvironmentRejectsExplicitSeenRetentionShorterThanActiveWindow()
    {
        ApplicationConfigurationException exception = Assert.Throws<ApplicationConfigurationException>(() =>
            ApplicationConfiguration.FromEnvironment(name => name switch
            {
                "FIRMS_MAP_KEY" => MapKey,
                "FIRMS_COUNTRIES" => "UKR",
                "FIRMS_ACTIVE_WINDOW" => "3.00:00:00",
                "TELEGRAM_SEEN_RETENTION" => "2.00:00:00",
                _ => null
            }));

        Assert.Equal(
            "TELEGRAM_SEEN_RETENTION must be at least FIRMS_ACTIVE_WINDOW.",
            exception.Message);
    }

    [Fact]
    public void FromEnvironmentIgnoresRemovedPreviewRetryWindow()
    {
        var configuration = ApplicationConfiguration.FromEnvironment(name => name switch
        {
            "FIRMS_MAP_KEY" => MapKey,
            "FIRMS_COUNTRIES" => "UKR",
            "TELEGRAM_PREVIEW_RETRY_WINDOW" => "not-a-duration",
            _ => null
        });

        Assert.Equal(TimeSpan.FromHours(hours: 48), configuration.Notifications.EpisodeRetention);
    }
}
