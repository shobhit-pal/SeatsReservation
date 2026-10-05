# Seat Reservation at Scale: Design Spec

Stack: C# / ASP.NET Core 8, Dapper + Npgsql, System.Text.Json, Postgres 16.
Goal: never double-sell a seat, never exceed the per-user limit, never double-book a retry, and return **zero 5xx** under a ~20k request burst. Graders test the live service, so correctness and operability matter more than polish.

---

## 1. Principles

1. **Postgres is the only authority.** Memory is a shortcut that avoids DB load. A stale cache may cost an extra DB call, never a wrong booking.
2. **The decision is one conditional SQL statement**, never read-then-write.
3. **Identity comes from the JWT only.** Body fields like `user_id` are ignored.
4. **Declines are 4xx.** Only a truly dead DB, after a long retry window, produces 503.
5. **Consistency over availability (CP).** If the DB is unreachable we refuse to confirm rather than risk a double-sell.
6. One app instance, no queue, no Redis. Mention Redis/pub-sub/PgBouncer/sharding by show as next steps in WRITEUP.md.
7. Money is `long` paise. Never float or decimal.

---

## 2. Business rules (decided)

| Topic | Decision |
|---|---|
| Multi-seat request | **All-or-nothing**: one transaction, any failure rolls everything back. Document why not best-effort: simpler, no partial states, easier to keep correct under concurrency. |
| `per_user_limit` | Stored per show, default 4, allowed 1 to 10. Counts active (non-cancelled) seats across all of that user's requests for that show. |
| Max seats per request | Equal to the show's `per_user_limit`. `len(seats) > per_user_limit` gives 409 per-user-limit with no DB call. |
| Abuse guard | Oversized payloads are bounded by Kestrel's 4 MB limit (413). (Create show retains 50,000 max seats guard). |
| Cancel model | Explicit `POST /reservations/{id}/cancel`, owner only. Admin has no cancel power. No TTL holds. |
| `held` status | Not used. API reports `held: 0` so the invariant keeps its shape. |
| Declines | Never stored. Recomputed on each request, so a seat freed by cancel can be won later. |
| Stored under the idempotency key | Only the successful 201. |
| Replay after cancel | Returns the original stored 201 (the key identifies the original intent). A new booking needs a new key. |
| Same seat, new key, same user | 409 seat-taken (not a second 201). |
| Auth | `POST /auth/token` mints a JWT for any `user_id` and role. Demo shortcut, documented. Also put ready-made admin, alice and bob tokens in the README. |
| One request | One user (from token) and one show (from URL). |

---

## 3. Schema (Postgres)

```sql
CREATE TABLE shows (
  id             uuid PRIMARY KEY,
  name           text NOT NULL,
  price_paise    bigint NOT NULL CHECK (price_paise > 0),
  per_user_limit int NOT NULL DEFAULT 4 CHECK (per_user_limit BETWEEN 1 AND 10),
  total_seats    int NOT NULL CHECK (total_seats > 0),
  created_at     timestamptz NOT NULL DEFAULT now()
);

CREATE TABLE seats (
  show_id        uuid NOT NULL REFERENCES shows(id),
  seat_label     text NOT NULL,
  reservation_id uuid NULL,                 -- NULL = available. No FK on purpose: claimed before the reservation row exists.
  PRIMARY KEY (show_id, seat_label)
);

CREATE TABLE reservations (
  id            uuid PRIMARY KEY,
  show_id       uuid NOT NULL REFERENCES shows(id),
  user_id       text NOT NULL,
  seats         text[] NOT NULL,
  amount_paise  bigint NOT NULL,
  status        smallint NOT NULL,          -- 1 = confirmed, 2 = cancelled (C# enum)
  created_at    timestamptz NOT NULL DEFAULT now(),
  cancelled_at  timestamptz
);

CREATE TABLE idempotency_keys (
  user_id        text NOT NULL,
  key            text NOT NULL,
  request_hash   text NOT NULL,
  reservation_id uuid NULL,                 -- filled before commit, never null after commit
  response_json  jsonb NULL,                -- the stored 201 body
  PRIMARY KEY (user_id, key)
);

CREATE TABLE user_show_quota (
  show_id      uuid NOT NULL REFERENCES shows(id),
  user_id      text NOT NULL,
  active_seats int NOT NULL DEFAULT 0 CHECK (active_seats >= 0),
  PRIMARY KEY (show_id, user_id)
);
```

