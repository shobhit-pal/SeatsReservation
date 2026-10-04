using Microsoft.Extensions.Options;
using SeatApi.Models;

namespace SeatApi.Services.Cache;

public class DbGate : IDbGate
{
    private readonly SemaphoreSlim _semaphore;

    public DbGate(IOptions<DbOptions> options)
    {
        var gateSize = options.Value.GateSize;
        _semaphore = new SemaphoreSlim(gateSize, gateSize);
    }

    public async Task<IAsyncDisposable> WaitAsync(CancellationToken ct = default)
    {
        await _semaphore.WaitAsync(ct);
        return new GateReleaser(_semaphore);
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
