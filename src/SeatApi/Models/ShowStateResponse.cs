using System.Text.Json.Serialization;

namespace SeatApi.Models;

public class ShowStateResponse
{
    public Guid Id { get; init; }
    public string Name { get; init; } = string.Empty;
    public long PricePaise { get; init; }
    public int PerUserLimit { get; init; }
    public int TotalSeats { get; init; }
    public SeatCountsResponse Counts { get; init; } = null!;

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<SeatStatusResponse>? Seats { get; init; }
}