No `users` table: the JWT carries `user_id` and `role`.
Invariant: `available + held + confirmed == total_seats`, where available = rows with `reservation_id IS NULL`.

---

## 4. API contract

JSON is snake_case. Every non-2xx uses `{ "error": "<code>", "message": "..." }`.

### 4.1 `POST /auth/token` (public, no DB)
Request: `{ "user_id": "bob", "role": "user" }`
200: `{ "token": "<jwt>", "user_id": "bob", "role": "user", "expires_in": 86400 }`

| Rule | Status |
|---|---|
| Invalid JSON or missing field | 400 |
| `user_id` not 1-64 chars of `[A-Za-z0-9_.-]` | 400 |
| `role` not `user` or `admin` | 400 |

JWT: HS256, secret from `Jwt:Secret`, claims `sub` and `role`, 24h expiry.

### 4.2 `POST /shows` (admin)
Request: `{ "name": "friday-night", "seats": ["A1","A2"], "price_paise": 25000, "per_user_limit": 4 }` (`per_user_limit` optional)
201: `{ id, name, price_paise, per_user_limit, total_seats, seats: [{ "seat": "A1", "status": "available" }, ...] }`

| Rule | Status |
|---|---|
| No, bad or expired token | 401 |
| Role is not admin | 403 |
| Invalid JSON, or float/string where integer expected | 400 |
| `name` empty or over 200 chars | 400 |
| `price_paise` not an integer > 0 | 400 |
| `per_user_limit` not an integer 1 to 10 | 400 |
| `seats` empty, over 50,000, blank label, label over 20 chars, or duplicates | 400 |

Flow, one transaction: insert show, then `INSERT INTO seats (show_id, seat_label) SELECT @id, unnest(@labels)`, commit, then load into the memory show cache.

### 4.3 `GET /shows/{id}` (any valid token)
200: `{ id, name, price_paise, per_user_limit, total_seats, counts: { available, held: 0, confirmed }, seats: [...] }`
- Non-UUID id or unknown show gives 404. No or bad token gives 401.
- One query: `SELECT seat_label, reservation_id IS NULL FROM seats WHERE show_id=@s`. Counts are computed in code from that same result, so the invariant holds by construction.
- `?summary=true` returns counts only (`GROUP BY` query), for polling during a burst.

### 4.4 `POST /shows/{id}/reserve` (user)
Request: `{ "seats": ["A12","A13"], "idempotency_key": "..." }`. The key may also come in the `Idempotency-Key` header. If both are given and differ, 400.
201 (also the stored replay): `{ reservation_id, show_id, user_id, seats, amount_paise, status: "confirmed" }`

Checked in this order:

| Case | Status | error |
|---|---|---|
| No or bad token | 401 | unauthorized |
| Show not found (or non-UUID) | 404 | show-not-found |
| Key missing, empty, over 128 chars, or header/body mismatch | 400 | validation |
| `seats` null or empty | 400 | validation |
| `len(seats) > per_user_limit` (O(1) count check before scans/DB) | 409 | per-user-limit |
| Blank labels in `seats` | 400 | validation |
| Duplicate labels in `seats` | 400 | validation |
| Seat not in this show (queried against DB with at most `per_user_limit` entries) | 404 | seat-not-found (`unknown: [...]`) |
| Same key, same body | 201 | stored original (idempotent-replay) |
| Same key, different body | 409 | idempotency-key-reuse |
| Active seats + requested > limit | 409 | per-user-limit |
| Any seat taken | 409 | seat-taken (`unavailable: [...]`) |
| DB unreachable after the retry window | 503 | unavailable (+ `Retry-After`) |

> **Validation Ordering & Side Effect:** Cheap count checks run before any string scanning or database calls. Consequently, subsequent validation steps (blank/duplicate checks) and the DB seat-existence query only ever process at most `per_user_limit` entries.  
> **Side effect:** A request with duplicate seats whose total count exceeds `per_user_limit` will return `409 per-user-limit` instead of `400 validation` because the count check runs first.

Per-user-limit wins over seat-taken when both apply (quota is checked first). Document it.

Request hash: SHA-256 over `show_id` plus the **sorted** seat list. Including the show matters because keys are scoped per user, not per show. Sorting makes `["A1","A2"]` equal to `["A2","A1"]`.

