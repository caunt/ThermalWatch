namespace ThermalWatch.Core;

public sealed class FirmsRequestException(string safeMessage) : Exception(safeMessage)
{
    public string SafeMessage { get; } = safeMessage;
}
