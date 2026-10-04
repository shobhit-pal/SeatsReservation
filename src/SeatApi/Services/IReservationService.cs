using SeatApi.Models;

namespace SeatApi.Services;

public interface IReservationService
{
    Task<ReserveResult> ReserveAsync(
        string? showIdRaw,
        ReserveRequest? request,
        string? headerIdempotencyKey,
        string userId,
        CancellationToken ct = default);
}
