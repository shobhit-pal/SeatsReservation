namespace SeatApi.Models;

public static class ApiError
{
    public static object Response(string error, string message, IReadOnlyDictionary<string, object>? extra = null)
    {
        if (extra is null || extra.Count == 0)
        {
            return new { error, message };
        }

        var dict = new Dictionary<string, object>(extra, StringComparer.Ordinal)
        {
            ["error"] = error,
            ["message"] = message
        };
        return dict;
    }

    public static object Validation(string message) =>
        Response("validation", message);

    public static object NotFound(string message) =>
        Response("not-found", message);

    public static object Unauthorized() =>
        Response("unauthorized", "Missing or invalid token");

    public static object Forbidden() =>
        Response("forbidden", "Insufficient permissions");
}
