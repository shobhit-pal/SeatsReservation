namespace SeatApi.Services.Resilience;

public interface ITransientRetry
{
    Task<T> RunAsync<T>(Func<Task<T>> work, CancellationToken ct = default);
}
