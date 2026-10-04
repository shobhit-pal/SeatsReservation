namespace SeatApi.Models;

/// <summary>
/// Lightweight reservation metadata used to identify seats and owner before acquiring locks.
/// </summary>
public record ReservationMetadata(Guid ShowId, IReadOnlyList<string> Seats, string UserId, short Status);
