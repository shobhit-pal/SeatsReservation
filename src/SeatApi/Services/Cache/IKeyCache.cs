using System.Diagnostics.CodeAnalysis;
using SeatApi.Models;

namespace SeatApi.Services.Cache;

public interface IKeyCache
{
    bool TryGet(string userId, string key, [NotNullWhen(true)] out CachedKeyEntry? entry);
    void Set(string userId, string key, string requestHash, string responseJson);
}
