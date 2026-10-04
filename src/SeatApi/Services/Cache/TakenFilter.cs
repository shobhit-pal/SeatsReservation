using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;

namespace SeatApi.Services.Cache;

// Note: TakenFilter is in-memory for a single application instance.
// In a multi-instance deployment, correctness requires a short entry TTL (~2s)
// or a distributed pub/sub invalidation mechanism; PostgreSQL remains the final authority.
public class TakenFilter : ITakenFilter
{
    private readonly ConcurrentDictionary<(Guid ShowId, string Seat), string> _taken = new();

    public bool TryGetOwner(Guid showId, string seat, [NotNullWhen(true)] out string? owner)
    {
        return _taken.TryGetValue((showId, seat), out owner);
    }

    public void MarkTaken(Guid showId, string seat, string owner)
    {
        _taken[(showId, seat)] = owner;
    }

    public void Evict(Guid showId, string seat)
    {
        _taken.TryRemove((showId, seat), out _);
    }
}
