using System.Collections.Immutable;

namespace ThermalWatch.Core;

/// <summary>The validated FIRMS acquisition, retention, and concurrency configuration.</summary>
/// <param name="MapKey">The private FIRMS access key.</param>
/// <param name="CountryCodes">The configured ingestion countries.</param>
/// <param name="PollInterval">The minimum pause between completed polling cycles.</param>
/// <param name="ActiveWindow">The rolling observation retention interval.</param>
/// <param name="RequestTimeout">The admitted request's total timeout, including body reads.</param>
/// <param name="MaximumConcurrency">The maximum simultaneously admitted FIRMS operations.</param>
public sealed record FirmsOptions(
    string MapKey,
    ImmutableArray<string> CountryCodes,
    TimeSpan PollInterval,
    TimeSpan ActiveWindow,
    TimeSpan RequestTimeout,
    int MaximumConcurrency
);
