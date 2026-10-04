using System.Security.Cryptography;
using System.Text;
using SeatApi.Models;
using SeatApi.Repositories;
using SeatApi.Services.Cache;

namespace SeatApi.Services;

public class ReservationService : IReservationService
{
    private readonly IReservationRepository _repo;
    private readonly IShowCache _showCache;
    private readonly ITakenFilter _takenFilter;
    private readonly IKeyCache _keyCache;
    private readonly ISeatLockManager _seatLockManager;
    private readonly IDbGate _dbGate;

    public ReservationService(
        IReservationRepository repo,
        IShowCache showCache,
        ITakenFilter takenFilter,
        IKeyCache keyCache,
        ISeatLockManager seatLockManager,
        IDbGate dbGate)
    {
        _repo = repo;
        _showCache = showCache;
        _takenFilter = takenFilter;
        _keyCache = keyCache;
        _seatLockManager = seatLockManager;
        _dbGate = dbGate;
    }

    public async Task<ReserveResult> ReserveAsync(
        string? showIdRaw,
        ReserveRequest? request,
        string? headerIdempotencyKey,
        string userId,
        CancellationToken ct = default)
    {
        // 1. show id not a Guid, or show not in cache/DB -> 404 show-not-found
        if (string.IsNullOrWhiteSpace(showIdRaw) || !Guid.TryParse(showIdRaw, out var showId))
        {
            return ReserveResult.Decline(404, "show-not-found", "Show not found");
        }

        var cachedShow = await _showCache.GetAsync(showId, ct);
        if (cachedShow is null)
        {
            return ReserveResult.Decline(404, "show-not-found", "Show not found");
        }

        var show = cachedShow.Show;

        // 2. Idempotency key from header or body
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

        // 3. (1) seats null or empty -> 400
        if (request?.Seats is null || request.Seats.Count == 0)
        {
            return ReserveResult.Decline(400, "validation", "seats must not be empty");
        }

        // The count check against per_user_limit runs first because it is O(1) and needs no DB call,
        // then blank labels and duplicates, then the seat existence check against cached seat set.
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

        // (5) Seat existence in the cached seat set (in-memory lookup, no DB call)
        var unknownSeats = request.Seats
            .Where(s => !cachedShow.Seats.Contains(s))
            .Distinct(StringComparer.Ordinal)
            .ToList();

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

        // 6.b. KeyCache lookup (userId, key): same hash -> return the stored 201 (Replay).
        // Different hash -> 409 idempotency-key-reuse.
        // This runs BEFORE the taken filter so an owner's retry is never denied.
        if (_keyCache.TryGet(userId, key, out var cachedKey))
        {
            if (cachedKey.RequestHash == requestHash)
            {
                return ReserveResult.Replay(cachedKey.ResponseJson);
            }

            return ReserveResult.Decline(409, "idempotency-key-reuse", "This key was already used with different parameters");
        }

        // 6.c. TakenFilter: if any requested seat is taken by ANOTHER user -> 409 seat-taken immediately, no lock, no DB.
        // If taken by the SAME user (and key not in cache) fall through to the DB path.
        var unavailableSeatsFromFilter = sortedSeats
            .Where(s => _takenFilter.TryGetOwner(show.Id, s, out var owner) && owner != userId)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        if (unavailableSeatsFromFilter.Count > 0)
        {
            return ReserveResult.Decline(409, "seat-taken", "One or more requested seats are already reserved",
                new Dictionary<string, object> { ["unavailable"] = unavailableSeatsFromFilter });
        }

        // 6.d. SeatLockManager.AcquireAsync(sorted seats).
        await using var seatLocks = await _seatLockManager.AcquireAsync(show.Id, sortedSeats, ct);

        // While holding the locks: re-check KeyCache and TakenFilter
        if (_keyCache.TryGet(userId, key, out cachedKey))
        {
            if (cachedKey.RequestHash == requestHash)
            {
                return ReserveResult.Replay(cachedKey.ResponseJson);
            }

            return ReserveResult.Decline(409, "idempotency-key-reuse", "This key was already used with different parameters");
        }

        unavailableSeatsFromFilter = sortedSeats
            .Where(s => _takenFilter.TryGetOwner(show.Id, s, out var owner) && owner != userId)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        if (unavailableSeatsFromFilter.Count > 0)
        {
            return ReserveResult.Decline(409, "seat-taken", "One or more requested seats are already reserved",
                new Dictionary<string, object> { ["unavailable"] = unavailableSeatsFromFilter });
        }

        // 6.e. DbGate.WaitAsync. Run the existing DB transaction.
        var reservationId = Guid.NewGuid();
        ReserveResult result;
        await using (await _dbGate.WaitAsync(ct))
        {
            result = await _repo.ExecuteReservationAsync(
                reservationId, show, userId, key, requestHash, sortedSeats, ct);
        }

        // 6.f. Still holding the seat locks:
        switch (result.Outcome)
        {
            case ReserveResult.OutcomeType.Created:
                foreach (var s in sortedSeats)
                {
                    _takenFilter.MarkTaken(show.Id, s, userId);
                }
                if (result.StoredJson != null)
                {
                    _keyCache.Set(userId, key, requestHash, result.StoredJson);
                }
                break;

            case ReserveResult.OutcomeType.Replay:
                if (result.StoredJson != null)
                {
                    _keyCache.Set(userId, key, requestHash, result.StoredJson);
                }
                break;

            case ReserveResult.OutcomeType.Decline:
                if (result.ErrorCode == "seat-taken" && result.UnavailableOwners != null)
                {
                    foreach (var (seatLabel, ownerUserId) in result.UnavailableOwners)
                    {
                        _takenFilter.MarkTaken(show.Id, seatLabel, ownerUserId);
                    }
                }
                break;
        }

        // 6.g. Release gate (done via using), then release locks (done via using)
        return result;
    }

    public async Task<CancelResult> CancelAsync(
        Guid reservationId,
        string userId,
        CancellationToken ct = default)
    {
        // 7.1. Need the reservation's seats to lock them: first SELECT show_id, seats, user_id FROM reservations WHERE id=@r
        // (read only; 404 if not found or not the owner, no lock yet)
        var metadata = await _repo.GetReservationForCancelAsync(reservationId, ct);
        if (metadata is null || metadata.UserId != userId)
        {
            return CancelResult.NotFound("Reservation not found");
        }

        var sortedSeats = metadata.Seats.OrderBy(s => s, StringComparer.Ordinal).ToList();

        // 7.2. SeatLockManager.AcquireAsync(sorted seats), then DbGate, then the existing cancel transaction, release gate.
        await using var seatLocks = await _seatLockManager.AcquireAsync(metadata.ShowId, sortedSeats, ct);

        CancelResult result;
        await using (await _dbGate.WaitAsync(ct))
        {
            result = await _repo.CancelReservationAsync(reservationId, userId, ct);
        }

        // 7.3. After COMMIT only: TakenFilter.Evict for the seats THIS cancel actually freed
        // (the ones whose UPDATE matched). Then release the locks.
        // If the cancel was a no-op (already cancelled), evict nothing.
        if (result.Outcome == CancelResult.OutcomeType.Cancelled && result.FreedSeats is { Count: > 0 })
        {
            foreach (var seat in result.FreedSeats)
            {
                _takenFilter.Evict(metadata.ShowId, seat);
            }
        }

        return result;
    }
}
