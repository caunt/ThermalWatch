using ThermalWatch.Api;

namespace ThermalWatch.Tests.Api;

/// <summary>Verifies environment configuration boundaries and defaults.</summary>
public sealed class ApplicationConfigurationTests
{
    private const string MapKey = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";

    /// <summary>Accepts both supported duration forms and retains the entire active window.</summary>
    [Theory]
    [InlineData("3.00:00:00")]
    [InlineData("72:00:00")]
    public void FromEnvironmentAllowsSeventyTwoHourActiveWindowAndRaisesDefaultEpisodeRetention(string activeWindow)
    {
        var configuration = ApplicationConfiguration.FromEnvironment(name => name switch { "FIRMS_MAP_KEY" => MapKey, "FIRMS_COUNTRIES" => "UKR", "FIRMS_ACTIVE_WINDOW" => activeWindow, _ => null });

        Assert.Equal(TimeSpan.FromHours(hours: 72), configuration.Firms.ActiveWindow);
        Assert.Equal(configuration.Firms.ActiveWindow, configuration.Notifications.EpisodeRetention);
    }

    /// <summary>Ignores the retired preview retry setting.</summary>
    [Fact]
    public void FromEnvironmentIgnoresRemovedPreviewRetryWindow()
    {
        var configuration = ApplicationConfiguration.FromEnvironment(
            name => name switch
        {
            "FIRMS_MAP_KEY" => MapKey,
            "FIRMS_COUNTRIES" => "UKR",
            "TELEGRAM_PREVIEW_RETRY_WINDOW" => "not-a-duration",
            _ => null
        }
        );

        Assert.Equal(TimeSpan.FromHours(hours: 48), configuration.Notifications.EpisodeRetention);
    }

    /// <summary>Rejects active windows beyond FIRMS retention.</summary>
    [Fact]
    public void FromEnvironmentRejectsActiveWindowLongerThanSeventyTwoHours()
    {
        ApplicationConfigurationException exception = Assert.Throws<ApplicationConfigurationException>(
            () =>
            ApplicationConfiguration.FromEnvironment(name => name switch { "FIRMS_MAP_KEY" => MapKey, "FIRMS_COUNTRIES" => "UKR", "FIRMS_ACTIVE_WINDOW" => "3.00:00:01", _ => null })
        );

        Assert.Equal(expected: "FIRMS_ACTIVE_WINDOW must be a duration between 00:01:00 and 3.00:00:00.", exception.Message);
    }

    /// <summary>Rejects episode retention that cannot cover active anomalies.</summary>
    [Fact]
    public void FromEnvironmentRejectsExplicitEpisodeRetentionShorterThanActiveWindow()
    {
        ApplicationConfigurationException exception = Assert.Throws<ApplicationConfigurationException>(
            () =>
            ApplicationConfiguration.FromEnvironment(
                name => name switch
            {
                "FIRMS_MAP_KEY" => MapKey,
                "FIRMS_COUNTRIES" => "UKR",
                "FIRMS_ACTIVE_WINDOW" => "3.00:00:00",
                "NOTIFICATION_EPISODE_RETENTION" => "2.00:00:00",
                _ => null
            }
            )
        );

        Assert.Equal(expected: "NOTIFICATION_EPISODE_RETENTION must be at least FIRMS_ACTIVE_WINDOW.", exception.Message);
    }
}
