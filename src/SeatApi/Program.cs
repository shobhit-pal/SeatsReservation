// Seat Reservation API — entry point
// Spec: docs/design.md, sections 13, 14, and commit-01-skeleton-prompt.md

using System.Text;
using System.Text.Json;
using Dapper;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Mvc;
using Microsoft.IdentityModel.Tokens;
using Npgsql;
using SeatApi.Middleware;
using SeatApi.Models;
using SeatApi.Repositories;
using SeatApi.Services;
using SeatApi.Services.Cache;
using SeatApi.Services.Metrics;
using SeatApi.Services.Resilience;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Prometheus;

// ---------------------------------------------------------------------------
// 1. Load .env before CreateBuilder so IConfiguration sees the values.
//    DotNetEnv traverses parent dirs, enabling `dotnet run` from any cwd.
// ---------------------------------------------------------------------------
DotNetEnv.Env.NoClobber().TraversePath().Load();

var builder = WebApplication.CreateBuilder(args);

// If PORT environment variable is set (Railway, Render, Fly), listen on that port
var port = Environment.GetEnvironmentVariable("PORT");
if (!string.IsNullOrEmpty(port))
{
    builder.WebHost.UseUrls($"http://0.0.0.0:{port}");
}

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

if (jwtSecret.Length < 32)
{
    throw new InvalidOperationException("Jwt__Secret must be at least 32 characters.");
}

// ---------------------------------------------------------------------------
// 2.1. DbOptions (Options pattern) and connection pool / gate validation
// ---------------------------------------------------------------------------
builder.Services.Configure<DbOptions>(builder.Configuration.GetSection(DbOptions.SectionName));

var dbOptions = new DbOptions();
builder.Configuration.GetSection(DbOptions.SectionName).Bind(dbOptions);

var csBuilder = new NpgsqlConnectionStringBuilder(connectionString);
if (dbOptions.GateSize >= csBuilder.MaxPoolSize || dbOptions.GateSize < 1)
{
    throw new InvalidOperationException("Db__GateSize must be at least 1 and less than Maximum Pool Size");
}

// ---------------------------------------------------------------------------
// 3. Thread-pool and body-size / connection tuning (design.md §10)
// ---------------------------------------------------------------------------
ThreadPool.SetMinThreads(200, 200);

builder.WebHost.ConfigureKestrel(opts =>
{
    // Limit request bodies to 4 MB (design.md §10)
    opts.Limits.MaxRequestBodySize = 4_000_000;
    opts.Limits.MinRequestBodyDataRate = new MinDataRate(bytesPerSecond: 10, gracePeriod: TimeSpan.FromSeconds(30));
    opts.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(30);
    opts.Limits.KeepAliveTimeout = TimeSpan.FromSeconds(120);
    opts.Limits.MaxConcurrentConnections = null;
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
    {
        o.JsonSerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower;
        o.JsonSerializerOptions.NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.Strict;
    })
    .ConfigureApiBehaviorOptions(options =>
    {
        options.InvalidModelStateResponseFactory = context =>
        {
            return new BadRequestObjectResult(new { error = "validation", message = "Invalid JSON or missing fields." });
        };
    });

// ---------------------------------------------------------------------------
// 6. Register NpgsqlDataSource as a singleton.
//    Building the data source does NOT open a connection — safe without a DB.
// ---------------------------------------------------------------------------
var dataSource = NpgsqlDataSource.Create(connectionString);
builder.Services.AddSingleton(dataSource);

// ---------------------------------------------------------------------------
// 6.3. Resilience services (Retry, Observers) & Metrics
// ---------------------------------------------------------------------------
builder.Services.AddSingleton<IAppMetrics, AppMetrics>();
builder.Services.AddSingleton<IDbRetryObserver, PrometheusDbRetryObserver>();
builder.Services.AddSingleton<ITransientRetry, TransientRetry>();
builder.Services.AddHostedService<SeatsAvailableSyncService>();

// ---------------------------------------------------------------------------
// 6.4. In-memory layer singletons (ShowCache, TakenFilter, KeyCache, SeatLockManager, DbGate)
// ---------------------------------------------------------------------------
builder.Services.AddSingleton<IShowCache, ShowCache>();
builder.Services.AddSingleton<ITakenFilter, TakenFilter>();
builder.Services.AddSingleton<IKeyCache, KeyCache>();
builder.Services.AddSingleton<ISeatLockManager, SeatLockManager>();
builder.Services.AddSingleton<IDbGate, DbGate>();

// ---------------------------------------------------------------------------
// 6.45. WarmupService (preloads pool, shows, taken seats, keys)
// ---------------------------------------------------------------------------
builder.Services.AddSingleton<WarmupService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<WarmupService>());

