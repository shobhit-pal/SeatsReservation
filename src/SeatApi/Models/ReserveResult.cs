namespace SeatApi.Models;

/// <summary>
/// Domain result type for reservation attempts.
/// Encapsulates Created, Replay, or Decline outcomes without throwing exceptions.
/// </summary>
public class ReserveResult
{
    public enum OutcomeType
    {
        Created,
        Replay,
        Decline
    }

    public OutcomeType Outcome { get; private init; }
    public ReservationResponse? Response { get; private init; }
    public string? StoredJson { get; private init; }
    public int StatusCode { get; private init; }
    public string? ErrorCode { get; private init; }
    public string? ErrorMessage { get; private init; }
    public IReadOnlyDictionary<string, object>? Extra { get; private init; }
    public IReadOnlyList<(string SeatLabel, string OwnerUserId)>? UnavailableOwners { get; private init; }

    public static ReserveResult Created(ReservationResponse response, string? storedJson = null) =>
        new()
        {
            Outcome = OutcomeType.Created,
            Response = response,
            StoredJson = storedJson,
            StatusCode = 201
        };

    public static ReserveResult Replay(string storedJson) =>
        new()
        {
            Outcome = OutcomeType.Replay,
            StoredJson = storedJson,
            StatusCode = 201
        };

    public static ReserveResult Decline(
        int status,
        string error,
        string message,
        IReadOnlyDictionary<string, object>? extra = null,
        IReadOnlyList<(string SeatLabel, string OwnerUserId)>? unavailableOwners = null) =>
        new()
        {
            Outcome = OutcomeType.Decline,
            StatusCode = status,
            ErrorCode = error,
            ErrorMessage = message,
            Extra = extra,
            UnavailableOwners = unavailableOwners
        };
}
