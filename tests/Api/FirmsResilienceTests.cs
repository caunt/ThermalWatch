using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http.Resilience;

using ThermalWatch.Api;

namespace ThermalWatch.Tests.Api;

/// <summary>Verifies FIRMS retry and timeout pipeline configuration.</summary>
public sealed class FirmsResilienceTests
{
    /// <summary>Provides proportional timeout and minimum timeout cases.</summary>
    public static TheoryData<double, double> AttemptTimeoutCases => new()
    {
        { 45, 18 },
        { 300, 120 },
        { 1, 1 }
    };

    /// <summary>Provides supported total timeout boundaries and a normal configuration.</summary>
    public static TheoryData<int> PipelineTimeoutCases => [5, 45, 300];

    /// <summary>Uses forty percent of the total timeout with a one-second minimum.</summary>
    [Theory]
    [MemberData(nameof(AttemptTimeoutCases))]
    public void CalculateAttemptTimeoutUsesFortyPercentWithoutFixedCeiling(double totalSeconds, double expectedSeconds)
    {
        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), FirmsResilience.CalculateAttemptTimeout(TimeSpan.FromSeconds(totalSeconds)));
    }

    /// <summary>Builds a valid standard pipeline throughout the supported timeout range.</summary>
    [Theory]
    [MemberData(nameof(PipelineTimeoutCases))]
    public void ConfigureBuildsValidStandardPipeline(int totalSeconds)
    {
        var services = new ServiceCollection();

        IHttpStandardResiliencePipelineBuilder resilienceBuilder = services
            .AddHttpClient(name: "FirmsTest")
            .AddStandardResilienceHandler(options => FirmsResilience.Configure(options, TimeSpan.FromSeconds(totalSeconds)));

        using ServiceProvider serviceProvider = resilienceBuilder.Services.BuildServiceProvider();

        using HttpClient client = serviceProvider
            .GetRequiredService<IHttpClientFactory>()
            .CreateClient(name: "FirmsTest");

        Assert.NotNull(client);
    }

    /// <summary>Configures one jittered retry and compatible attempt and circuit-breaker timings.</summary>
    [Fact]
    public void ConfigureUsesOneRetryAndCalculatedAttemptTimeout()
    {
        var options = new HttpStandardResilienceOptions();
        var totalTimeout = TimeSpan.FromSeconds(seconds: 45);

        FirmsResilience.Configure(options, totalTimeout);

        Assert.Equal(totalTimeout, options.TotalRequestTimeout.Timeout);
        Assert.Equal(TimeSpan.FromSeconds(seconds: 18), options.AttemptTimeout.Timeout);
        Assert.Equal(TimeSpan.FromSeconds(seconds: 36), options.CircuitBreaker.SamplingDuration);
        Assert.Equal(expected: 1, options.Retry.MaxRetryAttempts);
        Assert.True(options.Retry.UseJitter);
        Assert.True(options.Retry.ShouldRetryAfterHeader);
    }
}
