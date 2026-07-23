namespace ThermalWatch.Core;

public sealed class CountryBoundaryException(string safeMessage) : Exception(safeMessage);
