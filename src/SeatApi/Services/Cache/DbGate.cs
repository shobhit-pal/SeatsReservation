using Microsoft.Extensions.Options;
using SeatApi.Models;
using SeatApi.Services.Metrics;

namespace SeatApi.Services.Cache;

public class DbGate : IDbGate
{
    private readonly SemaphoreSlim _semaphore;
    private readonly IAppMetrics _metrics;

    public DbGate(IOptions<DbOptions> options, IAppMetrics metrics)
    {
        var gateSize = options.Value.GateSize;
        _semaphore = new SemaphoreSlim(gateSize, gateSize);
        _metrics = metrics;
    }

    public async Task<IAsyncDisposable> WaitAsync(CancellationToken ct = default)
    {
        _metrics.IncrementDbGateWaiting();
        try
        {
            await _semaphore.WaitAsync(ct);
            return new GateReleaser(_semaphore);
        }
        finally
        {
            _metrics.DecrementDbGateWaiting();
        }
    }

    private sealed class GateReleaser : IAsyncDisposable
    {
        private readonly SemaphoreSlim _semaphore;
        private int _disposed;

        public GateReleaser(SemaphoreSlim semaphore) => _semaphore = semaphore;

        public ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                _semaphore.Release();
            }

            return ValueTask.CompletedTask;
        }
    }
}