### 4.5 `POST /reservations/{id}/cancel` (user, owner only)
200: `{ reservation_id, show_id, user_id, seats, amount_paise, status: "cancelled" }`

| Case | Status |
|---|---|
| No or bad token | 401 |
| Non-UUID, unknown, or someone else's reservation | 404 reservation-not-found (does not leak existence) |
| Already cancelled by the owner | 200, same body, nothing changes |
| DB unreachable after the retry window | 503 |

### 4.6 Operational
- `GET /health/live`: 200 if the process runs. No DB check.
- `GET /health/ready`: 200 only when `SELECT 1` succeeds **and** the pool is warm **and** warm-up has finished. Otherwise 503 (fails closed). Use it as the platform health check path.
- `GET /metrics`: Prometheus format.

---

## 5. SQL for reserve (one transaction, fixed lock order)

`@r` is a new `Guid` generated in code before `BEGIN`.

```
BEGIN                                   -- READ COMMITTED

-- 1. Idempotency key (unique index = exactly-once)
INSERT INTO idempotency_keys (user_id, key, request_hash)
VALUES (@u, @k, @h) ON CONFLICT DO NOTHING;
   1 row  -> new key, continue
   0 rows -> SELECT request_hash, response_json FROM idempotency_keys WHERE user_id=@u AND key=@k
             same hash -> ROLLBACK, return stored 201 (idempotent-replay)
             different -> ROLLBACK, 409 idempotency-key-reuse

-- 2. Per-user limit (row lock on the quota row serializes one user's parallel requests)
INSERT INTO user_show_quota (show_id, user_id, active_seats) VALUES (@s, @u, @n)
ON CONFLICT (show_id, user_id) DO UPDATE
  SET active_seats = user_show_quota.active_seats + @n
  WHERE user_show_quota.active_seats + @n <= @limit;
   0 rows -> ROLLBACK, 409 per-user-limit
   (first-ever insert skips the WHERE; safe because memory check already ensured n <= limit)

-- 3. Claim each seat, SORTED order (batch via NpgsqlBatch)
UPDATE seats SET reservation_id=@r
WHERE show_id=@s AND seat_label=@x AND reservation_id IS NULL;
   0 rows -> ROLLBACK, 409 seat-taken

-- 4. Record the booking
INSERT INTO reservations (id, show_id, user_id, seats, amount_paise, status)
VALUES (@r, @s, @u, @seats, @pricePaise * @n, 1);

-- 5. Complete the key row with the 201 body
UPDATE idempotency_keys SET reservation_id=@r, response_json=@json WHERE user_id=@u AND key=@k;

COMMIT -> 201
```

After a seat-taken rollback, one separate read (`seats JOIN reservations`) returns the unavailable seats with their owners. It feeds the 409 body and the taken filter.

Rows affected is the signal. 0 rows on the key insert is not a failure, it means "replay path". The 201 status is chosen by code, not by SQL.

## 6. SQL for cancel (one transaction)

```
BEGIN
UPDATE reservations SET status=2, cancelled_at=now()
WHERE id=@r AND user_id=@u AND status=1
RETURNING show_id, seats, amount_paise;
   0 rows -> SELECT by id AND user_id: found (status 2) -> 200 cancelled; not found -> 404

SELECT 1 FROM user_show_quota WHERE show_id=@s AND user_id=@u FOR UPDATE;   -- quota before seats, same as reserve

UPDATE seats SET reservation_id=NULL                                         -- per seat, SORTED
WHERE show_id=@s AND seat_label=@x AND reservation_id=@r;                    -- guard: never frees another booking's seat

UPDATE user_show_quota SET active_seats = active_seats - @n WHERE show_id=@s AND user_id=@u;
COMMIT -> 200
```

## 7. Lock order (deadlock avoidance)

- Reserve: idempotency key, then quota row, then seats in sorted order.
- Cancel: reservation row, then quota row, then seats in sorted order.
- Both take quota before seats, and always seats in sorted order, so no cycle can form.
- Backup: retry on Postgres 40P01 (deadlock) and 40001.

## 8. Concurrency safety table

