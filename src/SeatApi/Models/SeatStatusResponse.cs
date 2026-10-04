namespace SeatApi.Models;

/// <summary>One entry in the seats array of the show response.</summary>
public class SeatStatusResponse
{
    public string Seat { get; init; } = string.Empty;
    public string Status { get; init; } = string.Empty;
}
