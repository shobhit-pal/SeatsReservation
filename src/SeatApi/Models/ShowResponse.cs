namespace SeatApi.Models;

/// <summary>201 response body for POST /shows.</summary>
public class ShowResponse
{
    public Guid Id { get; init; }
    public string Name { get; init; } = string.Empty;
    public long PricePaise { get; init; }
    public int PerUserLimit { get; init; }
    public int TotalSeats { get; init; }
    public List<SeatStatusResponse> Seats { get; init; } = new();
}
