namespace SeatApi.Models;

/// <summary>
/// Inbound body for POST /shows/{id}/reserve.
/// </summary>
public class ReserveRequest
{
    public List<string>? Seats { get; set; }
    public string? IdempotencyKey { get; set; }
}
