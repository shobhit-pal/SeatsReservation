using Dapper;
using Npgsql;
using SeatApi.Models;

namespace SeatApi.Repositories;

public class ShowRepository : IShowRepository
{
    private readonly NpgsqlDataSource _db;

    public ShowRepository(NpgsqlDataSource db) => _db = db;

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
}
