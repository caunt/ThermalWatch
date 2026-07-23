namespace ThermalWatch.Viewer;

public sealed record ViewerOptions(string? GoogleMapsApiKey)
{
    public static ViewerOptions FromEnvironment(Func<string, string?> getEnvironmentVariable) =>
        new(Normalize(getEnvironmentVariable("GOOGLE_MAPS_API_KEY")));

    private static string? Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
