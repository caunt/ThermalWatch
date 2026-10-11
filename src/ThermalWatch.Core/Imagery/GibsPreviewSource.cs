namespace ThermalWatch.Core;

/// <summary>The source of the contextual base imagery used beneath the representative's thermal overlay.</summary>
/// <param name="FirmsSource">The corresponding FIRMS dataset.</param>
/// <param name="Satellite">The base-imagery satellite.</param>
/// <param name="Instrument">The base-imagery instrument.</param>
public readonly record struct GibsPreviewSource(string FirmsSource, string Satellite, string Instrument);