| Race | Why it is safe |
|---|---|
| 500 users on A12 | Row lock on the seat. Winner commits, others re-check `reservation_id IS NULL`, get 0 rows, get 409. |
| Same key twice at once | Second insert waits on the unique index. First commits: second returns stored 201. First rolled back: second proceeds as new. |
| 10 parallel reserves, limit 4 | All serialize on the quota row. The 5th sees `4 + 1 <= 4` false. Limit is enforced in SQL, never in C#. |
| Half-booked multi-seat | Impossible. Quota, seats and key row roll back together. |
| Cancel racing reserve | Both take the seat row lock. Either order leaves a consistent state. |
| Double cancel | Second waits on the reservation row, sees `status=1` false, returns 200 no-op. Seats and quota change once. |
| Multiple app servers | Locks and rollback live in Postgres, so they work across servers. Only the in-memory layer is per-instance. |

---

## 9. In-memory layer (per instance)

Request pipeline, memory first and DB last:

```
1. Auth (JWT, memory)                          -> 401/403
2. Show lookup (memory; on miss load from DB, 404 only if DB says none; cache "not found" ~1s)
3. Validate body, key, seats vs show seat set, len <= per_user_limit
4. Key cache (user,key): same hash -> 201 replay | different hash -> 409   (BEFORE the taken filter)
5. Taken filter: any seat taken by ANOTHER user -> 409 seat-taken
6. Per-seat lock(s), sorted order; re-check filter after acquiring
7. DB gate (SemaphoreSlim smaller than pool size)
8. DB transaction (section 5) wrapped in transient-error retry
9. After commit or decline: update filter and key cache, metrics
```

Components:
- **Show cache:** show metadata and seat set, loaded at boot and on miss. Immutable after creation.
- **Taken filter:** `(show, seat) -> owner user_id`. Written only **after** the DB answers (after commit, or after a 0-row loss). Never speculatively. A hit for a different user is an instant 409. The owner's own retry skips the instant 409 and falls to the key cache and DB.
- **Key cache:** `(user, key) -> (hash, response)`, warmed at boot with recent keys (e.g. 24h). The DB key lookup stays the final authority. Checked before the filter, so Bob's retry after Alice took his old seat still gets his original 201.
- **Per-seat async locks:** one lock per seat, only while someone waits (create on demand, remove when the last waiter leaves). A seat already in the filter exits early with no lock. Multi-seat requests lock in **sorted order**. **Cancel takes the same locks** and releases them after evicting the filter entry. The loser holds the lock from its DB attempt through the filter write, so a cancel can never interleave and leave a permanent stale "taken" entry.
- **DB gate:** `SemaphoreSlim` smaller than the Npgsql pool (e.g. gate 30, pool 30 to 50). Requests wait in memory (async, no thread held), then re-check the filter, so a hot-seat storm of 500 produces about one DB transaction, not 500.
- **Order:** seat lock, then gate, then DB.
- **Limit:** this is correct on one instance. With several instances, give filter entries a short TTL (about 2s) or use pub/sub. Document it.
- Do not 429 on long queues. Keep the queue in memory and rely on timeouts.

## 10. Connection pool and 5xx rules

| Environment | Maximum Pool Size | Db__GateSize | Postgres max_connections |
|---|---|---|---|
| Low-resource / Free-tier DB | 8 | 6 | 15 |
| Compose / Own VM / Production | 30 | 24 | 200 (set in compose) |

> **Note:** All of these values are configuration (connection string and `Db__GateSize` environment variable), never hard-coded.

Connection string (dev):
`Host=localhost;Port=5432;Database=seats;Username=app;Password=app;Minimum Pool Size=10;Maximum Pool Size=30;Keepalive=15;Connection Idle Lifetime=300;Timeout=60;Command Timeout=30`

- Servers x max pool size must stay below Postgres `max_connections`.
- Min pool gives a warm start. Idle lifetime shrinks the pool after a burst (never below min). Keepalive stops firewalls and hosts from silently killing idle connections. These are different jobs.
- `ThreadPool.SetMinThreads(200, 200)` at startup. Kestrel body limit set.
- **Retry transient errors only** (`NpgsqlException`, 40P01, 40001, 57P01) with backoff (50, 100, 200ms and so on), total window about 10 to 20s, below the grader's client timeout. Never retry a 409.
- An ambiguous commit is safe to retry because the key row makes the retry return the stored 201.
- **503 only after the window fails**, with `Retry-After`. While the DB is down, taken-filter hits still return 409, and bad input still returns 400/404.
- Global error handler: bad JSON gives 400, oversize body 413, unknown route 404, wrong method 405, non-UUID id 404, anything else logged and mapped to a domain response.

