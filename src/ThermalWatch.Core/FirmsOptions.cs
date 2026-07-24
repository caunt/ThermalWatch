using System.Collections.Immutable;

namespace ThermalWatch.Core;

public sealed record FirmsOptions(
    string MapKey,
    ImmutableArray<string> CountryCodes,
    TimeSpan PollInterval,
    TimeSpan ActiveWindow,
    TimeSpan RequestTimeout,
    int MaxConcurrency);
