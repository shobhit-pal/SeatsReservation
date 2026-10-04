// Seat Reservation API — entry point
// Spec: docs/design.md, sections 13, 14, and commit-01-skeleton-prompt.md

using System.Text.Json;
using Npgsql;

// ---------------------------------------------------------------------------
// 1. Load .env before CreateBuilder so IConfiguration sees the values.
//    DotNetEnv traverses parent dirs, enabling `dotnet run` from any cwd.
// ---------------------------------------------------------------------------
DotNetEnv.Env.TraversePath().Load();

var builder = WebApplication.CreateBuilder(args);

// ---------------------------------------------------------------------------
// 2. Fail fast: both secrets must be present. Named error message helps ops.
// ---------------------------------------------------------------------------
var connectionString = builder.Configuration["ConnectionStrings:Default"]
    ?? throw new InvalidOperationException(
        "Missing required configuration: 'ConnectionStrings__Default'. " +
        "Copy .env.example to .env and set the value.");

var jwtSecret = builder.Configuration["Jwt:Secret"]
    ?? throw new InvalidOperationException(
        "Missing required configuration: 'Jwt__Secret'. " +
        "Copy .env.example to .env and set the value.");

// ---------------------------------------------------------------------------
// 3. Thread-pool and body-size tuning (design.md §10)
// ---------------------------------------------------------------------------
ThreadPool.SetMinThreads(200, 200);

builder.WebHost.ConfigureKestrel(opts =>
{
    // Limit request bodies to 4 MB (design.md §10)
    opts.Limits.MaxRequestBodySize = 4_000_000;
});

// ---------------------------------------------------------------------------
// 4. Dapper: map snake_case column names to PascalCase C# properties
// ---------------------------------------------------------------------------
Dapper.DefaultTypeMap.MatchNamesWithUnderscores = true;

// ---------------------------------------------------------------------------
// 5. JSON: snake_case for both minimal APIs and MVC controllers (design.md §4)
// ---------------------------------------------------------------------------
builder.Services.ConfigureHttpJsonOptions(o =>
    o.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower);

builder.Services
    .AddControllers()
    .AddJsonOptions(o =>
        o.JsonSerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower);

// ---------------------------------------------------------------------------
// 6. Register NpgsqlDataSource as a singleton.
//    Building the data source does NOT open a connection — safe without a DB.
// ---------------------------------------------------------------------------
var dataSource = NpgsqlDataSource.Create(connectionString);
builder.Services.AddSingleton(dataSource);

// ---------------------------------------------------------------------------
// 7. Build the app
// ---------------------------------------------------------------------------
var app = builder.Build();

// ---------------------------------------------------------------------------
// 8. Endpoints
// ---------------------------------------------------------------------------

// Liveness probe — no DB dependency (design.md §4.6)
app.MapGet("/health/live", () => Results.Ok(new { status = "live" }));

// Controllers will be mapped in later commits (auth, shows, reservations)
app.MapControllers();

app.Run();
