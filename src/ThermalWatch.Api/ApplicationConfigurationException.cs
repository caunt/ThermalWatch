namespace ThermalWatch.Api;

internal sealed class ApplicationConfigurationException : Exception
{
    public ApplicationConfigurationException() { }

    public ApplicationConfigurationException(string safeMessage) : base(safeMessage) { }

    public ApplicationConfigurationException(string safeMessage, Exception innerException) : base(safeMessage, innerException) { }
}
