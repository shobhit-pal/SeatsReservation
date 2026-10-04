using Dapper;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace SeatApi.Services.Metrics;

public class SeatsAvailableSyncService : BackgroundService
{
    private readonly NpgsqlDataSource _db;
    private readonly IAppMetrics _metrics;
    private readonly ILogger<SeatsAvailableSyncService> _logger;

    public SeatsAvailableSyncService(
        NpgsqlDataSource db,
        IAppMetrics metrics,
        ILogger<SeatsAvailableSyncService> logger)
    {
        _db = db;
        _metrics = metrics;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30));

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await timer.WaitForNextTickAsync(stoppingToken);
                await SyncSeatsAvailableAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning("Failed to re-sync available seats gauge: {Message}", ex.Message);
            }
        }
    }

    private async Task SyncSeatsAvailableAsync(CancellationToken ct)
    {
        await using var conn = await _db.OpenConnectionAsync(ct);

        // Re-syncs available seats for the 50 newest shows to heal drift and cap cardinality
        const string sql = """
            WITH newest_shows AS (
                SELECT id
                FROM shows
                ORDER BY created_at DESC
                LIMIT 50
            )
            SELECT n.id AS show_id, count(s.seat_label) FILTER (WHERE s.reservation_id IS NULL) AS available_count
            FROM newest_shows n
            LEFT JOIN seats s ON s.show_id = n.id
            GROUP BY n.id;
            """;

        var rows = (await conn.QueryAsync<(Guid ShowId, long AvailableCount)>(
            new CommandDefinition(sql, cancellationToken: ct))).ToList();

        var newestShowIds = rows.Select(r => r.ShowId).ToHashSet();

        foreach (var (showId, count) in rows)
        {
            _metrics.SetSeatsAvailable(showId, (int)count);
        }

        var tracked = _metrics.GetTrackedSeatsAvailableShowIds();
        foreach (var showId in tracked)
        {
            if (!newestShowIds.Contains(showId))
            {
                _metrics.RemoveSeatsAvailable(showId);
            }
        }
    }
}
