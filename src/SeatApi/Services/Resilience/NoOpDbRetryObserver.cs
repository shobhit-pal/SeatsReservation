namespace SeatApi.Services.Resilience;

/// <summary>
/// Default no-op implementation of IDbRetryObserver.
/// </summary>
public class NoOpDbRetryObserver : IDbRetryObserver
{
    public void OnRetry(int attempt, Exception exception, TimeSpan delay)
    {
        // No-op until metrics collection is wired in subsequent commits
    }
}
