using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Http.Resilience;
using Microsoft.Extensions.Primitives;
using Polly;
using Serilog;
using Serilog.Events;
using ThermalWatch.Api;
using ThermalWatch.Core;
using ThermalWatch.Telegram;
using ThermalWatch.Viewer;

ApplicationConfiguration configuration;
CountryBoundaryCatalog countryBoundaries;
try
{
    configuration = ApplicationConfiguration.FromEnvironment();
    countryBoundaries = new(configuration.Firms);
}
catch (Exception exception) when (exception is ApplicationConfigurationException or TelegramConfigurationException)
{
    Console.Error.WriteLine(value: $"Configuration error: {exception.Message}");
    return 1;
}
catch (CountryBoundaryException exception)
{
    Console.Error.WriteLine(value: $"Country boundary error: {exception.Message}");
    return 1;
}

Log.Logger = ApplicationLogging
    .Configure(new LoggerConfiguration(), configuration.MinimumLogLevel)
    .WriteTo.Console(formatProvider: CultureInfo.InvariantCulture)
    .CreateLogger();

try
{
    WebApplicationBuilder builder = WebApplication.CreateBuilder(args);
    builder.WebHost.UseStaticWebAssets();
    builder.WebHost.UseUrls("http://0.0.0.0:8080");
    builder.Host.UseSerilog(Log.Logger, dispose: false);

    builder.Services.AddSingleton(configuration.Firms);
    builder.Services.AddSingleton(configuration.Notifications);
    builder.Services.AddSingleton(configuration.Telegram);
    builder.Services.AddSingleton(configuration.Viewer);
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
        .AddStandardResilienceHandler(options => FirmsResilience.Configure(
            options,
            configuration.Firms.RequestTimeout));

    builder.Services
        .AddHttpClient<GibsClient>(client =>
        {
            client.BaseAddress = new("https://gibs.earthdata.nasa.gov/");
            client.Timeout = Timeout.InfiniteTimeSpan;
        })
        .AddStandardResilienceHandler(options => ConfigureResilience(
            options,
            TimeSpan.FromSeconds(seconds: 30),
            TimeSpan.FromSeconds(seconds: 10),
            retryCount: 2));

    builder.Services
        .AddHttpClient<NearbyFeatureClient>(client =>
        {
            client.BaseAddress = new("https://overpass-api.de/api/");
            client.Timeout = TimeSpan.FromSeconds(seconds: 15);
            client.DefaultRequestHeaders.UserAgent.ParseAdd(
                input: "ThermalWatch/1.0 (+https://github.com/caunt/ThermalWatch)");
            client.DefaultRequestHeaders.Accept.ParseAdd(input: "application/json");
        });

    builder.Services
        .AddHttpClient(name: "GibsMapTiles", client =>
        {
            client.BaseAddress = new(uriString: "https://gibs.earthdata.nasa.gov/");
            client.Timeout = Timeout.InfiniteTimeSpan;
        })
        .AddStandardResilienceHandler(options => ConfigureResilience(
            options,
            TimeSpan.FromSeconds(seconds: 30),
            TimeSpan.FromSeconds(seconds: 10),
            retryCount: 2));
    builder.Services.AddSingleton(serviceProvider => new GibsMapTileClient(
        serviceProvider.GetRequiredService<IHttpClientFactory>().CreateClient(name: "GibsMapTiles"),
        serviceProvider.GetRequiredService<IMemoryCache>(),
        serviceProvider.GetRequiredService<ILogger<GibsMapTileClient>>()));
    builder.Services.AddSingleton<NotificationCandidateEngine>();

    builder.Services
        .AddHttpClient(name: "Telegram", client => client.Timeout = Timeout.InfiniteTimeSpan)
        .AddStandardResilienceHandler(options => ConfigureResilience(
            options,
            TimeSpan.FromSeconds(seconds: 30),
            TimeSpan.FromSeconds(seconds: 10),
            retryCount: 1));

    builder.Services.AddHostedService<FirmsPollingService>();
    builder.Services.AddSingleton<TelegramNotificationService>();
    if (configuration.Telegram.IsEnabled)
    {
        builder.Services.AddHostedService(serviceProvider =>
            serviceProvider.GetRequiredService<TelegramNotificationService>());
    }
    else if (configuration.Telegram.IsPartiallyConfigured)
    {
        Log.Warning(messageTemplate: "Telegram notifications disabled: both TELEGRAM_BOT_TOKEN and TELEGRAM_CHANNEL_ID are required");
    }
    else
    {
        Log.Information(messageTemplate: "Telegram notifications disabled: no Telegram configuration was provided");
    }

    WebApplication app = builder.Build();
    app.UseSerilogRequestLogging(options => options.GetLevel =
        (httpContext, _, exception) => exception is not null
            ? LogEventLevel.Error
            : httpContext.Request.Path.StartsWithSegments(other: "/api/viewer/imagery/gibs", StringComparison.OrdinalIgnoreCase)
                ? LogEventLevel.Debug
                : LogEventLevel.Information);
    app.UseCors();
    app.UseDefaultFiles();
    app.UseStaticFiles(new StaticFileOptions
    {
        OnPrepareResponse = context =>
            context.Context.Response.Headers.CacheControl = "no-cache, must-revalidate"
    });

    app.MapThermalWatchViewer();

    app.MapGet(pattern: "/api/anomalies", (HttpRequest request, AnomalySnapshotStore store, FirmsOptions firmsOptions) =>
    {
        AnomalySnapshot snapshot = store.Current;
        if (!AnomalyQuery.TryParse(
                request.Query,
                snapshot.GeneratedAtUtc - firmsOptions.ActiveWindow,
                out AnomalyQuery? query,
                out string? error))
        {
            return Results.BadRequest(new { error });
        }

        ImmutableArray<Anomaly> items = query!.Apply(snapshot.Items);
        return Results.Ok(snapshot with { Count = items.Length, Items = items });
    });

    app.MapGet(pattern: "/api/telegram/send-top", async (
        HttpRequest request,
        TelegramNotificationService telegram,
        CancellationToken cancellationToken) =>
    {
        if (!TryParseManualSendCount(request.Query, out int count))
        {
            return Results.BadRequest(new
            {
                error = "count must be one integer between 1 and 50."
            });
        }

        ManualTelegramSendResult result = await telegram.SendTopAsync(count, cancellationToken).ConfigureAwait(false);
        return result.Status switch
        {
            ManualTelegramSendStatus.Completed => Results.Ok(result.Response),
            ManualTelegramSendStatus.TelegramUnavailable => Results.Conflict(new
            {
                error = "Telegram is disabled or not validated."
            }),
            ManualTelegramSendStatus.AlreadyRunning => Results.Conflict(new
            {
                error = "A manual Telegram send is already running."
            }),
            ManualTelegramSendStatus.StatusMessageFailed => Results.Json(
                new { error = "The Telegram status message could not be sent." },
                statusCode: StatusCodes.Status502BadGateway),
            _ => Results.StatusCode(StatusCodes.Status500InternalServerError)
        };
    });

    await app.RunAsync().ConfigureAwait(false);
    return 0;
}
finally
{
    await Log.CloseAndFlushAsync().ConfigureAwait(false);
}

static void ConfigureResilience(
    HttpStandardResilienceOptions options,
    TimeSpan totalTimeout,
    TimeSpan attemptTimeout,
    int retryCount)
{
    options.TotalRequestTimeout.Timeout = totalTimeout;
    options.AttemptTimeout.Timeout = attemptTimeout;
    options.Retry.MaxRetryAttempts = retryCount;
    options.Retry.Delay = TimeSpan.FromSeconds(seconds: 1);
    options.Retry.BackoffType = DelayBackoffType.Exponential;
    options.Retry.UseJitter = true;
    options.Retry.ShouldRetryAfterHeader = true;
}

static bool TryParseManualSendCount(IQueryCollection query, out int count)
{
    count = 5;
    if (!query.TryGetValue(key: "count", out StringValues values))
        return true;

    return values.Count == 1
        && values[0] is { } value
        && int.TryParse(
            value,
            NumberStyles.None,
            CultureInfo.InvariantCulture,
            out count)
        && count is >= 1 and <= 50;
}
