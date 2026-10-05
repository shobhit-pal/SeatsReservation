namespace SeatApi.Services.Metrics;

public interface IAppMetrics
{
    void RecordConfirmedReservation(Guid showId, int seatCount);
    void RecordDeclinedReservation(string reason);
    void RecordCancelledReservation(Guid showId, int freedSeatCount);
    void SetSeatsAvailable(Guid showId, int availableCount);
    void RemoveSeatsAvailable(Guid showId);
    IReadOnlyCollection<Guid> GetTrackedSeatsAvailableShowIds();
    void IncrementDbGateWaiting();
    void DecrementDbGateWaiting();
    void RecordDbRetry();
    void RecordTakenFilterHit();
    void RecordUnhandledException();
    MetricsSnapshot GetSnapshot();
}
