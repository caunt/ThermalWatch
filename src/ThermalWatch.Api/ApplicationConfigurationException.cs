namespace ThermalWatch.Api;

public sealed class ApplicationConfigurationException(string safeMessage) : Exception(safeMessage);
