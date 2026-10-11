namespace ThermalWatch.Core;

/// <summary>The embedded boundary data cannot supply a usable configured country geometry.</summary>
public sealed class CountryBoundaryException : Exception
{
    /// <summary>Creates a failure with a safe default message.</summary>
    public CountryBoundaryException() : this(safeMessage: "Embedded country boundary data is invalid.")
    {
    }

    /// <summary>Creates a boundary-loading failure with a safe diagnostic message.</summary>
    /// <param name="safeMessage">The boundary validation failure.</param>
    public CountryBoundaryException(string safeMessage) : base(safeMessage)
    {
    }

    /// <summary>Preserves a boundary-loading failure's underlying cause.</summary>
    /// <param name="message">The safe boundary validation failure.</param>
    /// <param name="innerException">The underlying data or geometry failure.</param>
    public CountryBoundaryException(string message, Exception innerException) : base(message, innerException)
    {
    }
}
