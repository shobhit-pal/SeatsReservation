namespace SeatApi.Models;

/// <summary>
/// Cached immutable show entity along with its complete set of seat labels.
/// </summary>
public record CachedShow(Show Show, HashSet<string> Seats);
