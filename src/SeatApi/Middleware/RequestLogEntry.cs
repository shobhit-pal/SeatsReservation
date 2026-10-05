using System.Text.Json.Serialization;

namespace SeatApi.Middleware;

public record RequestLogEntry(
    [property: JsonPropertyName("ts")] string Ts,
    [property: JsonPropertyName("level")] string Level,
    [property: JsonPropertyName("request_id")] string RequestId,
    [property: JsonPropertyName("method")] string Method,
    [property: JsonPropertyName("route")] string Route,
    [property: JsonPropertyName("status")] int Status,
    [property: JsonPropertyName("outcome")] string Outcome,
    [property: JsonPropertyName("duration_ms")] double DurationMs,
    [property: JsonPropertyName("user_id"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? UserId = null,
    [property: JsonPropertyName("show_id"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ShowId = null,
    [property: JsonPropertyName("seat_count"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? SeatCount = null,
    [property: JsonPropertyName("seats"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string[]? Seats = null,
    [property: JsonPropertyName("sampled"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? Sampled = null,
    [property: JsonPropertyName("error"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Error = null
);
