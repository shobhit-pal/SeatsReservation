using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using SeatApi.Models;

namespace SeatApi.Services.Resilience;

public class TransientRetry : ITransientRetry
{
    private readonly int _retryWindowSeconds;
    private readonly IDbRetryObserver _observer;
    private readonly ILogger<TransientRetry> _logger;

    public TransientRetry(
        IOptions<DbOptions> options,
        IDbRetryObserver observer,
        ILogger<TransientRetry> logger)
    {
        _retryWindowSeconds = options.Value.RetryWindowSeconds > 0 ? options.Value.RetryWindowSeconds : 10;
        _observer = observer;
        _logger = logger;
    }

    public async Task<T> RunAsync<T>(Func<Task<T>> work, CancellationToken ct = default)
    {
        var stopwatch = Stopwatch.StartNew();
        var maxDuration = TimeSpan.FromSeconds(_retryWindowSeconds);
        int attempt = 0;

        while (true)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                return await work();
            }
            catch (Exception ex) when (IsTransient(ex))
            {
                attempt++;
                var elapsed = stopwatch.Elapsed;

                if (elapsed >= maxDuration)
                {
                    _logger.LogError(ex, "Retry window of {Window}s exhausted after {Attempts} attempts.", _retryWindowSeconds, attempt);
                    throw new DbUnavailableException("Database temporarily unavailable", ex);
                }

                var delay = CalculateDelay(attempt, maxDuration - elapsed);
                _observer.OnRetry(attempt, ex, delay);

                _logger.LogWarning(
                    "Transient database error on attempt {Attempt} ({Elapsed}s elapsed). Retrying in {Delay}ms: {Message}",
                    attempt, (int)elapsed.TotalSeconds, (int)delay.TotalMilliseconds, ex.Message);

                await Task.Delay(delay, ct);
            }
        }
    }

    private static bool IsTransient(Exception ex)
    {
        if (ex is TimeoutException)
        {
            return true;
        }

        if (ex is PostgresException pgEx)
        {
            if (pgEx.SqlState == "40P01" || // deadlock_detected
                pgEx.SqlState == "40001" || // serialization_failure
                pgEx.SqlState == "57P01" || // admin_shutdown
                pgEx.SqlState == "53300" || // too_many_connections
                (pgEx.SqlState != null && pgEx.SqlState.StartsWith("08", StringComparison.Ordinal))) // connection exceptions
            {
                return true;
            }
        }

        if (ex is NpgsqlException npgsqlEx)
        {
            if (npgsqlEx.IsTransient)
            {
                return true;
            }

            if (npgsqlEx.InnerException is TimeoutException or
                System.IO.IOException or
                System.Net.Sockets.SocketException)
            {
                return true;
            }
        }

        return false;
    }

    private static TimeSpan CalculateDelay(int attempt, TimeSpan remainingWindow)
    {
        // 50ms, 100ms, 200ms, 400ms, 800ms, then 1s steps with jitter
        int baseMs = attempt switch
        {
            1 => 50,
            2 => 100,
            3 => 200,
            4 => 400,
            5 => 800,
            _ => 1000
        };

        // Add 0-25% jitter
        int jitter = Random.Shared.Next(0, Math.Max(1, baseMs / 4));
        int totalMs = baseMs + jitter;

        var delay = TimeSpan.FromMilliseconds(totalMs);
        return delay < remainingWindow ? delay : remainingWindow;
    }
}
