using System.Collections.Concurrent;
using Prometheus;

namespace SeatApi.Services.Metrics;

public class AppMetrics : IAppMetrics
{
    private readonly ConcurrentDictionary<Guid, byte> _trackedSeatsAvailableShows = new();

    private readonly Counter _confirmedCounter = Prometheus.Metrics.CreateCounter(
        "reservations_confirmed_total",
        "Total number of confirmed reservations.");

    private readonly Counter _declinedCounter = Prometheus.Metrics.CreateCounter(
        "reservations_declined_total",
        "Total number of declined reservations.",
        new CounterConfiguration
        {
            LabelNames = new[] { "reason" }
        });

    private readonly Counter _cancelledCounter = Prometheus.Metrics.CreateCounter(
        "reservations_cancelled_total",
        "Total number of successfully cancelled reservations.");

    private readonly Gauge _seatsAvailableGauge = Prometheus.Metrics.CreateGauge(
        "seats_available",
        "Number of available seats for a show.",
        new GaugeConfiguration
        {
            LabelNames = new[] { "show_id" }
        });

    private readonly Gauge _dbGateWaitingGauge = Prometheus.Metrics.CreateGauge(
        "db_gate_waiting",
        "Number of requests currently waiting at the database admission gate.");

    private readonly Counter _dbRetriesCounter = Prometheus.Metrics.CreateCounter(
        "db_retries_total",
        "Total number of transient database operation retries.");

    private readonly Counter _takenFilterHitsCounter = Prometheus.Metrics.CreateCounter(
        "taken_filter_hits_total",
        "Total number of reservation requests rejected in-memory by the taken filter.");

    private readonly Counter _unhandledExceptionsCounter = Prometheus.Metrics.CreateCounter(
        "unhandled_exceptions_total",
        "Total number of unhandled exceptions caught by middleware.");

    public void RecordConfirmedReservation(Guid showId, int seatCount)
    {
        _confirmedCounter.Inc();
        _trackedSeatsAvailableShows.TryAdd(showId, 0);
        _seatsAvailableGauge.WithLabels(showId.ToString()).Dec(seatCount);
    }

    public void RecordDeclinedReservation(string reason)
    {
        _declinedCounter.WithLabels(reason).Inc();
    }

    public void RecordCancelledReservation(Guid showId, int freedSeatCount)
    {
        _cancelledCounter.Inc();
        _trackedSeatsAvailableShows.TryAdd(showId, 0);
        _seatsAvailableGauge.WithLabels(showId.ToString()).Inc(freedSeatCount);
    }

    public void SetSeatsAvailable(Guid showId, int availableCount)
    {
        _trackedSeatsAvailableShows.TryAdd(showId, 0);
        _seatsAvailableGauge.WithLabels(showId.ToString()).Set(availableCount);
    }

    public void RemoveSeatsAvailable(Guid showId)
    {
        _trackedSeatsAvailableShows.TryRemove(showId, out _);
        _seatsAvailableGauge.RemoveLabelled(showId.ToString());
    }

    public IReadOnlyCollection<Guid> GetTrackedSeatsAvailableShowIds() =>
        _trackedSeatsAvailableShows.Keys.ToList();

    public void IncrementDbGateWaiting()
    {
        _dbGateWaitingGauge.Inc();
    }

    public void DecrementDbGateWaiting()
    {
        _dbGateWaitingGauge.Dec();
    }

    public void RecordDbRetry()
    {
        _dbRetriesCounter.Inc();
    }

    public void RecordTakenFilterHit()
    {
        _takenFilterHitsCounter.Inc();
    }

    public void RecordUnhandledException()
    {
        _unhandledExceptionsCounter.Inc();
    }

    public MetricsSnapshot GetSnapshot()
    {
        return new MetricsSnapshot(
            Confirmed: (long)_confirmedCounter.Value,
            DeclinedSeatTaken: (long)_declinedCounter.WithLabels("seat-taken").Value,
            DeclinedPerUserLimit: (long)_declinedCounter.WithLabels("per-user-limit").Value,
            DeclinedIdempotentReplay: (long)_declinedCounter.WithLabels("idempotent-replay").Value,
            DeclinedKeyReuse: (long)_declinedCounter.WithLabels("idempotency-key-reuse").Value,
            Cancelled: (long)_cancelledCounter.Value,
            UnhandledExceptions: (long)_unhandledExceptionsCounter.Value,
            DbGateWaiting: (long)_dbGateWaitingGauge.Value,
            DbRetries: (long)_dbRetriesCounter.Value
        );
    }
}
