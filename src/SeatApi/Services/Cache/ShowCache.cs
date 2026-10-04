using System.Collections.Concurrent;
using Dapper;
using Npgsql;
using SeatApi.Models;
using SeatApi.Services.Resilience;

namespace SeatApi.Services.Cache;

public class ShowCache : IShowCache
{
    private readonly NpgsqlDataSource _db;
    private readonly ITransientRetry _transientRetry;
    private readonly ConcurrentDictionary<Guid, CacheItem> _cache = new();

    private sealed class CacheItem
    {
        public CachedShow? Show { get; }
        public DateTime? NegativeExpiresUtc { get; }

        public CacheItem(CachedShow show) => Show = show;
        public CacheItem(DateTime negativeExpiresUtc) => NegativeExpiresUtc = negativeExpiresUtc;
    }

    public ShowCache(NpgsqlDataSource db, ITransientRetry transientRetry)
    {
        _db = db;
        _transientRetry = transientRetry;
    }

    public async Task<CachedShow?> GetAsync(Guid showId, CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;

        if (_cache.TryGetValue(showId, out var cached))
        {
            if (cached.Show != null)
            {
                return cached.Show;
            }

            if (cached.NegativeExpiresUtc.HasValue)
            {
                if (now < cached.NegativeExpiresUtc.Value)
                {
                    return null;
                }

                // Evict expired negative cache tombstone so stale entry does not linger
                _cache.TryRemove(showId, out _);
            }
        }

        return await _transientRetry.RunAsync(async () =>
        {
            await using var conn = await _db.OpenConnectionAsync(ct);

            // Fetches show metadata on cache miss
            var show = await conn.QuerySingleOrDefaultAsync<Show>(new CommandDefinition(
                """
                SELECT id, name, price_paise, per_user_limit, total_seats, created_at
                FROM shows
                WHERE id = @Id;
                """,
                new { Id = showId },
                cancellationToken: ct));

            if (show == null)
            {
                _cache[showId] = new CacheItem(now.AddSeconds(1));
                return null;
            }

            // Fetches complete seat label set for the show to populate immutable cache
            var seatLabels = (await conn.QueryAsync<string>(new CommandDefinition(
                """
                SELECT seat_label
                FROM seats
                WHERE show_id = @Id;
                """,
                new { Id = showId },
                cancellationToken: ct))).ToList();

            var cachedShow = new CachedShow(show, new HashSet<string>(seatLabels, StringComparer.Ordinal));
            _cache[showId] = new CacheItem(cachedShow);
            return cachedShow;
        }, ct);
    }

    public void Set(Show show, IEnumerable<string> seats)
    {
        var set = seats as HashSet<string> ?? new HashSet<string>(seats, StringComparer.Ordinal);
        _cache[show.Id] = new CacheItem(new CachedShow(show, set));
    }
}
