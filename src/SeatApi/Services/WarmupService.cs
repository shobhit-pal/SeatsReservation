using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Dapper;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;
using SeatApi.Models;
using SeatApi.Services.Cache;

using SeatApi.Services.Metrics;

namespace SeatApi.Services;

public class WarmupService : IHostedService
{
    private readonly NpgsqlDataSource _db;
    private readonly IShowCache _showCache;
    private readonly ITakenFilter _takenFilter;
    private readonly IKeyCache _keyCache;
    private readonly IAppMetrics _metrics;
    private readonly ILogger<WarmupService> _logger;
    private readonly string _connectionString;

    public bool IsWarm { get; private set; }

    public WarmupService(
        NpgsqlDataSource db,
        IShowCache showCache,
        ITakenFilter takenFilter,
        IKeyCache keyCache,
        IAppMetrics metrics,
        ILogger<WarmupService> logger,
        Microsoft.Extensions.Configuration.IConfiguration config)
    {
        _db = db;
        _showCache = showCache;
        _takenFilter = takenFilter;
        _keyCache = keyCache;
        _metrics = metrics;
        _logger = logger;
        _connectionString = config["ConnectionStrings:Default"] ?? string.Empty;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Starting application warm-up background task...");
        _ = Task.Run(() => RunWarmupLoopAsync(cancellationToken), cancellationToken);
        return Task.CompletedTask;
    }

    private async Task RunWarmupLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && !IsWarm)
        {
            try
            {
                await ExecuteWarmupAsync(ct);
                IsWarm = true;
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning("Warm-up attempt failed ({Message}). Retrying in 2 seconds...", ex.Message);
                try
                {
                    await Task.Delay(2000, ct);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
    }

    private async Task ExecuteWarmupAsync(CancellationToken cancellationToken)
    {
        // 1. Open Minimum Pool Size connections in parallel
        var csBuilder = new NpgsqlConnectionStringBuilder(_connectionString);
        var minPool = Math.Max(1, (int)csBuilder.MinPoolSize);

        var poolTasks = Enumerable.Range(0, minPool).Select(async _ =>
        {
            await using var conn = await _db.OpenConnectionAsync(cancellationToken);
            // Verifies and preheats connection in the database pool during warm-up
            await conn.ExecuteScalarAsync<int>(new CommandDefinition(
                "SELECT 1;",
                cancellationToken: cancellationToken));
        });
        await Task.WhenAll(poolTasks);

        // 2. Load all shows + seat sets into ShowCache (cap at 500 newest)
        await using var mainConn = await _db.OpenConnectionAsync(cancellationToken);

        // Preloads newest shows to warm the immutable show cache
        var shows = (await mainConn.QueryAsync<Show>(new CommandDefinition(
            """
            SELECT id, name, price_paise, per_user_limit, total_seats, created_at
            FROM shows
            ORDER BY created_at DESC
            LIMIT 500;
            """,
            cancellationToken: cancellationToken))).ToList();

        if (shows.Count > 0)
        {
            var showIds = shows.Select(s => s.Id).ToArray();

            // Preloads seat labels for cached shows to eliminate DB lookups on reserve
            var seatRows = await mainConn.QueryAsync<(Guid ShowId, string SeatLabel)>(new CommandDefinition(
                """
                SELECT show_id, seat_label
                FROM seats
                WHERE show_id = ANY(@ShowIds::uuid[]);
                """,
                new { ShowIds = showIds },
                cancellationToken: cancellationToken));

            var seatsByShow = seatRows
                .GroupBy(r => r.ShowId)
                .ToDictionary(g => g.Key, g => g.Select(x => x.SeatLabel).ToList());

            // Preloads available seat counts for cached shows to initialize seats_available gauge
            var availableCounts = await mainConn.QueryAsync<(Guid ShowId, long AvailableCount)>(new CommandDefinition(
                """
                SELECT show_id, count(*) FILTER (WHERE reservation_id IS NULL) AS available_count
                FROM seats
                WHERE show_id = ANY(@ShowIds::uuid[])
                GROUP BY show_id;
                """,
                new { ShowIds = showIds },
                cancellationToken: cancellationToken));

            var countsDict = availableCounts.ToDictionary(x => x.ShowId, x => (int)x.AvailableCount);

            foreach (var show in shows)
            {
                if (seatsByShow.TryGetValue(show.Id, out var labels))
                {
                    _showCache.Set(show, labels);
                }
                else
                {
                    _showCache.Set(show, Array.Empty<string>());
                }

                _metrics.SetSeatsAvailable(show.Id, countsDict.TryGetValue(show.Id, out var count) ? count : 0);
            }
        }

        // 3. Load taken seats with owners into TakenFilter
        // Preloads confirmed taken seats with their owner user IDs into memory filter
        var takenSeats = await mainConn.QueryAsync<(Guid ShowId, string SeatLabel, string UserId)>(new CommandDefinition(
            """
            SELECT s.show_id, s.seat_label, r.user_id
            FROM seats s
            JOIN reservations r ON r.id = s.reservation_id
            WHERE r.status = 1;
            """,
            cancellationToken: cancellationToken));

        foreach (var row in takenSeats)
        {
            _takenFilter.MarkTaken(row.ShowId, row.SeatLabel, row.UserId);
        }

        // 4. Load idempotency keys from the last 24h (cap 100,000) into KeyCache
        // Preloads recent confirmed idempotency keys from the last 24 hours into memory cache
        var keys = await mainConn.QueryAsync<(string UserId, string Key, string RequestHash, string ResponseJson)>(new CommandDefinition(
            """
            SELECT k.user_id, k.key, k.request_hash, k.response_json::text AS response_json
            FROM idempotency_keys k
            JOIN reservations r ON r.id = k.reservation_id
            WHERE r.created_at >= now() - interval '24 hours'
              AND k.response_json IS NOT NULL
            LIMIT 100000;
            """,
            cancellationToken: cancellationToken));

        foreach (var keyRow in keys)
        {
            _keyCache.Set(keyRow.UserId, keyRow.Key, keyRow.RequestHash, keyRow.ResponseJson);
        }

        // 5. Run one dummy call of each hot path to JIT-compile it (validation + hashing)
        var dummyShowId = Guid.NewGuid();
        var dummyHashInput = $"{dummyShowId}|A1,A2";
        _ = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(dummyHashInput))).ToLowerInvariant();
        var dummySet = new HashSet<string>(StringComparer.Ordinal) { "A1", "A2" };
        dummySet.Contains("A1");
        var dummyResponse = new ReservationResponse
        {
            ReservationId = Guid.NewGuid(),
            ShowId = dummyShowId,
            UserId = "warmup",
            Seats = new[] { "A1" },
            AmountPaise = 100,
            Status = "confirmed"
        };
        var dummyJson = JsonSerializer.Serialize(dummyResponse, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower });
        JsonSerializer.Deserialize<ReservationResponse>(dummyJson, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower });

        _logger.LogInformation("Warm-up complete. Preloaded {Shows} shows, {Taken} taken seats, {Keys} idempotency keys.",
            shows.Count, takenSeats.Count(), keys.Count());
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
