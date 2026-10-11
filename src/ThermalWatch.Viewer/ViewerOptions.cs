namespace ThermalWatch.Viewer;

/// <summary>The optional provider configuration exposed to the map viewer.</summary>
/// <param name="GoogleMapsApiKey">The restricted browser key, or null when Google Maps is unavailable.</param>
public sealed record ViewerOptions(string? GoogleMapsApiKey)
{
    /// <summary>Reads the exact uppercase Google Maps environment setting.</summary>
    public static ViewerOptions FromEnvironment(Func<string, string?> getEnvironmentVariable)
    {
        ArgumentNullException.ThrowIfNull(getEnvironmentVariable);

        return new(Normalize(getEnvironmentVariable(arg: "GOOGLE_MAPS_API_KEY")));
    }

    private static string? Normalize(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }
}
