namespace SeatApi.Models;

public class SeatCountsResponse
{
    public int Available { get; init; }
    public int Held { get; init; }
    public int Confirmed { get; init; }
}
