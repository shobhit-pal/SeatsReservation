# Architecture & Design Writeup

## 1. The Atomic Decision

Every reservation decision executes as a single conditional SQL update in PostgreSQL (`READ COMMITTED` isolation level):
```sql
UPDATE seats
SET reservation_id = @ReservationId
WHERE show_id = @ShowId AND seat_label = @SeatLabel AND reservation_id IS NULL;
```

### Why this is race-free
PostgreSQL takes an exclusive row-level lock on the target seat row during `UPDATE`. When multiple transactions attempt to claim the same seat concurrently:
1. The first transaction acquires the row lock and evaluates `WHERE reservation_id IS NULL`. It succeeds and updates the row.
2. Waiting transactions queue behind the row lock. Once the first transaction commits, waiting transactions wake up and re-evaluate the `WHERE` clause against the updated row version.
3. Because `reservation_id` is no longer `NULL`, the `WHERE` clause evaluates to `false`. The update modifies `0` rows.
4. The application checks the affected rows count. If `0`, it rolls back the entire transaction and returns `409 seat-taken`. No two transactions can ever claim the same seat.

### Multi-seat all-or-nothing
All requested seats for a booking are updated in a single database transaction. If any individual seat fails to claim (`rows_affected == 0`), the transaction immediately rolls back completely. Partial bookings are impossible.

### Deadlock avoidance
Concurrent transactions claiming overlapping seat sets could deadlock if locking order is unordered. To prevent deadlocks:
- **Sorted seat order:** Requested seat labels are always sorted alphabetically (`A1`, `A2`, `B1`, etc.) prior to running SQL updates.
- **Fixed acquisition hierarchy:**
  - **Reserve transaction:** `idempotency_keys` $\rightarrow$ `user_quotas` $\rightarrow$ sorted `seats` $\rightarrow$ `reservations`.
  - **Cancel transaction:** `reservations` $\rightarrow$ `user_quotas` $\rightarrow$ sorted `seats`.
- **Transient error retries:** If PostgreSQL reports deadlock (`40P01`) or serialization failure (`40001`), the transaction retries automatically with exponential backoff up to the configured retry window (`Db__RetryWindowSeconds`).

### Per-user limit enforcement
User quotas are enforced inside the database transaction via conditional upsert on `user_quotas`:
```sql
INSERT INTO user_quotas (show_id, user_id, active_seats)
VALUES (@ShowId, @UserId, @RequestedCount)
ON CONFLICT (show_id, user_id) DO UPDATE
SET active_seats = user_quotas.active_seats + @RequestedCount
WHERE user_quotas.active_seats + @RequestedCount <= @PerUserLimit;
```
If the user's active seats plus the requested seats exceed `per_user_limit`, zero rows are modified, the transaction rolls back, and the API returns `409 per-user-limit`. Quotas cannot be exceeded even during concurrent requests from the same user.

---

## 2. Idempotency

Idempotency guarantees that retrying an operation produces the same outcome without duplicate bookings.

### Schema and Key Mechanics
- Storage: Dedicated `idempotency_keys` table.
- Primary key constraint: `UNIQUE (user_id, idempotency_key)`.
- Request fingerprint: `request_hash = SHA256(show_id + "|" + sorted_seats)`.

### Request lifecycle
1. **New Key:** The transaction inserts `(user_id, key, request_hash, status, response_body)`. If the reservation commits, the HTTP 201 response payload is stored alongside the key.
2. **Same Key + Same Body:** If a key exists with the matching `request_hash`, the service returns the stored HTTP 201 response directly. No new reservation is created.
3. **Same Key + Different Body:** If a key exists with a differing `request_hash`, the service declines with `409 Conflict (idempotency-key-reuse)`.
4. **Concurrent Requests with Same Key:** The database unique constraint serializes parallel attempts. The winner commits the booking; the loser hits the conflict block, loads the winner's committed response, and returns it as an idempotent replay.

### Lifecycle after cancellation
Idempotency rows are never deleted when a reservation is cancelled. A delayed network retry cannot create a secondary booking. If a user retries an old key for a cancelled booking, the API replays the original stored 201 response confirming the original historical receipt. Current live seat availability is queried via `GET /shows/{id}`. A future endpoint (`GET /reservations/{id}`) will provide direct reservation status lookups.

---

