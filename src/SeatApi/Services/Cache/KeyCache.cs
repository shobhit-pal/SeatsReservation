using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using SeatApi.Models;

namespace SeatApi.Services.Cache;

public class KeyCache : IKeyCache
{
    private const int MaxEntries = 200_000;
    private readonly ConcurrentDictionary<(string UserId, string Key), CachedKeyEntry> _cache = new();

    public bool TryGet(string userId, string key, [NotNullWhen(true)] out CachedKeyEntry? entry)
    {
        return _cache.TryGetValue((userId, key), out entry);
    }

    public void Set(string userId, string key, string requestHash, string responseJson)
    {
        if (_cache.ContainsKey((userId, key)))
        {
            _cache[(userId, key)] = new CachedKeyEntry(requestHash, responseJson);
            return;
        }

        // Bounded capacity: when reaching 200,000 entries, stop adding rather than evicting randomly
        if (_cache.Count >= MaxEntries)
        {
            return;
        }

        _cache.TryAdd((userId, key), new CachedKeyEntry(requestHash, responseJson));
    }
}
