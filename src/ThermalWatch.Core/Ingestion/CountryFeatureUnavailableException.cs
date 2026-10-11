namespace ThermalWatch.Core;

/// <summary>A verified FIRMS country-feature outage that permits atomic area fallback.</summary>
public sealed class CountryFeatureUnavailableException : Exception
{
    /// <summary>Creates a verified country-feature outage.</summary>
    public CountryFeatureUnavailableException() : this(message: "The FIRMS country feature is unavailable.")
    {
    }

    /// <summary>Creates an outage with its diagnostic description.</summary>
    /// <param name="message">The credential-free outage description.</param>
    public CountryFeatureUnavailableException(string message) : base(message)
    {
    }

    /// <summary>Preserves the underlying cause of the verified outage.</summary>
    /// <param name="message">The credential-free outage description.</param>
    /// <param name="innerException">The underlying failure.</param>
    public CountryFeatureUnavailableException(string message, Exception innerException) : base(message, innerException)
    {
    }
}
