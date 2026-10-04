namespace SeatApi.Models;

/// <summary>
/// Domain result type — business failures are values, not exceptions.
/// </summary>
public class ServiceResult<T>
{
    public bool IsSuccess { get; private init; }
    public T? Value { get; private init; }
    public string? ErrorCode { get; private init; }
    public string? ErrorMessage { get; private init; }

    public static ServiceResult<T> Ok(T value) =>
        new() { IsSuccess = true, Value = value };

    public static ServiceResult<T> Fail(string code, string message) =>
        new() { IsSuccess = false, ErrorCode = code, ErrorMessage = message };
}
