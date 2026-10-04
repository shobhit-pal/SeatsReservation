using System.Text.Json;
using Microsoft.Extensions.Logging;
using Npgsql;
using SeatApi.Services.Resilience;

namespace SeatApi.Middleware;

public class ErrorHandlingMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<ErrorHandlingMiddleware> _logger;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
    };

    public ErrorHandlingMiddleware(RequestDelegate next, ILogger<ErrorHandlingMiddleware> logger)
    {
        _next = next;
        _logger = logger;
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
                        message = "Endpoint not found"
                    }, JsonOptions);
                }
                else if (context.Response.StatusCode == StatusCodes.Status405MethodNotAllowed &&
                         string.IsNullOrEmpty(context.Response.ContentType))
                {
                    context.Response.ContentType = "application/json";
                    await context.Response.WriteAsJsonAsync(new
                    {
                        error = "method-not-allowed",
                        message = "Method not allowed"
                    }, JsonOptions);
                }
            }
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

        if (exception is DbUnavailableException or NpgsqlException or TimeoutException)
        {
            _logger.LogWarning(exception, "Database unavailable for request {CorrelationId}", correlationId);
            context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            context.Response.Headers.RetryAfter = "2";
            context.Response.ContentType = "application/json";

            await context.Response.WriteAsJsonAsync(new
            {
                error = "unavailable",
                message = "database temporarily unavailable"
            }, JsonOptions);
            return;
        }

        _logger.LogError(exception, "Unhandled exception for request {CorrelationId}", correlationId);
        context.Response.StatusCode = StatusCodes.Status500InternalServerError;
        context.Response.ContentType = "application/json";

        await context.Response.WriteAsJsonAsync(new
        {
            error = "internal",
            message = "An internal error occurred"
        }, JsonOptions);
    }
}
