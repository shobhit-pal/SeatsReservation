namespace SeatApi.Models;

/// <summary>
/// 201 response body for POST /shows/{id}/reserve.
/// </summary>
public class ReservationResponse
{
    public Guid ReservationId { get; init; }
    public Guid ShowId { get; init; }
    public string UserId { get; init; } = string.Empty;
    public IReadOnlyList<string> Seats { get; init; } = Array.Empty<string>();
    public long AmountPaise { get; init; }
    public string Status { get; init; } = "confirmed";
}
