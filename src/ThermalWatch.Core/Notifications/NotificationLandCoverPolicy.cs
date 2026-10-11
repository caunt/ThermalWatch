namespace ThermalWatch.Core;

/// <summary>Applies vegetation suppression and its configured exceptions, failing open on unavailable land cover.</summary>
public static class NotificationLandCoverPolicy
{
    /// <summary>Evaluates validated land-cover samples independently of notification delivery history.</summary>
    /// <param name="cluster">The current observation cluster.</param>
    /// <param name="options">The vegetation and built-up proximity policy.</param>
    /// <param name="landCover">The NASA sample result.</param>
    public static NotificationLandCoverResult Evaluate(NotificationCluster cluster, NotificationLandCoverOptions options, GibsLandCoverResult landCover)
    {
        ArgumentNullException.ThrowIfNull(cluster);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(landCover);

        if (!landCover.IsAvailable)
            return NotificationLandCoverResult.Unavailable(landCover.Year, reason: "NASA land-cover data unavailable");

        if (landCover.Year is not { } landCoverYear || landCover.SampledClasses.IsDefaultOrEmpty)
            return NotificationLandCoverResult.Unavailable(landCover.Year, reason: "NASA land-cover data invalid");

        int vegetationCount = 0;

        foreach (byte landCoverClass in landCover.SampledClasses)
        {
            if (landCoverClass is < 1 or > 17)
                return NotificationLandCoverResult.Unavailable(landCover.Year, reason: "NASA land-cover data invalid");

            if (landCoverClass is (>= 1 and <= 12) or 14)
                vegetationCount++;
        }

        double vegetationPercent = vegetationCount * 100d / landCover.SampledClasses.Length;
        string formattedPercent = NotificationPolicy.FormatNumber(vegetationPercent);

        string formattingSummary = landCover.HasBuiltUpWithinProximity
            ? $"Urban/built-up nearby · vegetation {formattedPercent}%"
            : $"Vegetation · {formattedPercent}%";

        if (vegetationPercent < options.VegetationPercentThreshold)
        {
            return NotificationLandCoverResult.Retained(
                landCoverYear,
                vegetationPercent,
                landCover.HasBuiltUpWithinProximity,
                $"Vegetation {formattedPercent}% is below {NotificationPolicy.FormatNumber(options.VegetationPercentThreshold)}%",
                formattingSummary
            );
        }

        if (landCover.HasBuiltUpWithinProximity)
        {
            return NotificationLandCoverResult.Retained(
                landCoverYear,
                vegetationPercent,
                hasBuiltUpWithinProximity: true,
                $"NASA class 13 is within {NotificationPolicy.FormatNumber(options.BuiltUpProximityKilometers)} km",
                formattingSummary
            );
        }

        if (options.KeepHighFrpVegetation && cluster.Representative.FrpMegawatts is { } frp)
        {
            if (frp >= options.VegetationMaximumFrpMegawatts)
            {
                return NotificationLandCoverResult.Retained(
                    landCoverYear,
                    vegetationPercent,
                    hasBuiltUpWithinProximity: false,
                    $"Configured high-FRP vegetation exception retained {NotificationPolicy.FormatNumber(frp)} MW",
                    formattingSummary
                );
            }
        }

        bool hasMultipleSatellites = cluster.Members
            .Select(static member => member.Satellite)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Skip(count: 1)
            .Any();

        return options.KeepMultiSatelliteVegetation && hasMultipleSatellites
            ? NotificationLandCoverResult.Retained(
                landCoverYear,
                vegetationPercent,
                hasBuiltUpWithinProximity: false,
                reason: "Configured multi-satellite vegetation exception retained cluster",
                formattingSummary
            )
            : NotificationLandCoverResult.Suppressed(
            landCoverYear,
            vegetationPercent,
            $"Vegetation {formattedPercent}% meets {NotificationPolicy.FormatNumber(options.VegetationPercentThreshold)}%; no NASA class 13 within {NotificationPolicy.FormatNumber(options.BuiltUpProximityKilometers)} km",
            formattingSummary
        );
    }

    /// <summary>Fetches NASA samples for all members before applying the vegetation policy.</summary>
    /// <param name="cluster">The current observation cluster.</param>
    /// <param name="options">The vegetation and built-up proximity policy.</param>
    /// <param name="gibsClient">The cached NASA imagery and land-cover client.</param>
    /// <param name="cancellationToken">Cancels provider work.</param>
    public static async Task<NotificationLandCoverResult> EvaluateAsync(NotificationCluster cluster, NotificationLandCoverOptions options, GibsClient gibsClient, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(cluster);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(gibsClient);

        GibsLandCoverResult landCover = await gibsClient.GetLandCoverAsync(cluster.Members, options.BuiltUpProximityKilometers, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);

        return Evaluate(cluster, options, landCover);
    }
}
