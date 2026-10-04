namespace SeatApi.Models;

/// <summary>
/// Status of a reservation stored as smallint in Postgres.
/// 1 = Confirmed, 2 = Cancelled.
/// </summary>
public enum ReservationStatus : short
{
    Confirmed = 1,
    Cancelled = 2
}
