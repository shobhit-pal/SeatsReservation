using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Claims;
using System.Text.Json;
using SeatApi.Services.Metrics;

namespace SeatApi.Middleware;

public class RequestLoggingMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ITrafficTracker _trafficTracker;
    private readonly ConcurrentDictionary<string, OutcomeSamplingCounter> _outcomeCounters = new();

    private long _currentSecondEpoch;
    private long _loggedLinesThisSecond;
    private const int MaxLinesPerSecond = 100;

    public RequestLoggingMiddleware(RequestDelegate next, ITrafficTracker trafficTracker)
    {
        _next = next;
        _trafficTracker = trafficTracker;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var sw = Stopwatch.StartNew();
        Exception? caughtException = null;

        try
        {
            await _next(context);
        }
        catch (Exception ex)
        {
            caughtException = ex;
            throw;
        }
        finally
        {
            sw.Stop();
            double durationMs = Math.Round(sw.Elapsed.TotalMilliseconds, 2);

            try
            {
                // Always record to traffic tracker first so traffic_summary and p95 reflect all requests
                _trafficTracker.RecordRequest(durationMs);
                LogCompletion(context, durationMs, caughtException);
            }
            catch
            {
                // Logging must never fail or block a request
            }
        }
    }

    private void LogCompletion(HttpContext context, double durationMs, Exception? caughtException)
    {
        string? outcome = null;
        if (context.Items.TryGetValue("outcome", out var decOutcome) && decOutcome is string explicitOutcome)
        {
            outcome = explicitOutcome;
        }
        else if (caughtException != null || context.Response.StatusCode >= 500)
        {
            outcome = "internal";
        }
        else if (context.Response.StatusCode == StatusCodes.Status503ServiceUnavailable)
        {
            outcome = "unavailable";
        }
        else if (context.Response.StatusCode == StatusCodes.Status404NotFound)
        {
            outcome = "not-found";
        }
        else if (context.Response.StatusCode == StatusCodes.Status401Unauthorized)
        {
            outcome = "unauthorized";
        }
        else if (context.Response.StatusCode == StatusCodes.Status403Forbidden)
        {
            outcome = "forbidden";
        }
        else if (context.Response.StatusCode == StatusCodes.Status400BadRequest ||
                 context.Response.StatusCode == StatusCodes.Status422UnprocessableEntity)
        {
            outcome = "validation";
        }
        else if (context.Response.StatusCode == StatusCodes.Status409Conflict)
        {
            outcome = "seat-taken";
        }

        // If no mapped outcome (e.g. successful GET /shows/{id}, /health/*, /metrics), skip logging
        if (outcome is null)
        {
            return;
        }

        bool isError = context.Response.StatusCode >= 500 || outcome is "internal" or "unavailable";

        bool isSampledType = outcome switch
        {
            "seat-taken" or "per-user-limit" or "replay" or "validation" or "not-found" => true,
            _ => false
        };

        var counter = _outcomeCounters.GetOrAdd(outcome, _ => new OutcomeSamplingCounter());
        long count = counter.Increment();

        bool shouldLog;
        bool? isSampled = null;

        if (isSampledType)
        {
            // Thread-safe first occurrence: exactly when count == 1
            if (count == 1)
            {
                shouldLog = true;
                isSampled = true;
            }
            // Sample 1 in 100
            else if (count % 100 == 0)
            {
                shouldLog = true;
                isSampled = true;
            }
            else
            {
                shouldLog = false;
            }
        }
        else
        {
            // 100% of confirmed, cancelled, cancel-noop, 5xx, 503, unauthorized, forbidden
            shouldLog = true;
            isSampled = null;
        }

        if (!shouldLog)
        {
            return;
        }

        // Errors bypass rate limiter; non-error logs check 100 lines/second rate cap
        if (!isError && !CheckRateLimit())
        {
            return;
        }

        string requestId = context.Items.TryGetValue("request_id", out var reqIdObj) && reqIdObj is string rId && !string.IsNullOrWhiteSpace(rId)
            ? rId
            : context.Response.Headers.TryGetValue("X-Request-Id", out var respId) && !string.IsNullOrWhiteSpace(respId)
                ? respId.ToString()
                : context.TraceIdentifier;

        var endpoint = context.GetEndpoint() as RouteEndpoint;
        string route = endpoint?.RoutePattern?.RawText != null
            ? ("/" + endpoint.RoutePattern.RawText.TrimStart('/'))
            : (context.Request.Path.Value ?? "/");

        string level = isError ? "error" : "info";

        string? userId = context.Items.TryGetValue("user_id", out var uidObj) && uidObj is string uid && !string.IsNullOrWhiteSpace(uid)
            ? uid
            : context.User.FindFirst("user_id")?.Value
              ?? context.User.FindFirst(ClaimTypes.NameIdentifier)?.Value
              ?? context.User.Identity?.Name;

        string? showId = context.Items.TryGetValue("show_id", out var sidObj) && sidObj is string sid && !string.IsNullOrWhiteSpace(sid)
            ? sid
            : (context.Request.RouteValues.TryGetValue("id", out var rVal) && rVal is string rStr && Guid.TryParse(rStr, out _) ? rStr : null);

        int? seatCount = context.Items.TryGetValue("seat_count", out var scObj) && scObj is int sc
            ? sc
            : null;

        string[]? seats = null;
        if (isSampled == true && context.Items.TryGetValue("seat_labels", out var slObj) && slObj is IEnumerable<string> seatLabels)
        {
            seats = seatLabels.Take(5).ToArray();
        }

        string? error = null;
        if (level == "error")
        {
            error = caughtException?.Message
                ?? (context.Items.TryGetValue("exception_message", out var exMsg) ? exMsg?.ToString() : null)
                ?? "Internal server error";
        }

        var entry = new RequestLogEntry(
            Ts: DateTime.UtcNow.ToString("o"),
            Level: level,
            RequestId: requestId,
            Method: context.Request.Method,
            Route: route,
            Status: context.Response.StatusCode,
            Outcome: outcome,
            DurationMs: durationMs,
            UserId: userId,
            ShowId: showId,
            SeatCount: seatCount,
            Seats: seats,
            Sampled: isSampled,
            Error: error
        );

        string json = JsonSerializer.Serialize(entry);
        Console.Out.WriteLine(json);
    }

    private bool CheckRateLimit()
    {
        long nowSecond = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        long currentSecond = Volatile.Read(ref _currentSecondEpoch);
        if (nowSecond != currentSecond)
        {
            Interlocked.Exchange(ref _currentSecondEpoch, nowSecond);
            Interlocked.Exchange(ref _loggedLinesThisSecond, 0);
        }
        return Interlocked.Increment(ref _loggedLinesThisSecond) <= MaxLinesPerSecond;
    }

    private class OutcomeSamplingCounter
    {
        private long _count;
        public long Increment() => Interlocked.Increment(ref _count);
    }
}
