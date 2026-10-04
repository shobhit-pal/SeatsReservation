namespace SeatApi.Services.Cache;

public interface IDbGate
{
    Task<IAsyncDisposable> WaitAsync(CancellationToken ct = default);
}
