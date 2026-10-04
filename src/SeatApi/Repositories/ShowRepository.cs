using Dapper;
using Npgsql;
using SeatApi.Models;
using SeatApi.Services.Resilience;

namespace SeatApi.Repositories;

public class ShowRepository : IShowRepository
{
    private readonly NpgsqlDataSource _db;
    private readonly ITransientRetry _transientRetry;

    public ShowRepository(NpgsqlDataSource db, ITransientRetry transientRetry)
    {
        _db = db;
        _transientRetry = transientRetry;
    }

    public async Task CreateAsync(Show show, IReadOnlyList<string> seatLabels,
                                   CancellationToken ct = default)
    {
        await using var conn = await _db.OpenConnectionAsync(ct);
        await using var tx   = await conn.BeginTransactionAsync(ct);

        // Creates the show row with all header fields
        await conn.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO shows (id, name, price_paise, per_user_limit, total_seats, created_at)
            VALUES (@Id, @Name, @PricePaise, @PerUserLimit, @TotalSeats, @CreatedAt)
            """,
            show, tx, cancellationToken: ct));

        // Inserts all seat rows in a single round trip via unnest — avoids N+1
        await conn.ExecuteAsync(new CommandDefinition(
            "INSERT INTO seats (show_id, seat_label) SELECT @ShowId, unnest(@Labels::text[])",
            new { ShowId = show.Id, Labels = seatLabels.ToArray() },
            tx, cancellationToken: ct));

        await tx.CommitAsync(ct);
    }

    public async Task<ShowStateResponse?> GetShowStateAsync(
        Guid showId,
        bool summaryOnly,
        CancellationToken ct = default)
    {
        return await _transientRetry.RunAsync(async () =>
        {
            await using var conn = await _db.OpenConnectionAsync(ct);

            // show lookup
            var show = await conn.QuerySingleOrDefaultAsync<Show>(new CommandDefinition(
                """
                SELECT id, name, price_paise, per_user_limit, total_seats
                FROM shows
                WHERE id = @Id;
                """,
                new { Id = showId },
                cancellationToken: ct));

            if (show is null)
            {
                return null;
            }

            if (summaryOnly)
            {
                // counts in one statement
                var countsRow = await conn.QuerySingleAsync<(long Available, long Confirmed)>(new CommandDefinition(
                    """
                    SELECT count(*) FILTER (WHERE reservation_id IS NULL) AS Available,
                           count(*) FILTER (WHERE reservation_id IS NOT NULL) AS Confirmed
                    FROM seats
                    WHERE show_id = @Id;
                    """,
                    new { Id = showId },
                    cancellationToken: ct));

                return new ShowStateResponse
                {
                    Id = show.Id,
                    Name = show.Name,
                    PricePaise = show.PricePaise,
                    PerUserLimit = show.PerUserLimit,
                    TotalSeats = show.TotalSeats,
                    Counts = new SeatCountsResponse
                    {
                        Available = (int)countsRow.Available,
                        Held = 0,
                        Confirmed = (int)countsRow.Confirmed
                    },
                    Seats = null
                };
            }
            else
            {
                // one snapshot of all seats
                var seatsRows = (await conn.QueryAsync<(string SeatLabel, bool IsAvailable)>(new CommandDefinition(
                    """
                    SELECT seat_label, reservation_id IS NULL AS is_available
                    FROM seats
                    WHERE show_id = @Id
                    ORDER BY seat_label;
                    """,
                    new { Id = showId },
                    cancellationToken: ct))).ToList();

                int availableCount = seatsRows.Count(s => s.IsAvailable);
                int confirmedCount = seatsRows.Count - availableCount;

                var seatList = seatsRows
                    .Select(s => new SeatStatusResponse
                    {
                        Seat = s.SeatLabel,
                        Status = s.IsAvailable ? SeatStatus.Available : SeatStatus.Confirmed
                    })
                    .ToList();

                return new ShowStateResponse
                {
                    Id = show.Id,
                    Name = show.Name,
                    PricePaise = show.PricePaise,
                    PerUserLimit = show.PerUserLimit,
                    TotalSeats = show.TotalSeats,
                    Counts = new SeatCountsResponse
                    {
                        Available = availableCount,
                        Held = 0,
                        Confirmed = confirmedCount
                    },
                    Seats = seatList
                };
            }
        }, ct);
    }
}
