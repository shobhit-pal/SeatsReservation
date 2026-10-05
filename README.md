# Seat Reservation API

High-throughput seat reservation service built on C# / ASP.NET Core 8, Dapper, Npgsql, and PostgreSQL 16.
Guarantees exactly-one booking per seat under concurrent load with atomic SQL updates and in-memory pre-filtering.
All reservation decisions execute in a single conditional SQL transaction—no read-then-write race conditions.

## Live Service
- **Base URL:** `https://seatsreservation-production.up.railway.app`
- **Liveness Probe:** `https://seatsreservation-production.up.railway.app/health/live`
- **Readiness Probe:** `https://seatsreservation-production.up.railway.app/health/ready`
- **Prometheus Metrics:** `https://seatsreservation-production.up.railway.app/metrics`

## Quick Start (Docker)
```bash
docker compose up --build
curl http://localhost:8080/health/ready
```
No `.env` file needed; Docker Compose configures database and API defaults automatically.

## Run Without Docker
Prerequisites: .NET 8 SDK and PostgreSQL 16.
```bash
cp .env.example .env
psql -h localhost -U app -d seats -f db/schema.sql
dotnet run --project src/SeatApi
```

## Environment Variables
| Variable | Description | Default |
|---|---|---|
| `ConnectionStrings__Default` | PostgreSQL connection string | See `.env.example` |
| `Jwt__Secret` | Signing key (min 32 characters) | dev-only-secret-change-in-prod-min-32-chars |
| `Jwt__ExpiryHours` | JWT validity lifetime in hours | `24` |
| `Db__GateSize` | Max concurrent DB transactions in memory | `24` |
| `Db__RetryWindowSeconds` | Max retry duration for transient DB errors | `10` |
| `PORT` | Listening port (Render/Fly/Railway) | `8080` (or `ASPNETCORE_URLS`) |

*Rule:* `Db__GateSize` must stay below `Maximum Pool Size`. Across all servers, `(instances * max_pool_size)` must stay below PostgreSQL `max_connections`.

## Demo Authentication
Mint tokens via `POST /auth/token`:
```bash
curl -X POST http://localhost:8080/auth/token -H "Content-Type: application/json" \
  -d '{"user_id":"alice","role":"user"}'
```
Demo tokens:
- Admin: `DEMO_ADMIN_TOKEN`
- Alice: `DEMO_ALICE_TOKEN`
- Bob: `DEMO_BOB_TOKEN`

*Note on demo auth:* Anyone can mint any identity. Production would use real login/IdP; identity in requests comes only from the signed token, never the body.

## API Reference
**Create Show (Admin):**
```bash
curl -X POST http://localhost:8080/shows -H "Authorization: Bearer DEMO_ADMIN_TOKEN" \
  -H "Content-Type: application/json" \
  -d '{"name":"Concert","price_paise":2500,"per_user_limit":4,"seats":["A1","A2","B1","B2"]}'
```

**Get Show State & Summary:**
```bash
curl "http://localhost:8080/shows/{id}?summary=true"
```

**Reserve Seats:**
```bash
curl -X POST http://localhost:8080/shows/{id}/reserve -H "Authorization: Bearer DEMO_ALICE_TOKEN" \
  -H "Idempotency-Key: KEY-123" -H "Content-Type: application/json" \
  -d '{"seats":["A1","A2"]}'
```

**Cancel Reservation:**
```bash
curl -X POST http://localhost:8080/reservations/{id}/cancel -H "Authorization: Bearer DEMO_ALICE_TOKEN"
```

### Errors & Status Codes
Error envelope: `{"error":"<code-or-status>","message":"<desc>","details":{...}}`
| Status | Meaning | Notes |
|---|---|---|
| 200 | OK | Show state, cancellation |
| 201 | Created | Confirmed reservation or idempotent replay |
| 400 | Bad Request | Validation failure (blank labels, bad JSON) |
| 401 | Unauthorized | Missing or expired JWT |
| 403 | Forbidden | Admin role required |
| 404 | Not Found | Unknown show, unknown seat, or someone else's reservation |
| 409 | Conflict | Business declines: `seat-taken`, `per-user-limit`, `idempotency-key-reuse` |
| 413 | Payload Too Large | Request body exceeds limit |
| 503 | Service Unavailable | Database unreachable after retry window |

## Behaviour Decisions
- **All-or-nothing multi-seat:** Either all requested seats are claimed in one transaction or none are.
- **Limit per show default 4:** Show-level quota limits total seats an individual user may hold.
- **Max seats per request = the limit:** Cannot request more seats in one call than the show limit.
- **Cancel owner-only:** Only the booking owner can cancel; admins cannot cancel user bookings.
- **Declines are not stored:** Failed attempts do not consume idempotency keys or database storage.
- **Replay returns original response:** Exact same key + same body returns the stored 201 response.

## Burst Testing
Run the single-file burst stampede script against local or live deployment:

**Live deployment burst (standard):**
```bash
python3 burst.py https://seatsreservation-production.up.railway.app --users 2000 --hot 5 --seats 500 --concurrency 100 --yes
```

**Full scale burst (20,000 users):**
```bash
python3 burst.py https://seatsreservation-production.up.railway.app --users 20000 --hot 2 --seats 500 --concurrency 100 --yes
```
*(On Windows: `python burst.py ...`)*

- **Scenarios checked:** S1 Hot-seat storm, S2 On-sale mix, S3 Retry storm, S4 Key reuse, S5 Per-user limit, S6 Identity, S7 Cancel and rebook.
- **Interpreting output:** Displays per-scenario latency percentiles (p50/p95/p99/max), rps, and outcome distributions.
- **Exit code:** Returns `0` only if all reconciliation checks and periodic background invariant samples pass; otherwise `1`.

## Metrics Reconciliation
Scraped at `GET /metrics`:
- `reservations_confirmed_total` matches distinct 201 confirmations.
- `reservations_declined_total{reason="..."}` matches 409 outcomes (`seat-taken`, `per-user-limit`, `idempotent-replay`).
- `reservations_cancelled_total` matches distinct 200 cancellations.
- `seats_available{show_id="..."}` matches `counts.available` from `GET /shows/{id}`.

## Health Checks
- `GET /health/live`: 200 if the Kestrel web process is running.
- `GET /health/ready`: 200 only when PostgreSQL connectivity succeeds (`SELECT 1`), database pool is warm, and in-memory caches have preloaded. Returns 503 during warm-up or database outage.