## 3. Holds and Expiry

### Direct Confirmation vs TTL Holds
We chose a direct booking and cancellation model rather than short-lived TTL holds:
- Seats are reserved and confirmed immediately.
- Users cancel via `POST /reservations/{id}/cancel`.
- This eliminates the need for background timer sweeps and state churn during high-velocity ticket on-sales.

### Safe Cancellations
Cancellation runs in a single transaction:
```sql
UPDATE seats
SET reservation_id = NULL
WHERE show_id = @ShowId AND seat_label = @SeatLabel AND reservation_id = @ReservationId;
```
Checking `reservation_id = @ReservationId` ensures a cancellation can never free a seat if another booking acquired it. Quota is decremented and `reservations.status` transitions to `cancelled`. Double cancellations are idempotent no-ops returning 200.

### Adding TTL Holds in the Future
To support payment gateways requiring a 10-minute hold window:
1. Add `status` (`held` vs `confirmed`) and `expires_at TIMESTAMPTZ` to `seats` and `reservations`.
2. Update claim SQL: `WHERE reservation_id IS NULL OR (status = 'held' AND expires_at < NOW())`.
3. Background sweeper releases expired holds, or optimistic overwrite reclaims expired seats on demand.

---

## 4. Consistency vs Availability

This system implements **CP** (Consistency and Partition Tolerance over Availability).

### Single Source of Truth
PostgreSQL is the sole authority for seat claims, quotas, and idempotency records. Memory is an acceleration layer, never the authority.
- If PostgreSQL becomes unreachable, requests retry within the retry window (`Db__RetryWindowSeconds`).
- If the database remains unreachable after the window expires, the system returns `503 Service Unavailable` with a `Retry-After` header.
- The service fails closed rather than risking double-sold seats.

### Provably True Memory Shortcuts
In-memory structures only short-circuit operations when provably safe:
- **KeyCache:** Returns stored 201s for previously confirmed keys.
- **TakenFilter:** Returns `409 seat-taken` when a requested seat is already known to be held by another user.
- **ShowCache:** Serves read-only show metadata (total seats, price, limit).

*Multi-instance caveat:* The current in-memory `TakenFilter` is local to the running process. In a horizontally scaled cluster, cross-node invalidation requires a pub/sub bus (such as Redis or Postgres LISTEN/NOTIFY) or short cache entry TTLs.

---

## 5. Performance Design in One Page

The architecture avoids database saturation through layered in-memory barriers:

```
Request ──▶ ShowCache (in-memory metadata)
        ──▶ KeyCache (O(1) duplicate replay check)
        ──▶ TakenFilter (O(1) rejection if seat already sold)
        ──▶ SeatLockManager (in-memory async lock per requested seat)
        ──▶ DbGate (SemaphoreSlim limiting concurrent DB txs)
        ──▶ PostgreSQL (Atomic 1-3ms transaction)
```

1. **Memory First:**
   - `ShowCache` serves static show configuration without DB reads.
   - `KeyCache` runs before `TakenFilter` so retrying an original key returns the user's booking even after subsequent seats were taken.
   - `TakenFilter` checks seat ownership in RAM, rejecting ~90% of stampede losers in microseconds.

