namespace SeatApi.Models;

/// <summary>
/// Inbound body for POST /shows.
/// All fields are nullable so model binding succeeds for valid JSON types,
/// and we return precise validation messages from the service.
/// Wrong types (float/string for price_paise) fail model binding → existing 400 envelope.
/// </summary>
public class CreateShowRequest
{
    public string? Name { get; set; }
    public List<string>? Seats { get; set; }
    public long? PricePaise { get; set; }
    public int? PerUserLimit { get; set; }
}
