using System.Data;
using System.Text.Json;
using Dapper;
using Npgsql;
using SeatApi.Models;

namespace SeatApi.Repositories;

public class ReservationRepository : IReservationRepository
{
    private readonly NpgsqlDataSource _db;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
    };

    public ReservationRepository(NpgsqlDataSource db) => _db = db;

    public async Task<Show?> GetShowAsync(Guid showId, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenConnectionAsync(ct);

        // Reads show details and quota limit for reservation validation
        return await conn.QuerySingleOrDefaultAsync<Show>(new CommandDefinition(
            """
            SELECT id, name, price_paise, per_user_limit, total_seats, created_at
            FROM shows
            WHERE id = @Id;
            """,
            new { Id = showId },
            cancellationToken: ct));
    }

    public async Task<IReadOnlyList<string>> GetExistingSeatLabelsAsync(
        Guid showId, IReadOnlyList<string> seatLabels, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenConnectionAsync(ct);

        // Identifies which requested seats actually belong to this show
        var found = await conn.QueryAsync<string>(new CommandDefinition(
            """
            SELECT seat_label
            FROM seats
            WHERE show_id = @ShowId AND seat_label = ANY(@Labels::text[]);
            """,
            new { ShowId = showId, Labels = seatLabels.ToArray() },
            cancellationToken: ct));

        return found.ToList();
    }

    public async Task<ReservationMetadata?> GetReservationForCancelAsync(Guid reservationId, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenConnectionAsync(ct);

        // Reads reservation metadata to identify seats to lock before cancel
        var row = await conn.QuerySingleOrDefaultAsync<(Guid ShowId, string[] Seats, string UserId, short Status)>(new CommandDefinition(
            """
            SELECT show_id, seats, user_id, status
            FROM reservations
            WHERE id = @Id;
            """,
            new { Id = reservationId },
            cancellationToken: ct));

        if (row == default)
        {
            return null;
        }

        return new ReservationMetadata(row.ShowId, row.Seats, row.UserId, row.Status);
    }

    public async Task<ReserveResult> ExecuteReservationAsync(
        Guid reservationId,
        Show show,
        string userId,
        string idempotencyKey,
        string requestHash,
        IReadOnlyList<string> sortedSeats,
        CancellationToken ct = default)
    {
        await using var conn = await _db.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);

        // a. Inserts idempotency key record as an exactly-once guard
        var keyInserted = await conn.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO idempotency_keys (user_id, key, request_hash)
            VALUES (@UserId, @Key, @RequestHash)
            ON CONFLICT DO NOTHING;
            """,
            new { UserId = userId, Key = idempotencyKey, RequestHash = requestHash },
            tx, cancellationToken: ct));

        if (keyInserted == 0)
        {
            // Retrieves existing request hash and response for idempotency evaluation
            var existing = await conn.QuerySingleOrDefaultAsync<(string RequestHash, string? ResponseJson)>(new CommandDefinition(
                """
                SELECT request_hash, response_json::text AS response_json
                FROM idempotency_keys
                WHERE user_id = @UserId AND key = @Key;
                """,
                new { UserId = userId, Key = idempotencyKey },
                tx, cancellationToken: ct));

            await tx.RollbackAsync(ct);

            if (existing.RequestHash == requestHash)
            {
                return ReserveResult.Replay(existing.ResponseJson ?? string.Empty);
            }

            return ReserveResult.Decline(409, "idempotency-key-reuse", "This key was already used with different parameters");
        }

        // b. Atomic per-user seat quota check and increment enforced directly in PostgreSQL
        var quotaUpdated = await conn.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO user_show_quota (show_id, user_id, active_seats)
            VALUES (@ShowId, @UserId, @Count)
            ON CONFLICT (show_id, user_id) DO UPDATE
            SET active_seats = user_show_quota.active_seats + @Count
            WHERE user_show_quota.active_seats + @Count <= @Limit;
            """,
            new { ShowId = show.Id, UserId = userId, Count = sortedSeats.Count, Limit = show.PerUserLimit },
            tx, cancellationToken: ct));

        if (quotaUpdated == 0)
        {
            await tx.RollbackAsync(ct);
            return ReserveResult.Decline(409, "per-user-limit", "Active seat limit exceeded for this show");
        }

        // c. Atomically claims each seat in sorted order to avoid deadlocks
        foreach (var seatLabel in sortedSeats)
        {
            var seatClaimed = await conn.ExecuteAsync(new CommandDefinition(
                """
                UPDATE seats
                SET reservation_id = @ReservationId
                WHERE show_id = @ShowId AND seat_label = @SeatLabel AND reservation_id IS NULL;
                """,
                new { ReservationId = reservationId, ShowId = show.Id, SeatLabel = seatLabel },
                tx, cancellationToken: ct));

            if (seatClaimed == 0)
            {
                await tx.RollbackAsync(ct);

                // Reads taken seats with their current owner user IDs to populate memory filter and response
                var takenRows = (await conn.QueryAsync<(string SeatLabel, string UserId)>(new CommandDefinition(
                    """
                    SELECT s.seat_label, r.user_id
                    FROM seats s
                    JOIN reservations r ON r.id = s.reservation_id
                    WHERE s.show_id = @ShowId
                      AND s.seat_label = ANY(@Labels::text[]);
                    """,
                    new { ShowId = show.Id, Labels = sortedSeats.ToArray() },
                    cancellationToken: ct))).ToList();

                var unavailableSeats = takenRows.Select(r => r.SeatLabel).Distinct(StringComparer.Ordinal).ToList();
                var extra = new Dictionary<string, object>
                {
                    ["unavailable"] = unavailableSeats
                };

                return ReserveResult.Decline(409, "seat-taken", "One or more requested seats are already reserved", extra, takenRows);
            }
        }

        long totalAmountPaise = show.PricePaise * sortedSeats.Count;

        // d. Inserts the confirmed reservation record with total amount in paise
        await conn.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO reservations (id, show_id, user_id, seats, amount_paise, status, created_at)
            VALUES (@Id, @ShowId, @UserId, @Seats, @AmountPaise, @Status, @CreatedAt);
            """,
            new
            {
                Id = reservationId,
                ShowId = show.Id,
                UserId = userId,
                Seats = sortedSeats.ToArray(),
                AmountPaise = totalAmountPaise,
                Status = (short)ReservationStatus.Confirmed,
                CreatedAt = DateTimeOffset.UtcNow
            },
            tx, cancellationToken: ct));

        var response = new ReservationResponse
        {
            ReservationId = reservationId,
            ShowId = show.Id,
            UserId = userId,
            Seats = sortedSeats,
            AmountPaise = totalAmountPaise,
            Status = "confirmed"
        };

        var responseJson = JsonSerializer.Serialize(response, JsonOptions);

        // e. Completes idempotency key entry with confirmed 201 JSON response
        await conn.ExecuteAsync(new CommandDefinition(
            """
            UPDATE idempotency_keys
            SET reservation_id = @ReservationId, response_json = @Json::jsonb
            WHERE user_id = @UserId AND key = @Key;
            """,
            new { ReservationId = reservationId, Json = responseJson, UserId = userId, Key = idempotencyKey },
            tx, cancellationToken: ct));

        // f. Commit transaction and return confirmed response
        await tx.CommitAsync(ct);
        return ReserveResult.Created(response, responseJson);
    }

    public async Task<CancelResult> CancelReservationAsync(
        Guid reservationId,
        string userId,
        CancellationToken ct = default)
    {
        await using var conn = await _db.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);

        // a. Flips confirmed reservation to cancelled once for the authenticated owner
        var confirmedReservation = await conn.QuerySingleOrDefaultAsync<(Guid ShowId, string[] Seats, long AmountPaise)>(new CommandDefinition(
            """
            UPDATE reservations
            SET status = 2, cancelled_at = now()
            WHERE id = @Id AND user_id = @UserId AND status = 1
            RETURNING show_id, seats, amount_paise;
            """,
            new { Id = reservationId, UserId = userId },
            tx, cancellationToken: ct));

        if (confirmedReservation == default)
        {
            // Checks if reservation was already cancelled by owner or does not belong to user
            var existing = await conn.QuerySingleOrDefaultAsync<(Guid ShowId, string[] Seats, long AmountPaise, short Status)>(new CommandDefinition(
                """
                SELECT show_id, seats, amount_paise, status
                FROM reservations
                WHERE id = @Id AND user_id = @UserId;
                """,
                new { Id = reservationId, UserId = userId },
                tx, cancellationToken: ct));

            await tx.RollbackAsync(ct);

            if (existing != default && existing.Status == (short)ReservationStatus.Cancelled)
            {
                return CancelResult.Cancelled(new ReservationResponse
                {
                    ReservationId = reservationId,
                    ShowId = existing.ShowId,
                    UserId = userId,
                    Seats = existing.Seats,
                    AmountPaise = existing.AmountPaise,
                    Status = "cancelled"
                }, freedSeats: Array.Empty<string>(), isEffective: false);
            }

            return CancelResult.NotFound("Reservation not found");
        }

        var showId = confirmedReservation.ShowId;
        var seats = confirmedReservation.Seats;
        var amountPaise = confirmedReservation.AmountPaise;

        // b. Locks quota row before seats to maintain consistent lock order and avoid deadlocks
        await conn.ExecuteAsync(new CommandDefinition(
            """
            SELECT 1 FROM user_show_quota
            WHERE show_id = @ShowId AND user_id = @UserId
            FOR UPDATE;
            """,
            new { ShowId = showId, UserId = userId },
            tx, cancellationToken: ct));

        // c. Atomically frees each seat in sorted order if still held by this reservation
        int freedCount = 0;
        var freedSeats = new List<string>();
        var sortedSeats = seats.OrderBy(s => s, StringComparer.Ordinal).ToList();
        foreach (var seatLabel in sortedSeats)
        {
            var rows = await conn.ExecuteAsync(new CommandDefinition(
                """
                UPDATE seats
                SET reservation_id = NULL
                WHERE show_id = @ShowId AND seat_label = @SeatLabel AND reservation_id = @ReservationId;
                """,
                new { ShowId = showId, SeatLabel = seatLabel, ReservationId = reservationId },
                tx, cancellationToken: ct));

            if (rows > 0)
            {
                freedSeats.Add(seatLabel);
            }

            freedCount += rows;
        }

        // d. Decrements active seats quota by the number of seats actually freed
        await conn.ExecuteAsync(new CommandDefinition(
            """
            UPDATE user_show_quota
            SET active_seats = active_seats - @FreedCount
            WHERE show_id = @ShowId AND user_id = @UserId;
            """,
            new { ShowId = showId, UserId = userId, FreedCount = freedCount },
            tx, cancellationToken: ct));

        await tx.CommitAsync(ct);

        return CancelResult.Cancelled(new ReservationResponse
        {
            ReservationId = reservationId,
            ShowId = showId,
            UserId = userId,
            Seats = seats,
            AmountPaise = amountPaise,
            Status = "cancelled"
        }, freedSeats);
    }
}
