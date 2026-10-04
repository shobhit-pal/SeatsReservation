using System.Diagnostics.CodeAnalysis;

namespace SeatApi.Services.Cache;

public interface ITakenFilter
{
    bool TryGetOwner(Guid showId, string seat, [NotNullWhen(true)] out string? owner);
    void MarkTaken(Guid showId, string seat, string owner);
    void Evict(Guid showId, string seat);
}
