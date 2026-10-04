namespace SeatApi.Models;

/// <summary>Maps to the shows table row. Used by repository and service.</summary>
public class Show
{
    public Guid Id { get; init; }
    public string Name { get; init; } = string.Empty;
    public long PricePaise { get; init; }
    public int PerUserLimit { get; init; }
    public int TotalSeats { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
}
