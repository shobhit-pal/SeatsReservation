// Seat Reservation API — entry point
// Spec: docs/design.md, sections 13, 14, and commit-01-skeleton-prompt.md

using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Mvc;
using Microsoft.IdentityModel.Tokens;
using Npgsql;
using SeatApi.Models;
using SeatApi.Repositories;
using SeatApi.Services;

// ---------------------------------------------------------------------------
// 1. Load .env before CreateBuilder so IConfiguration sees the values.
//    DotNetEnv traverses parent dirs, enabling `dotnet run` from any cwd.
// ---------------------------------------------------------------------------
DotNetEnv.Env.NoClobber().TraversePath().Load();

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
    "Database pool configured: MinPoolSize={MinPool}, MaxPoolSize={MaxPool}, GateSize={GateSize}",
    csBuilder.MinPoolSize, csBuilder.MaxPoolSize, dbOptions.GateSize);

// ---------------------------------------------------------------------------
// 8. Middleware & Endpoints
// ---------------------------------------------------------------------------

app.UseAuthentication();
app.UseAuthorization();

// Liveness probe — no DB dependency (design.md §4.6)
app.MapGet("/health/live", () => Results.Ok(new { status = "live" }));

// Controllers will be mapped in later commits (auth, shows, reservations)
app.MapControllers();

app.Run();

