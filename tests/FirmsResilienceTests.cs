using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http.Resilience;
using ThermalWatch.Api;

namespace ThermalWatch.Tests;

public sealed class FirmsResilienceTests
{
    [Fact]
    public void ConfigureUsesOneRetryAndCalculatedAttemptTimeout()
    {
        var options = new HttpStandardResilienceOptions();
        var totalTimeout = TimeSpan.FromSeconds(seconds: 45);

        FirmsResilience.Configure(options, totalTimeout);

        Assert.Equal(totalTimeout, options.TotalRequestTimeout.Timeout);
        Assert.Equal(TimeSpan.FromSeconds(seconds: 18), options.AttemptTimeout.Timeout);
        Assert.Equal(TimeSpan.FromSeconds(seconds: 36), options.CircuitBreaker.SamplingDuration);
        Assert.Equal(1, options.Retry.MaxRetryAttempts);
        Assert.True(options.Retry.UseJitter);
        Assert.True(options.Retry.ShouldRetryAfterHeader);
    }

    [Theory]
    [InlineData(5)]
    [InlineData(45)]
    [InlineData(300)]
    public void ConfigureBuildsValidStandardPipeline(int totalSeconds)
    {
        var services = new ServiceCollection();
        services
            .AddHttpClient(name: "FirmsTest")
            .AddStandardResilienceHandler(options => FirmsResilience.Configure(
                options,
                TimeSpan.FromSeconds(totalSeconds)));
        using ServiceProvider serviceProvider = services.BuildServiceProvider();

        using HttpClient client = serviceProvider
            .GetRequiredService<IHttpClientFactory>()
            .CreateClient(name: "FirmsTest");

        Assert.NotNull(client);
    }

    [Theory]
    [InlineData(45, 18)]
    [InlineData(300, 120)]
    [InlineData(1, 1)]
    public void CalculateAttemptTimeoutUsesFortyPercentWithoutFixedCeiling(
        double totalSeconds,
        double expectedSeconds) =>
        Assert.Equal(
            TimeSpan.FromSeconds(expectedSeconds),
            FirmsResilience.CalculateAttemptTimeout(TimeSpan.FromSeconds(totalSeconds)));
}
