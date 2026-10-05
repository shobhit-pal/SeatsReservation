namespace SeatApi.Services.Metrics;

public interface ITrafficTracker
{
    void RecordRequest(double durationMs);
    (long RequestCount, double P95Ms) DrainWindow();
}
