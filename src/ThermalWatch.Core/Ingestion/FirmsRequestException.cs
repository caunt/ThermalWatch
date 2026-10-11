namespace ThermalWatch.Core;

/// <summary>A FIRMS acquisition failure with a credential-free diagnostic message.</summary>
public sealed class FirmsRequestException : Exception
{
    /// <summary>Creates a failure with a safe default message.</summary>
    public FirmsRequestException() : this(safeMessage: "FIRMS request failed.")
    {
    }

    /// <summary>Creates a failure with a message safe for operator diagnostics.</summary>
    /// <param name="safeMessage">The message without credentials or provider request URLs.</param>
    public FirmsRequestException(string safeMessage) : base(safeMessage)
    {
    }

    /// <summary>Preserves an underlying failure with a safe diagnostic message.</summary>
    /// <param name="message">The credential-free diagnostic message.</param>
    /// <param name="innerException">The underlying failure.</param>
    public FirmsRequestException(string message, Exception innerException) : base(message, innerException)
    {
    }

    /// <summary>Gets the credential-free message used in segment status and logs.</summary>
    public string SafeMessage => Message;
}