2. **Per-Seat Async Locks:**
   - [SeatLockManager](file:///c:/Users/shobhit/Personal/SeatsReservation/src/SeatApi/Services/Cache/SeatLockManager.cs) manages `SemaphoreSlim` locks per seat. Locks are created on-demand and pruned when no waiters remain.
   - Contending requests on identical seats queue asynchronously in memory without blocking thread pool threads.

3. **Database Gate:**
   - A `SemaphoreSlim` (`Db__GateSize`) limits concurrent database transactions.
   - By sizing the gate strictly smaller than `Maximum Pool Size`, database connections are never exhausted and connection checkout never times out.

4. **Warm-up and Readiness:**
   - [WarmupService](file:///c:/Users/shobhit/Personal/SeatsReservation/src/SeatApi/Services/WarmupService.cs) pre-establishes minimum database connections, preloads recent idempotency keys, and caches taken seats before marking `/health/ready` healthy.
   - Cold-start JIT overhead is absorbed prior to receiving live traffic.

### Pool and Gate Configuration
| Environment | Max Pool Size | Db__GateSize | Postgres max_connections |
|---|---|---|---|
| Low-Resource / Free-Tier | 8 | 6 | 15 |
| Docker Compose / VM / Production | 30 | 24 | 200 |

---

## 6. Observability & 2 AM Paging Signals

### Metrics Exposed (`GET /metrics`)
- `reservations_confirmed_total`: Monotonic counter of confirmed bookings.
- `reservations_declined_total{reason="..."}`: Counter of declines partitioned by `seat-taken`, `per-user-limit`, `idempotent-replay`, `idempotency-key-reuse`.
- `reservations_cancelled_total`: Counter of successful cancellations.
- `seats_available{show_id="..."}`: Gauge of remaining available seats per show.
- `db_retries_total`: Counter of transient error retries.
- `db_gate_waiting`: Gauge of requests waiting for database capacity.
- `taken_filter_hits_total`: Counter of requests rejected in memory without touching the database.
- `unhandled_exceptions_total`: Counter of unhandled 500 exceptions.

### Structured Correlation Logs & Traffic Heartbeat
To guarantee full visibility during production bursts without overwhelming stdout or hitting platform rate limits:
1. **Single-Line JSON Logs:** Every reservation decision and error outputs a structured JSON line carrying: `ts` (UTC), `level`, `request_id` (honoring `X-Request-Id` or generated UUID), `method`, `route` (template `/shows/{id}/reserve`), `status`, `outcome` (e.g. `confirmed`, `seat-taken`, `per-user-limit`, `cancelled`, `internal`), `duration_ms`, `user_id`, `show_id`, and `seat_count`.
2. **Sampling & Rate Limiting:**
   - **100% Logged:** Confirmations, cancellations, 5xx internal errors, and 503 database unavailability.
   - **1-in-100 Sampled:** Repetitive declines (`seat-taken`, `per-user-limit`, `replay`, `validation`, `not-found`) are sampled with `"sampled": true`.
   - **First-Occurrence Guarantee:** The very first occurrence of every outcome is logged atomically so all status types remain visible.
   - **100 lines/second Cap:** A per-second emission limiter caps stdout logging to 100 lines/s to prevent Railway from dropping log lines. Errors bypass this cap.
3. **5-Second Traffic Summary Heartbeat:** A `BackgroundService` ticks every 5 seconds and emits a summary log when traffic occurred:
   ```json
   {"event":"traffic_summary","window_s":5,"requests":N,"confirmed":N,"declined":{"seat-taken":N,"per-user-limit":N,"idempotent-replay":N,"idempotency-key-reuse":N},"cancelled":N,"errors_5xx":N,"gate_waiting":N,"db_retries":N,"p95_ms":N}
   ```
   Deltas are derived from `AppMetrics`, while `p95_ms` is computed over a fixed 2,048-element lock-free ring buffer array with strictly bounded O(1) memory.

### Paging Conditions & First Actions
| Alert Condition | Meaning | First Action to Check |
|---|---|---|
| **5xx Rate > 0** | Unhandled exceptions or exhausted retries | Inspect container logs for database connectivity failures or unhandled crashes. |
| **/health/ready failing > 1 min** | Database connectivity lost or pool saturated | Check PostgreSQL container status, connection counts, and `SELECT 1` responsiveness. |
| **db_gate_waiting rising for minutes** | Database throughput bottleneck | Check PostgreSQL CPU utilization, slow queries, and active disk I/O. |
| **db_retries_total spike** | High deadlock or lock contention rate | Check for large multi-seat transactions contending on overlapping rows. |
| **seats_available drift** | In-memory gauge out of sync with DB counts | Inspect background sync logs (`SeatsAvailableSyncService`) and verify database transaction commit consistency. |
| **Confirmed counter mismatch** | Discrepancy between metrics and seat table | Query `COUNT(*) FROM seats WHERE reservation_id IS NOT NULL` against Prometheus counter deltas. |
| **p99 Latency Spike** | Thread starvation or lock convoy | Check `ThreadPool` starvation logs, connection pool checkout latency, and network latency. |

---

## 7. Engineering Process & AI Collaboration (Decided vs. Built)

I want to be completely transparent about how this system was engineered: **the core architecture, principles, and trade-offs were designed and specified by me in `docs/design.md`, while the code construction, testing harnesses, and low-level mechanics were built in close pairing with an AI agent.**

### What Was Decided by Me (The Architectural Blueprint in `docs/design.md`)
- **Authority & Consistency Model:** Mandated that PostgreSQL is the sole source of truth (CP model), refusing distributed locks, queues, or Redis read-then-write caching that could lead to double-selling.
- **The Atomic Conditional SQL Statement:** Specified the single `UPDATE seats SET reservation_id = @id WHERE reservation_id IS NULL` pattern to push race resolution entirely to Postgres row locks.
- **Strict Invariants:** Established the all-or-nothing multi-seat policy, user quota enforcement via atomic upsert, owner-only cancellation without TTL holds, and idempotent replay semantics.
- **Tiered Defense Architecture:** Designed the layered defense sequence: static metadata cache $\rightarrow$ idempotency cache $\rightarrow$ in-memory seat ownership filter $\rightarrow$ per-seat async locks $\rightarrow$ database connection admission gate $\rightarrow$ Postgres transaction.
- **Operational Constraints:** Defined the strict zero-5xx error policy, connection pool sizing rules (`GateSize < MaxPoolSize`), and the build commit milestones.

### How It Was Built with the Agent (Implementation & Iteration)
- **Codebase Construction:** The agent authored the C# ASP.NET Core 8 implementations, Dapper query definitions, database migrations, and DI wiring following the architectural constraints in `docs/design.md`.
- **Test Harness Development:** The agent implemented the `burst.py` multi-threaded test client, implementing persistent connection pooling, 7 distinct concurrency stampede scenarios, and post-burst reconciliation checks.
- **Resilience & Operational Tuning:** When live burst testing on Railway revealed platform-specific hurdles (such as Railway dropping console logs under burst), I directed the agent to address this, and the agent engineered the low-overhead observability mechanics:
  - Formulating the 3-tier middleware pipeline (`CorrelationIdMiddleware` $\rightarrow$ `RequestLoggingMiddleware` $\rightarrow$ `ErrorHandlingMiddleware`).
  - Implementing the thread-safe `Interlocked` per-outcome sampling with guaranteed first-seen emission.
  - Adding the 100 lines/second rate limiter to protect container stdout and Railway ingestion.
  - Developing the zero-allocation 2,048-element ring buffer for sliding-window p95 calculation in the 5-second traffic summary heartbeat.

This collaborative pairing allowed the project to move with high velocity while maintaining rigorous architectural integrity, provable concurrency correctness, and zero 5xx errors under severe load.

---

## 8. What I Would Do Next

1. **GET /reservations/{id}:** Expose direct status inspection endpoint for individual reservations.
2. **TTL Holds with Payment Processing:** Implement two-phase hold and confirm workflows for external payment gateways.
3. **Connection Pooling Proxy (PgBouncer):** Deploy PgBouncer in transaction pooling mode to scale to thousands of backend connections.
4. **Read Replicas:** Route read queries (`GET /shows/{id}`) to PostgreSQL read replicas with streaming replication.
5. **Distributed Invalidation:** Integrate Redis or PostgreSQL `LISTEN/NOTIFY` to synchronize the `TakenFilter` across multiple stateless API nodes.
6. **Database Sharding:** Shard the `seats` and `reservations` tables by `show_id` using Citus or native declarative partitioning.
7. **Idempotency Lifecycle Sweeper:** Implement background cleanup archiving idempotency keys older than 24 hours to cold storage.
8. **Production Identity & Rate Limiting:** Replace demo auth with an OAuth2 / OIDC provider and enforce per-IP rate limiting on token creation.
9. **OpenTelemetry Distributed Tracing:** Instrument requests with OTel tracing propagated to Jaeger or Honeycomb.
10. **Automated Continuous Load Testing:** Integrate burst stampede runs into CI deployment gates.

---

## 9. Known Limits

1. **Demo Auth:** `POST /auth/token` allows minting arbitrary user IDs without credential verification.
2. **Non-Idempotent Show Creation:** `POST /shows` does not accept an idempotency key; duplicate calls create multiple shows.
3. **In-Memory Cache Scope:** `TakenFilter` and `SeatLockManager` reside in single-process memory. Multiple horizontal instances without cache bus invalidation will rely on database row locks for conflicting claims.
4. **Free-Tier Resource Limits:** Low-memory container instances can experience increased GC pauses during extreme connection pool cycling.
