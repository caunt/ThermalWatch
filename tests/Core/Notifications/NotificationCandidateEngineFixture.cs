using System.Net;

using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;

using ThermalWatch.Core;

namespace ThermalWatch.Tests.Core.Notifications;

internal sealed class NotificationCandidateEngineFixture : IDisposable
{
    private readonly HttpClient _imagery;
    private readonly HttpMessageHandler? _ownedPlacesHandler;
    private readonly HttpClient _places;

    internal NotificationCandidateEngineFixture(
        HttpMessageHandler imageryHandler,
        IMemoryCache cache,
        HttpMessageHandler? nearbyHandler,
        NotificationOptions options,
        TimeProvider timeProvider,
        FirmsHistoryStore? historyStore
    )
    {
        _imagery = new(imageryHandler, disposeHandler: false) { BaseAddress = new(uriString: "https://gibs.example.test/") };

        if (nearbyHandler is null)
        {
            _ownedPlacesHandler = new UnavailablePlacesHandler();
            _places = new(_ownedPlacesHandler, disposeHandler: false);
        }
        else
        {
            _places = new(nearbyHandler, disposeHandler: false);
        }

        _places.BaseAddress = new(uriString: "https://overpass.example.test/api/");
        Engine = new(
            options,
            new GibsClient(_imagery, cache, NullLogger<GibsClient>.Instance),
            new NearbyFeatureClient(_places, cache, NullLogger<NearbyFeatureClient>.Instance),
            timeProvider,
            historyStore
        );
    }

    internal NotificationCandidateEngine Engine { get; }

    public void Dispose()
    {
        _places.Dispose();
        _imagery.Dispose();
        _ownedPlacesHandler?.Dispose();
    }

    internal static NotificationOptions DefaultOptions()
    {
        return new(
            SendExistingOnStartup: true,
            ClusterRadiusKilometers: 5,
            TimeSpan.FromMinutes(minutes: 90),
            TimeSpan.FromHours(hours: 48),
            HistoricalFrpFilterEnabled: false,
            new(
                new(WidthKilometers: 30, HeightKilometers: 20),
                new(WidthKilometers: 45, HeightKilometers: 30),
                PixelWidth: 900,
                PixelHeight: 600,
                LargeClusterMinimumDetections: 8,
                LargeClusterMinimumFrpMegawatts: 500,
                LargeClusterMinimumDiameterKilometers: 8
            ),
            new(
                Enabled: false,
                VegetationPercentThreshold: 50,
                BuiltUpProximityKilometers: 2,
                VegetationMaximumFrpMegawatts: 300,
                KeepHighFrpVegetation: false,
                KeepMultiSatelliteVegetation: false
            ),
            new(
                Enabled: true,
                MinimumFrpMegawatts: 50,
                MinimumClusterTotalFrpMegawatts: 50,
                MinimumThermalContrastKelvin: 20,
                MinimumClusterDetections: 2,
                MinimumModisConfidencePercent: 60,
                NotificationViirsConfidenceLevel.Nominal,
                RequireDaytime: true,
                RequirePreview: false
            )
        );
    }

    private sealed class UnavailablePlacesHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }
}
