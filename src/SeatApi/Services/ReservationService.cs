using System.Security.Cryptography;
using System.Text;
using SeatApi.Models;
using SeatApi.Repositories;

namespace SeatApi.Services;

public class ReservationService : IReservationService
{
    private readonly IReservationRepository _repo;

    public ReservationService(IReservationRepository repo) => _repo = repo;

    public async Task<ReserveResult> ReserveAsync(
        string? showIdRaw,
        ReserveRequest? request,
        string? headerIdempotencyKey,
        string userId,
        CancellationToken ct = default)
    {
        // 2. show id not a Guid, or show not in DB -> 404 show-not-found
        if (string.IsNullOrWhiteSpace(showIdRaw) || !Guid.TryParse(showIdRaw, out var showId))
        {
            return ReserveResult.Decline(404, "show-not-found", "Show not found");
        }

        var show = await _repo.GetShowAsync(showId, ct);
        if (show is null)
        {
            return ReserveResult.Decline(404, "show-not-found", "Show not found");
        }

        // 3. Idempotency key from header or body
        var headerKey = !string.IsNullOrWhiteSpace(headerIdempotencyKey) ? headerIdempotencyKey.Trim() : null;
        var bodyKey = !string.IsNullOrWhiteSpace(request?.IdempotencyKey) ? request.IdempotencyKey.Trim() : null;

        if (headerKey is not null && bodyKey is not null && !string.Equals(headerKey, bodyKey, StringComparison.Ordinal))
        {
            return ReserveResult.Decline(400, "validation", "Header and body idempotency keys do not match");
        }

        var key = headerKey ?? bodyKey;
        if (string.IsNullOrWhiteSpace(key))
        {
            return ReserveResult.Decline(400, "validation", "Idempotency key is required");
        }

        if (key.Length > 128)
        {
            return ReserveResult.Decline(400, "validation", "Idempotency key must not exceed 128 characters");
        }

        // 4. (1) seats null or empty
        if (request?.Seats is null || request.Seats.Count == 0)
        {
            return ReserveResult.Decline(400, "validation", "seats must not be empty");
        }

        // The count check against per_user_limit runs first because it is O(1) and needs no DB call,
        // then blank labels and duplicates, then the seat-existence query.
        // Oversized payloads are bounded by Kestrel's 4 MB limit (413).
        if (request.Seats.Count > show.PerUserLimit)
        {
            return ReserveResult.Decline(409, "per-user-limit", "Active seat limit exceeded for this show");
        }

        // (3) Blank labels check (array is now at most per_user_limit long)
        if (request.Seats.Any(string.IsNullOrWhiteSpace))
        {
            return ReserveResult.Decline(400, "validation", "seat labels must not be blank");
        }

        // (4) Duplicate labels check
        if (request.Seats.Count != request.Seats.Distinct(StringComparer.Ordinal).Count())
        {
            return ReserveResult.Decline(400, "validation", "seat labels must be unique");
        }

        // 5. Seat existence in the show (DB call only sees at most per_user_limit seats)
        var foundSeats = await _repo.GetExistingSeatLabelsAsync(show.Id, request.Seats, ct);
        var foundSet = new HashSet<string>(foundSeats, StringComparer.Ordinal);
        var unknownSeats = request.Seats.Where(s => !foundSet.Contains(s)).Distinct(StringComparer.Ordinal).ToList();

        if (unknownSeats.Count > 0)
        {
            var extra = new Dictionary<string, object>
            {
                ["unknown"] = unknownSeats
            };
            return ReserveResult.Decline(404, "seat-not-found", "One or more requested seats do not exist in this show", extra);
        }

        // Prepare transaction: sort seats with StringComparer.Ordinal
        var sortedSeats = request.Seats.OrderBy(s => s, StringComparer.Ordinal).ToList();

        // Calculate SHA-256 request hash: show_id + "|" + sorted seats joined by ","
        var hashInput = $"{show.Id}|{string.Join(",", sortedSeats)}";
        var hashBytes = SHA256.HashData(Encoding.UTF8.GetBytes(hashInput));
        var requestHash = Convert.ToHexString(hashBytes).ToLowerInvariant();

        // Generate reservationId before transaction BEGIN
        var reservationId = Guid.NewGuid();

        return await _repo.ExecuteReservationAsync(
            reservationId, show, userId, key, requestHash, sortedSeats, ct);
    }
}
