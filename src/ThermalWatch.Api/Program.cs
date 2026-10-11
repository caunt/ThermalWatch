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
using ThermalWatch.Api.Polling;
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
    await Console.Error.WriteLineAsync($"Configuration error: {exception.Message}").ConfigureAwait(continueOnCapturedContext: false);

    return 1;
}
catch (CountryBoundaryException exception)
{
    await Console.Error.WriteLineAsync($"Country boundary error: {exception.Message}").ConfigureAwait(continueOnCapturedContext: false);

    return 1;
}

Log.Logger = ApplicationLogging
    .Configure(new LoggerConfiguration(), configuration.MinimumLogLevel)
    .WriteTo.Console(formatProvider: CultureInfo.InvariantCulture)
    .CreateLogger();

try
{
    WebApplicationBuilder builder = WebApplication.CreateBuilder(args);
    ConfigureWebHostBuilder webHost = (ConfigureWebHostBuilder)builder.WebHost.UseStaticWebAssets();
    webHost = (ConfigureWebHostBuilder)webHost.UseUrls(urls: ["http://0.0.0.0:8080"]);

    IServiceCollection services = builder.Services
        .AddSerilog(Log.Logger, dispose: false)
        .AddSingleton(configuration.Firms)
        .AddSingleton(configuration.Notifications)
        .AddSingleton(configuration.Telegram)
        .AddSingleton(configuration.Viewer)
        .AddSingleton(countryBoundaries)
        .AddSingleton(TimeProvider.System)
        .AddSingleton<AnomalySnapshotStore>()
        .AddSingleton<FirmsHistoryStore>()
        .AddMemoryCache(options => options.SizeLimit = 64 * 1024 * 1024);

    services = services.ConfigureHttpJsonOptions(
        options =>
    {
        options.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
        options.SerializerOptions.DictionaryKeyPolicy = JsonNamingPolicy.CamelCase;
    }
    );
    services = services.AddCors(options => options.AddDefaultPolicy(policy => policy = policy.AllowAnyOrigin().WithMethods(methods: ["GET"]).AllowAnyHeader()));

    IHttpClientBuilder firmsHttpClient = services
        .AddHttpClient<FirmsClient>(
            client =>
        {
            client.BaseAddress = new(uriString: "https://firms.modaps.eosdis.nasa.gov/");
            client.Timeout = Timeout.InfiniteTimeSpan;
        }
        );

    IHttpStandardResiliencePipelineBuilder firmsResilience = firmsHttpClient.AddStandardResilienceHandler();
    firmsResilience = firmsResilience.Configure(options => FirmsResilience.Configure(options, configuration.Firms.RequestTimeout));

    IHttpClientBuilder gibsHttpClient = services
        .AddHttpClient<GibsClient>(
            client =>
        {
            client.BaseAddress = new(uriString: "https://gibs.earthdata.nasa.gov/");
            client.Timeout = Timeout.InfiniteTimeSpan;
        }
        );

    IHttpStandardResiliencePipelineBuilder gibsResilience = gibsHttpClient.AddStandardResilienceHandler();
    gibsResilience = gibsResilience.Configure(options => ConfigureResilience(options, TimeSpan.FromSeconds(seconds: 30), TimeSpan.FromSeconds(seconds: 10), retryCount: 2));

    IHttpClientBuilder nearbyHttpClient = services
        .AddHttpClient<NearbyFeatureClient>(
            client =>
        {
            client.BaseAddress = new(uriString: "https://overpass-api.de/api/");
            client.Timeout = TimeSpan.FromSeconds(seconds: 15);
            client.DefaultRequestHeaders.UserAgent.ParseAdd(input: "ThermalWatch/1.0 (+https://github.com/caunt/ThermalWatch)");
            client.DefaultRequestHeaders.Accept.ParseAdd(input: "application/json");
        }
        );

    services = nearbyHttpClient.Services;

    IHttpClientBuilder mapHttpClient = services
        .AddHttpClient(
            name: "GibsMapTiles",
            client =>
        {
            client.BaseAddress = new(uriString: "https://gibs.earthdata.nasa.gov/");
            client.Timeout = Timeout.InfiniteTimeSpan;
        }
        );

    IHttpStandardResiliencePipelineBuilder mapResilience = mapHttpClient.AddStandardResilienceHandler();
    mapResilience = mapResilience.Configure(options => ConfigureResilience(options, TimeSpan.FromSeconds(seconds: 30), TimeSpan.FromSeconds(seconds: 10), retryCount: 2));

    services = services.AddSingleton(
        serviceProvider => new GibsMapTileClient(
            serviceProvider.GetRequiredService<IHttpClientFactory>().CreateClient(name: "GibsMapTiles"),
            serviceProvider.GetRequiredService<IMemoryCache>(),
            serviceProvider.GetRequiredService<TimeProvider>(),
            serviceProvider.GetRequiredService<ILogger<GibsMapTileClient>>()
        )
    );
    services = services.AddSingleton<NotificationCandidateEngine>();

    IHttpClientBuilder telegramHttpClient = services.AddHttpClient(name: "Telegram", client => client.Timeout = Timeout.InfiniteTimeSpan);
    IHttpStandardResiliencePipelineBuilder telegramResilience = telegramHttpClient.AddStandardResilienceHandler();
    telegramResilience = telegramResilience.Configure(options => ConfigureResilience(options, TimeSpan.FromSeconds(seconds: 30), TimeSpan.FromSeconds(seconds: 10), retryCount: 1));

    services = services.AddHostedService<FirmsPollingService>().AddSingleton<TelegramNotificationService>();

    if (configuration.Telegram.IsEnabled)
        services = services.AddHostedService(serviceProvider => serviceProvider.GetRequiredService<TelegramNotificationService>());
    else if (configuration.Telegram.IsPartiallyConfigured)
        Log.Warning(messageTemplate: "Telegram notifications disabled: both TELEGRAM_BOT_TOKEN and TELEGRAM_CHANNEL_ID are required");
    else
        Log.Information(messageTemplate: "Telegram notifications disabled: no Telegram configuration was provided");

    WebApplication app = builder.Build();

    IApplicationBuilder pipeline = app.UseSerilogRequestLogging(
        options => options.GetLevel =
        (httpContext, elapsed, exception) => exception is not null
            ? LogEventLevel.Error
            : httpContext.Request.Path.StartsWithSegments(other: "/api/viewer/imagery/gibs", StringComparison.OrdinalIgnoreCase)
                ? LogEventLevel.Debug
                : LogEventLevel.Information
    );

    pipeline = pipeline.UseCors().UseDefaultFiles().UseStaticFiles(
        new StaticFileOptions
        {
            OnPrepareResponse = context =>
                context.Context.Response.Headers.CacheControl = "no-cache, must-revalidate"
        }
    );

    IEndpointRouteBuilder routes = app.MapThermalWatchViewer().MapFirmsHistory();

    RouteHandlerBuilder anomaliesEndpoint = routes.MapGet(
        pattern: "/api/anomalies",
        (HttpRequest request, AnomalySnapshotStore store, FirmsOptions firmsOptions) =>
    {
        AnomalySnapshot snapshot = store.Current;

        DateTimeOffset cutoff = snapshot.GeneratedAtUtc - firmsOptions.ActiveWindow;

        if (!AnomalyQuery.TryParse(request.Query, cutoff, out AnomalyQuery? query, out string? error))
            return Results.BadRequest(new { error });

        ImmutableArray<Anomaly> anomalies = query.Apply(snapshot.Anomalies);

        return Results.Ok(snapshot with { AnomalyCount = anomalies.Length, Anomalies = anomalies });
    }
    );

    anomaliesEndpoint.Add(static endpoint => endpoint.DisplayName = "Active FIRMS anomalies");

    RouteHandlerBuilder manualSendEndpoint = routes.MapGet(
        pattern: "/api/telegram/send-top",
        async (HttpRequest request, TelegramNotificationService telegram, CancellationToken cancellationToken) =>
    {
        if (!TryParseManualSendCount(request.Query, out int count))
            return Results.BadRequest(new { error = "count must be one integer between 1 and 50." });

        ManualTelegramSendResult result = await telegram.SendTopClustersAsync(count, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);

        return result.Status switch
        {
            ManualTelegramSendStatus.Completed => Results.Ok(result.Response),
            ManualTelegramSendStatus.TelegramUnavailable => Results.Conflict(new { error = "Telegram is disabled or not validated." }),
            ManualTelegramSendStatus.AlreadyRunning => Results.Conflict(new { error = "A manual Telegram send is already running." }),
            ManualTelegramSendStatus.StatusMessageFailed => Results.Json(new { error = "The Telegram status message could not be sent." }, statusCode: StatusCodes.Status502BadGateway),
            _ => Results.StatusCode(StatusCodes.Status500InternalServerError)
        };
    }
    );

    manualSendEndpoint.Add(static endpoint => endpoint.DisplayName = "Manual Telegram cluster delivery");

    await app.RunAsync().ConfigureAwait(continueOnCapturedContext: false);

    return 0;
}
finally
{
    await Log.CloseAndFlushAsync().ConfigureAwait(continueOnCapturedContext: false);
}

static void ConfigureResilience(HttpStandardResilienceOptions options, TimeSpan totalTimeout, TimeSpan attemptTimeout, int retryCount)
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

    return !query.TryGetValue(key: "count", out StringValues values) || (values.Count == 1
        && values[index: 0] is { } value
        && int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out count)
        && count is >= 1 and <= 50);
}
