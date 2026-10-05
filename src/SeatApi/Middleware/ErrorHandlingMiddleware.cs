using System.Text.Json;
using Microsoft.Extensions.Logging;
using Npgsql;
using SeatApi.Services.Metrics;
using SeatApi.Services.Resilience;

namespace SeatApi.Middleware;

public class ErrorHandlingMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<ErrorHandlingMiddleware> _logger;
    private readonly IAppMetrics _metrics;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
    };

    public ErrorHandlingMiddleware(RequestDelegate next, ILogger<ErrorHandlingMiddleware> logger, IAppMetrics metrics)
    {
        _next = next;
        _logger = logger;
        _metrics = metrics;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        // Correlation ID: honour X-Request-Id or use TraceIdentifier
        if (!context.Request.Headers.TryGetValue("X-Request-Id", out var correlationId) ||
            string.IsNullOrWhiteSpace(correlationId))
        {
            correlationId = context.TraceIdentifier;
        }

        context.Response.Headers["X-Request-Id"] = correlationId;

        try
        {
            await _next(context);

            if (!context.Response.HasStarted)
            {
                if (context.Response.StatusCode == StatusCodes.Status404NotFound &&
                    string.IsNullOrEmpty(context.Response.ContentType))
                {
                    context.Response.ContentType = "application/json";
                    await context.Response.WriteAsJsonAsync(new
                    {
                        error = "not-found",
                        message = "Endpoint not found",
                        request_id = correlationId.ToString()
                    }, JsonOptions);
                }
                else if (context.Response.StatusCode == StatusCodes.Status405MethodNotAllowed &&
                         string.IsNullOrEmpty(context.Response.ContentType))
                {
                    context.Response.ContentType = "application/json";
                    await context.Response.WriteAsJsonAsync(new
                    {
                        error = "method-not-allowed",
                        message = "Method not allowed",
                        request_id = correlationId.ToString()
                    }, JsonOptions);
                }
            }
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            // Quiet no-op when client disconnected (no error log, no 500)
        }
        catch (Exception ex)
        {
            await HandleExceptionAsync(context, ex, correlationId!);
        }
    }

    private async Task HandleExceptionAsync(HttpContext context, Exception exception, string correlationId)
    {
        if (context.Response.HasStarted)
        {
            _logger.LogWarning("Response has already started, unable to write error response for {CorrelationId}", correlationId);
            return;
        }

        if (exception is OperationCanceledException)
        {
            // Client disconnect or cancellation: quiet no-op
            return;
        }

        if (exception is BadHttpRequestException badHttpEx)
        {
            int statusCode = badHttpEx.StatusCode != 0 ? badHttpEx.StatusCode : StatusCodes.Status400BadRequest;
            string errorSlug = statusCode switch
            {
                StatusCodes.Status408RequestTimeout => "request-timeout",
                StatusCodes.Status413PayloadTooLarge => "payload-too-large",
                StatusCodes.Status400BadRequest => "bad-request",
                _ => "bad-request"
            };

            // Log at Warning WITHOUT a stack trace
            _logger.LogWarning("Bad HTTP request ({StatusCode}) for request {CorrelationId}: {Message}", statusCode, correlationId, badHttpEx.Message);

            context.Response.StatusCode = statusCode;
            context.Response.ContentType = "application/json";

            await context.Response.WriteAsJsonAsync(new
            {
                error = errorSlug,
                message = badHttpEx.Message,
                request_id = correlationId
            }, JsonOptions);
            return;
        }

        if (exception is DbUnavailableException or NpgsqlException or TimeoutException)
        {
            _logger.LogWarning(exception, "Database unavailable for request {CorrelationId}", correlationId);
            context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            context.Response.Headers.RetryAfter = "2";
            context.Response.ContentType = "application/json";

            await context.Response.WriteAsJsonAsync(new
            {
                error = "unavailable",
                message = "database temporarily unavailable",
                request_id = correlationId
            }, JsonOptions);
            return;
        }

        // Only truly unexpected exceptions are logged as Error with stack and increment unhandled metric
        _metrics.RecordUnhandledException();
        _logger.LogError(exception, "Unhandled exception for request {CorrelationId}", correlationId);
        context.Response.StatusCode = StatusCodes.Status500InternalServerError;
        context.Response.ContentType = "application/json";

        await context.Response.WriteAsJsonAsync(new
        {
            error = "internal",
            message = "An internal error occurred",
            request_id = correlationId
        }, JsonOptions);
    }
}
