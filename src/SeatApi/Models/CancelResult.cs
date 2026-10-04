namespace SeatApi.Models;

/// <summary>
/// Domain outcome for cancelling a reservation.
/// Encapsulates Cancelled or NotFound outcomes without exceptions.
/// </summary>
public class CancelResult
{
    public enum OutcomeType
    {
        Cancelled,
        NotFound
    }

    public OutcomeType Outcome { get; private init; }
    public ReservationResponse? Response { get; private init; }
    public string? ErrorMessage { get; private init; }
    public IReadOnlyList<string>? FreedSeats { get; private init; }

    public static CancelResult Cancelled(ReservationResponse response, IReadOnlyList<string>? freedSeats = null) =>
        new() { Outcome = OutcomeType.Cancelled, Response = response, FreedSeats = freedSeats };

    public static CancelResult NotFound(string message = "Reservation not found") =>
        new() { Outcome = OutcomeType.NotFound, ErrorMessage = message };
}
