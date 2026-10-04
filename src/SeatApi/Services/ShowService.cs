using SeatApi.Models;
using SeatApi.Repositories;
using SeatApi.Services.Cache;
using SeatApi.Services.Metrics;

namespace SeatApi.Services;

public class ShowService : IShowService
{
    private readonly IShowRepository _repo;
    private readonly IShowCache _showCache;
    private readonly IAppMetrics _metrics;

    public ShowService(IShowRepository repo, IShowCache showCache, IAppMetrics metrics)
    {
        _repo = repo;
        _showCache = showCache;
        _metrics = metrics;
    }

    public async Task<ServiceResult<ShowResponse>> CreateShowAsync(
        CreateShowRequest req, CancellationToken ct = default)
    {
        // Validate name: must be non-blank and at most 200 chars
        if (string.IsNullOrWhiteSpace(req.Name) || req.Name.Length > 200)
            return ServiceResult<ShowResponse>.Fail("validation",
                "name must be between 1 and 200 characters");

        // Validate price_paise: must be a positive integer
        if (req.PricePaise is null or <= 0)
            return ServiceResult<ShowResponse>.Fail("validation",
                "price_paise must be a positive integer");

        // Validate per_user_limit: optional, defaults to 4, must be 1..10 if provided
        int perUserLimit = req.PerUserLimit ?? 4;
        if (req.PerUserLimit is not null && (perUserLimit < 1 || perUserLimit > 10))
            return ServiceResult<ShowResponse>.Fail("validation",
                "per_user_limit must be between 1 and 10");

        // Validate seats: must be non-null and non-empty
        if (req.Seats is null || req.Seats.Count == 0)
            return ServiceResult<ShowResponse>.Fail("validation", "seats must not be empty");

        // Validate seats: at most 50,000
        if (req.Seats.Count > 50_000)
            return ServiceResult<ShowResponse>.Fail("validation",
                "seats must not exceed 50,000 entries");

        // Validate seats: no blank labels
        if (req.Seats.Any(string.IsNullOrWhiteSpace))
            return ServiceResult<ShowResponse>.Fail("validation",
                "seat labels must not be blank");

        // Validate seats: no label over 20 chars
        var longLabel = req.Seats.FirstOrDefault(s => s.Length > 20);
        if (longLabel is not null)
            return ServiceResult<ShowResponse>.Fail("validation",
                $"seat label '{longLabel}' exceeds 20 characters");

        // Validate seats: no duplicate labels (exact match)
        if (req.Seats.Count != req.Seats.Distinct(StringComparer.Ordinal).Count())
            return ServiceResult<ShowResponse>.Fail("validation",
                "seat labels must be unique");

        // Build domain entity — ID generated here, not in the DB or controller
        var show = new Show
        {
            Id           = Guid.NewGuid(),
            Name         = req.Name.Trim(),
            PricePaise   = req.PricePaise.Value,
            PerUserLimit = perUserLimit,
            TotalSeats   = req.Seats.Count,
            CreatedAt    = DateTimeOffset.UtcNow
        };

        await _repo.CreateAsync(show, req.Seats, ct);
        _showCache.Set(show, req.Seats);
        _metrics.SetSeatsAvailable(show.Id, show.TotalSeats);

        // Map to response — all seats start as available
        return ServiceResult<ShowResponse>.Ok(new ShowResponse
        {
            Id           = show.Id,
            Name         = show.Name,
            PricePaise   = show.PricePaise,
            PerUserLimit = show.PerUserLimit,
            TotalSeats   = show.TotalSeats,
            Seats        = req.Seats
                .Select(s => new SeatStatusResponse { Seat = s, Status = SeatStatus.Available })
                .ToList()
        });
    }

    public async Task<ServiceResult<ShowStateResponse>> GetShowStateAsync(
        string idRaw, string? summaryRaw, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(idRaw) || !Guid.TryParse(idRaw, out var showId))
        {
            return ServiceResult<ShowStateResponse>.Fail("show-not-found", "Show not found");
        }

        bool summaryOnly = string.Equals(summaryRaw, "true", StringComparison.Ordinal);

        var state = await _repo.GetShowStateAsync(showId, summaryOnly, ct);
        if (state is null)
        {
            return ServiceResult<ShowStateResponse>.Fail("show-not-found", "Show not found");
        }

        return ServiceResult<ShowStateResponse>.Ok(state);
    }
}
