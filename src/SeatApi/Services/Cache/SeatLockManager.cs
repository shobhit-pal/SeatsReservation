namespace SeatApi.Services.Cache;

public class SeatLockManager : ISeatLockManager
{
    private readonly object _sync = new();
    private readonly Dictionary<(Guid ShowId, string Seat), LockEntry> _locks = new();

    public int ActiveLockCount
    {
        get
        {
            lock (_sync)
            {
                return _locks.Count;
            }
        }
    }

    private sealed class LockEntry
    {
        public (Guid ShowId, string Seat) Key { get; }
        public SemaphoreSlim Semaphore { get; } = new(1, 1);
        public int Waiters { get; set; }

        public LockEntry((Guid ShowId, string Seat) key) => Key = key;
    }

    public async Task<IAsyncDisposable> AcquireAsync(
        Guid showId,
        IReadOnlyList<string> seatsSortedOrdinal,
        CancellationToken ct = default)
    {
        if (seatsSortedOrdinal.Count == 0)
        {
            return EmptyDisposable.Instance;
        }

        var acquiredEntries = new List<LockEntry>(seatsSortedOrdinal.Count);
        LockEntry? currentEntry = null;

        try
        {
            foreach (var seat in seatsSortedOrdinal)
            {
                var key = (showId, seat);
                lock (_sync)
                {
                    if (!_locks.TryGetValue(key, out currentEntry))
                    {
                        currentEntry = new LockEntry(key);
                        _locks[key] = currentEntry;
                    }

                    currentEntry.Waiters++;
                }

                await currentEntry.Semaphore.WaitAsync(ct);
                acquiredEntries.Add(currentEntry);
                currentEntry = null;
            }

            return new Releaser(this, acquiredEntries);
        }
        catch
        {
            if (currentEntry != null)
            {
                ReleaseEntry(currentEntry, acquired: false);
            }

            for (int i = acquiredEntries.Count - 1; i >= 0; i--)
            {
                ReleaseEntry(acquiredEntries[i], acquired: true);
            }

            throw;
        }
    }

    private void ReleaseEntry(LockEntry entry, bool acquired)
    {
        if (acquired)
        {
            entry.Semaphore.Release();
        }

        lock (_sync)
        {
            entry.Waiters--;
            if (entry.Waiters == 0)
            {
                _locks.Remove(entry.Key);
            }
        }
    }

    private sealed class Releaser : IAsyncDisposable
    {
        private readonly SeatLockManager _manager;
        private readonly List<LockEntry> _entries;
        private int _disposed;

        public Releaser(SeatLockManager manager, List<LockEntry> entries)
        {
            _manager = manager;
            _entries = entries;
        }

        public ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                for (int i = _entries.Count - 1; i >= 0; i--)
                {
                    _manager.ReleaseEntry(_entries[i], acquired: true);
                }
            }

            return ValueTask.CompletedTask;
        }
    }

    private sealed class EmptyDisposable : IAsyncDisposable
    {
        public static readonly EmptyDisposable Instance = new();
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
