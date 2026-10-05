namespace SeatApi.Services.Metrics;

public record MetricsSnapshot(
    long Confirmed,
    long DeclinedSeatTaken,
    long DeclinedPerUserLimit,
    long DeclinedIdempotentReplay,
    long DeclinedKeyReuse,
    long Cancelled,
    long UnhandledExceptions,
    long DbGateWaiting,
    long DbRetries
);
