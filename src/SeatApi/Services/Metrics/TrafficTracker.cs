namespace SeatApi.Services.Metrics;

public class TrafficTracker : ITrafficTracker
{
    private long _requestCount;
    private const int BufferSize = 2048;
    private readonly double[] _buffer = new double[BufferSize];
    private long _writeIndex;

    public void RecordRequest(double durationMs)
    {
        Interlocked.Increment(ref _requestCount);
        long idx = Interlocked.Increment(ref _writeIndex) - 1;
        _buffer[idx % BufferSize] = durationMs;
    }

    public (long RequestCount, double P95Ms) DrainWindow()
    {
        long count = Interlocked.Exchange(ref _requestCount, 0);
        long totalWrites = Interlocked.Exchange(ref _writeIndex, 0);

        if (count == 0 || totalWrites == 0)
        {
            return (count, 0.0);
        }

        int sampleCount = (int)Math.Min(totalWrites, BufferSize);
        var samples = new double[sampleCount];
        Array.Copy(_buffer, samples, sampleCount);
        Array.Sort(samples);

        int p95Index = (int)Math.Ceiling(samples.Length * 0.95) - 1;
        if (p95Index < 0) p95Index = 0;
        if (p95Index >= samples.Length) p95Index = samples.Length - 1;

        return (count, Math.Round(samples[p95Index], 2));
    }
}
