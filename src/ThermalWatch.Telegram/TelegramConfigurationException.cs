namespace ThermalWatch.Telegram;

/// <summary>A credential-free explanation of invalid Telegram configuration.</summary>
public sealed class TelegramConfigurationException : Exception
{
    /// <summary>Creates an unspecified configuration failure.</summary>
    public TelegramConfigurationException() { }

    /// <summary>Creates a configuration failure with a safe explanation.</summary>
    public TelegramConfigurationException(string safeMessage) : base(safeMessage) { }

    /// <summary>Creates a configuration failure retaining its underlying cause.</summary>
    public TelegramConfigurationException(string safeMessage, Exception innerException) : base(safeMessage, innerException) { }
}
