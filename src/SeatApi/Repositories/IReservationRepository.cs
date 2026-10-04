using SeatApi.Models;

namespace SeatApi.Repositories;

public interface IReservationRepository
{
    Task<Show?> GetShowAsync(Guid showId, CancellationToken ct = default);

    Task<IReadOnlyList<string>> GetExistingSeatLabelsAsync(Guid showId, IReadOnlyList<string> seatLabels, CancellationToken ct = default);

    Task<ReservationMetadata?> GetReservationForCancelAsync(Guid reservationId, CancellationToken ct = default);

    Task<ReserveResult> ExecuteReservationAsync(
        Guid reservationId,
        Show show,
        string userId,
        string idempotencyKey,
        string requestHash,
        IReadOnlyList<string> sortedSeats,
        CancellationToken ct = default);

    Task<CancelResult> CancelReservationAsync(
        Guid reservationId,
        string userId,
        CancellationToken ct = default);
}
