namespace SeatApi.Services.Cache;

public interface ISeatLockManager
{
    Task<IAsyncDisposable> AcquireAsync(
        Guid showId,
        IReadOnlyList<string> seatsSortedOrdinal,
        CancellationToken ct = default);

    int ActiveLockCount { get; }
}
