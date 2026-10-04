namespace SeatApi.Models;

/// <summary>
/// Database and gate configuration options.
/// Bound from the "Db" configuration section (env var: Db__GateSize).
/// </summary>
public class DbOptions
{
    public const string SectionName = "Db";

    public int GateSize { get; set; } = 6;

    public int RetryWindowSeconds { get; set; } = 10;
}
