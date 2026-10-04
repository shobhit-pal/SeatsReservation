using SeatApi.Services.Resilience;

namespace SeatApi.Services.Metrics;

public class PrometheusDbRetryObserver : IDbRetryObserver
{
    private readonly IAppMetrics _metrics;

    public PrometheusDbRetryObserver(IAppMetrics metrics)
    {
        _metrics = metrics;
    }

    public void OnRetry(int attempt, Exception exception, TimeSpan delay)
    {
        _metrics.RecordDbRetry();
    }
}