| Cause of 5xx | Fix |
|---|---|
| Pool exhausted | Gate below pool, `Timeout=60`, wait in memory |
| Dead or stale connection | Keepalive, idle lifetime, retry |
| Deadlock | Fixed lock order, retry |
| Cold start | Warm pool, preloaded cache, ready only after warm-up |
| Thread starvation | `SetMinThreads`, always-on host |
| Unhandled exception | Global handler |

## 11. Warm-up (before `/health/ready` returns 200)

1. Open the minimum pool connections.
2. Load shows, seat sets, taken seats with owners, and recent idempotency keys into memory.
3. Run a dummy query on each hot path (JIT). Publish with ReadyToRun.
4. Set min threads.

---

## 12. Metrics and logs (build after the business logic)

Metrics:
- `reservations_confirmed_total` (counter)
- `reservations_declined_total{reason="seat-taken|per-user-limit|idempotent-replay"}` (counter)
- `reservations_cancelled_total` (counter)
- `seats_available` (gauge): adjust only after commit, and re-sync from a cheap `COUNT` about every 30s.

They must reconcile with `GET /shows/{id}` and with what the burst script observes.

Logs: structured JSON with a correlation id per request (honour `X-Request-Id`, else generate). Ship to Axiom asynchronously in batches so logging can never block or fail a request. Do not log every 409 at info level during a burst: count in metrics and sample logs. Check Axiom's current free-tier limits.

---

## 13. Naming and code rules

- Classes, interfaces (`I` prefix), records, enums, methods, properties: UpperCamelCase.
- Locals and parameters: camelCase. Private fields: `_camelCase`. Async methods end with `Async`.
- JSON snake_case via `JsonNamingPolicy.SnakeCaseLower`. DB tables and columns snake_case. Dapper `MatchNamesWithUnderscores = true`.
- Layers: Controller (HTTP only), then Service (business rules), then Repository (SQL only).
- Domain outcomes use a Result type, not exceptions.
- SOLID and DRY. Patterns allowed: Repository, Options, Decorator (cache over repository), Result. No others without a reason.
- LINQ for validation, mapping and filtering in memory. Avoid it in tight hot-path loops.
- Every SQL statement has a one-line comment saying what it decides.

## 14. Config and secrets

- `.env` is git-ignored. `.env.example` is committed with dev-only values. Loaded with `DotNetEnv.Env.TraversePath().Load()` before `CreateBuilder`.
- Real environment variables override `.env`, which overrides `appsettings.json`.
- `ConnectionStrings__Default` maps to `ConnectionStrings:Default`. `Jwt__Secret` maps to `Jwt:Secret`.
- Prod secrets are set in the platform's env vars. Never commit them. Put only generated demo **tokens** (not the secret) in the README.

---

## 15. Build order (one commit each, conventional messages)

1. `chore: project skeleton, gitignore, env example`
2. `feat: postgres schema and docker compose (db only)`
3. `feat: global error handling`
4. `feat: auth token endpoint and JWT middleware`
5. `feat: create show`
6. `feat: get show state`
7. `feat: reserve (db transaction, idempotency, quota)`
8. `feat: reserve in-memory layer (filter, key cache, seat locks, gate)`
9. `feat: cancel`
10. `test: burst script`
11. `feat: metrics, structured logs, health/ready`
12. `chore: Dockerfile, deploy config, README, WRITEUP`

Then: full-flow test, deploy, test the live URL.

## 16. Tests that must pass before deploy

- 500 users on one seat: exactly one 201, 499 are 409, zero 5xx.
- 20k mixed requests with hot seats and retries: zero 5xx, invariant holds during and after.
- Same key twice: one reservation. Same key with different seats: 409.
- One user, 10 parallel reserves, limit 4: at most 4 seats held.
- Spoofed `user_id` in the body is ignored. Cancelling someone else's reservation gives 404.
- Bob books, cancels, Alice books, Bob retries his old key: Bob gets his original 201.
- Cancel then rebook of the same seat works. Double cancel is a no-op 200.
- Stop the DB mid-burst: `/health/ready` goes 503, requests wait and retry, and recover when the DB returns.
- Cold start: restart the app, hit `/health/ready`, then burst immediately.

