namespace SeatApi.Middleware;

public class CorrelationIdMiddleware
{
    private readonly RequestDelegate _next;

    public CorrelationIdMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        if (!context.Request.Headers.TryGetValue("X-Request-Id", out var correlationId) ||
            string.IsNullOrWhiteSpace(correlationId))
        {
            correlationId = context.TraceIdentifier;
        }

        context.Items["request_id"] = correlationId.ToString();
        context.Response.Headers["X-Request-Id"] = correlationId;

        await _next(context);
    }
}
