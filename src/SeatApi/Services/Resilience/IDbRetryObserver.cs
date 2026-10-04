namespace SeatApi.Services.Resilience;

/// <summary>
/// Observer hook for retry attempts, intended for metrics collection in later commits.
/// </summary>
public interface IDbRetryObserver
{
    void OnRetry(int attempt, Exception exception, TimeSpan delay);
}
