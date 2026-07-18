using System.Text.Json;
using Microsoft.Extensions.Http.Resilience;
using Polly;
using Serilog;
using Serilog.Events;
using ThermalWatch.Api;
using ThermalWatch.Core;
using ThermalWatch.Telegram;

ApplicationConfiguration configuration;
CountryBoundaryCatalog countryBoundaries;
try
{
    configuration = ApplicationConfiguration.FromEnvironment();
    countryBoundaries = new(configuration.Firms);
}
catch (Exception exception) when (exception is ApplicationConfigurationException or TelegramConfigurationException)
{
    Console.Error.WriteLine($"Configuration error: {exception.Message}");
    return 1;
}
catch (CountryBoundaryException exception)
{
    Console.Error.WriteLine($"Country boundary error: {exception.Message}");
    return 1;
}

Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Is(configuration.MinimumLogLevel)
    .MinimumLevel.Override("Microsoft.AspNetCore", LogEventLevel.Warning)
    .MinimumLevel.Override("Microsoft.Extensions.Http", LogEventLevel.Fatal)
    .MinimumLevel.Override("Polly", LogEventLevel.Warning)
    .MinimumLevel.Override("System.Net.Http.HttpClient", LogEventLevel.Fatal)
    .Enrich.FromLogContext()
    .WriteTo.Console()
    .CreateLogger();

try
{
    var builder = WebApplication.CreateBuilder(args);
    builder.WebHost.UseUrls("http://0.0.0.0:8080");
    builder.Host.UseSerilog(Log.Logger, dispose: false);

    builder.Services.AddSingleton(configuration.Firms);
    builder.Services.AddSingleton(configuration.Telegram);
    builder.Services.AddSingleton(countryBoundaries);
    builder.Services.AddSingleton(TimeProvider.System);
    builder.Services.AddSingleton<AnomalySnapshotStore>();
    builder.Services.AddMemoryCache(options => options.SizeLimit = 64 * 1024 * 1024);
    builder.Services.ConfigureHttpJsonOptions(options =>
    {
        options.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
        options.SerializerOptions.DictionaryKeyPolicy = JsonNamingPolicy.CamelCase;
    });
    builder.Services.AddCors(options => options.AddDefaultPolicy(policy =>
        policy.AllowAnyOrigin().WithMethods("GET").AllowAnyHeader()));

    builder.Services
        .AddHttpClient<FirmsClient>(client =>
        {
            client.BaseAddress = new("https://firms.modaps.eosdis.nasa.gov/");
            client.Timeout = Timeout.InfiniteTimeSpan;
        })
        .AddStandardResilienceHandler(options => ConfigureResilience(
            options,
            configuration.Firms.RequestTimeout,
            AttemptTimeout(configuration.Firms.RequestTimeout),
            2));

    builder.Services
        .AddHttpClient<GibsClient>(client =>
        {
            client.BaseAddress = new("https://gibs.earthdata.nasa.gov/");
            client.Timeout = Timeout.InfiniteTimeSpan;
        })
        .AddStandardResilienceHandler(options => ConfigureResilience(
            options,
            TimeSpan.FromSeconds(30),
            TimeSpan.FromSeconds(10),
            2));

    builder.Services
        .AddHttpClient("Telegram", client => client.Timeout = Timeout.InfiniteTimeSpan)
        .AddStandardResilienceHandler(options => ConfigureResilience(
            options,
            TimeSpan.FromSeconds(30),
            TimeSpan.FromSeconds(10),
            1));

    builder.Services.AddHostedService<FirmsPollingService>();
    if (configuration.Telegram.IsEnabled)
    {
        builder.Services.AddHostedService<TelegramNotificationService>();
    }
    else if (configuration.Telegram.IsPartiallyConfigured)
    {
        Log.Warning("Telegram notifications disabled: both TELEGRAM_BOT_TOKEN and TELEGRAM_CHANNEL_ID are required");
    }
    else
    {
        Log.Information("Telegram notifications disabled: no Telegram configuration was provided");
    }

    var app = builder.Build();
    app.UseSerilogRequestLogging();
    app.UseCors();

    app.MapGet("/api/anomalies", (HttpRequest request, AnomalySnapshotStore store, FirmsOptions firmsOptions) =>
    {
        var snapshot = store.Current;
        if (!AnomalyQuery.TryParse(
                request.Query,
                snapshot.GeneratedAtUtc - firmsOptions.ActiveWindow,
                out var query,
                out var error))
        {
            return Results.BadRequest(new { error });
        }

        var items = query!.Apply(snapshot.Items);
        return Results.Ok(snapshot with { Count = items.Length, Items = items });
    });

    await app.RunAsync();
    return 0;
}
finally
{
    await Log.CloseAndFlushAsync();
}

static TimeSpan AttemptTimeout(TimeSpan totalTimeout) =>
    TimeSpan.FromSeconds(Math.Clamp(totalTimeout.TotalSeconds / 3, 1, 15));

static void ConfigureResilience(
    HttpStandardResilienceOptions options,
    TimeSpan totalTimeout,
    TimeSpan attemptTimeout,
    int retryCount)
{
    options.TotalRequestTimeout.Timeout = totalTimeout;
    options.AttemptTimeout.Timeout = attemptTimeout;
    options.Retry.MaxRetryAttempts = retryCount;
    options.Retry.Delay = TimeSpan.FromSeconds(1);
    options.Retry.BackoffType = DelayBackoffType.Exponential;
    options.Retry.UseJitter = true;
    options.Retry.ShouldRetryAfterHeader = true;
}
