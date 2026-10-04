using SeatApi.Models;

namespace SeatApi.Services.Cache;

public interface IShowCache
{
    Task<CachedShow?> GetAsync(Guid showId, CancellationToken ct = default);
    void Set(Show show, IEnumerable<string> seats);
}
