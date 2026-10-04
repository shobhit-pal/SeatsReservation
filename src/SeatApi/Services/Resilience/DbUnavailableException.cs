namespace SeatApi.Services.Resilience;

/// <summary>
/// Thrown when the database is unavailable or exhausted the retry window.
/// Handled by ErrorHandlingMiddleware to return HTTP 503.
/// </summary>
public class DbUnavailableException : Exception
{
    public DbUnavailableException(string message) : base(message) { }

    public DbUnavailableException(string message, Exception innerException) : base(message, innerException) { }
}
