using System.Text.Json;
using Microsoft.Extensions.Hosting;

namespace SeatApi.Services.Metrics;

public class TrafficSummaryBackgroundService : BackgroundService
{
    private readonly IAppMetrics _appMetrics;
    private readonly ITrafficTracker _trafficTracker;
    private MetricsSnapshot _prevSnapshot;

    public TrafficSummaryBackgroundService(IAppMetrics appMetrics, ITrafficTracker trafficTracker)
    {
        _appMetrics = appMetrics;
        _trafficTracker = trafficTracker;
        _prevSnapshot = _appMetrics.GetSnapshot();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));

        while (!stoppingToken.IsCancellationRequested && await timer.WaitForNextTickAsync(stoppingToken))
        {
            var (requests, p95Ms) = _trafficTracker.DrainWindow();
            var currentSnapshot = _appMetrics.GetSnapshot();

            if (requests > 0)
            {
                long confirmedDelta = Math.Max(0, currentSnapshot.Confirmed - _prevSnapshot.Confirmed);
                long seatTakenDelta = Math.Max(0, currentSnapshot.DeclinedSeatTaken - _prevSnapshot.DeclinedSeatTaken);
                long perUserLimitDelta = Math.Max(0, currentSnapshot.DeclinedPerUserLimit - _prevSnapshot.DeclinedPerUserLimit);
                long replayDelta = Math.Max(0, currentSnapshot.DeclinedIdempotentReplay - _prevSnapshot.DeclinedIdempotentReplay);
                long keyReuseDelta = Math.Max(0, currentSnapshot.DeclinedKeyReuse - _prevSnapshot.DeclinedKeyReuse);
                long cancelledDelta = Math.Max(0, currentSnapshot.Cancelled - _prevSnapshot.Cancelled);
                long errors5xxDelta = Math.Max(0, currentSnapshot.UnhandledExceptions - _prevSnapshot.UnhandledExceptions);
                long dbRetriesDelta = Math.Max(0, currentSnapshot.DbRetries - _prevSnapshot.DbRetries);

                var summary = new
                {
                    @event = "traffic_summary",
                    window_s = 5,
                    requests = requests,
                    confirmed = confirmedDelta,
                    declined = new Dictionary<string, long>
                    {
                        ["seat-taken"] = seatTakenDelta,
                        ["per-user-limit"] = perUserLimitDelta,
                        ["idempotent-replay"] = replayDelta,
                        ["idempotency-key-reuse"] = keyReuseDelta
                    },
                    cancelled = cancelledDelta,
                    errors_5xx = errors5xxDelta,
                    gate_waiting = currentSnapshot.DbGateWaiting,
                    db_retries = dbRetriesDelta,
                    p95_ms = p95Ms
                };

                string json = JsonSerializer.Serialize(summary);
                Console.Out.WriteLine(json);
            }

            _prevSnapshot = currentSnapshot;
        }
    }
}