// ---------------------------------------------------------------------------
// 6.5. Show repository and service (Commit D)
// ---------------------------------------------------------------------------
builder.Services.AddScoped<IShowRepository, ShowRepository>();
builder.Services.AddScoped<IShowService, ShowService>();

// ---------------------------------------------------------------------------
// 6.51. Reservation repository and service
// ---------------------------------------------------------------------------
builder.Services.AddScoped<IReservationRepository, ReservationRepository>();
builder.Services.AddScoped<IReservationService, ReservationService>();

// ---------------------------------------------------------------------------
// 6.6. Auth setup
// ---------------------------------------------------------------------------
builder.Services.AddSingleton<IJwtTokenService, JwtTokenService>();

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.MapInboundClaims = false;
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = false,
            ValidateAudience = false,
            ValidateLifetime = true,
            ClockSkew = TimeSpan.Zero,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtSecret)),
            NameClaimType = "sub",
            RoleClaimType = "role"
        };
        options.Events = new JwtBearerEvents
        {
            OnChallenge = context =>
            {
                context.HandleResponse();
                context.Response.StatusCode = 401;
                return context.Response.WriteAsJsonAsync(new { error = "unauthorized", message = "Missing or invalid token" });
            },
            OnForbidden = context =>
            {
                context.Response.StatusCode = 403;
                return context.Response.WriteAsJsonAsync(new { error = "forbidden", message = "Insufficient permissions" });
            }
        };
    });

builder.Services.AddAuthorization(options =>
{
    options.AddPolicy("AdminOnly", policy => policy.RequireRole("admin"));
    options.AddPolicy("AnyUser", policy => policy.RequireAuthenticatedUser());
});

// ---------------------------------------------------------------------------
// 7. Build the app and log pool diagnostic line
// ---------------------------------------------------------------------------
var app = builder.Build();

app.Logger.LogInformation(
    "Database pool configured: MinPoolSize={MinPool}, MaxPoolSize={MaxPool}, GateSize={GateSize}, RetryWindowSeconds={RetryWindow}",
    csBuilder.MinPoolSize, csBuilder.MaxPoolSize, dbOptions.GateSize, dbOptions.RetryWindowSeconds);

// ---------------------------------------------------------------------------
// 8. Middleware & Endpoints
// ---------------------------------------------------------------------------

app.UseMiddleware<ErrorHandlingMiddleware>();

app.UseHttpMetrics();

app.UseAuthentication();
app.UseAuthorization();

// Metrics endpoint — no auth, plain Prometheus text
app.MapMetrics();

// Liveness probe — no DB dependency (design.md §4.6)
app.MapGet("/health/live", () => Results.Ok(new { status = "live" }));

// Readiness probe — checks warm-up completion and live DB connection (cached for 1s)
var lastSelect1TimeUtc = DateTime.MinValue;
var lastSelect1Success = false;
var select1Lock = new object();

app.MapGet("/health/ready", async (WarmupService warmup, NpgsqlDataSource db) =>
{
    if (!warmup.IsWarm)
    {
        return Results.Json(new { status = "not-ready", reason = "warming-up" }, statusCode: 503);
    }

    lock (select1Lock)
    {
        if (DateTime.UtcNow - lastSelect1TimeUtc < TimeSpan.FromSeconds(1))
        {
            return lastSelect1Success
                ? Results.Ok(new { status = "ready" })
                : Results.Json(new { status = "not-ready", reason = "db-unreachable" }, statusCode: 503);
        }
    }

    try
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        await using var conn = await db.OpenConnectionAsync(cts.Token);
        // Validates database connectivity for readiness probe
        await conn.ExecuteScalarAsync<int>(new CommandDefinition("SELECT 1;", cancellationToken: cts.Token));

        lock (select1Lock)
        {
            lastSelect1Success = true;
            lastSelect1TimeUtc = DateTime.UtcNow;
        }

        return Results.Ok(new { status = "ready" });
    }
    catch
    {
        lock (select1Lock)
        {
            lastSelect1Success = false;
            lastSelect1TimeUtc = DateTime.UtcNow;
        }

        return Results.Json(new { status = "not-ready", reason = "db-unreachable" }, statusCode: 503);
    }
});

// Controllers
app.MapControllers();

app.Run();
