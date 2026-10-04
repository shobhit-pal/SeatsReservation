namespace SeatApi.Models;

public static class ApiError
{
    public static object Validation(string message) =>
        new { error = "validation", message };

    public static object NotFound(string message) =>
        new { error = "not-found", message };

    public static object Unauthorized() =>
        new { error = "unauthorized", message = "Missing or invalid token" };

    public static object Forbidden() =>
        new { error = "forbidden", message = "Insufficient permissions" };
}
