# Seat Reservation API

A high-throughput seat reservation service built on **C# / ASP.NET Core 8**, **Dapper**, **Npgsql**, and **PostgreSQL 16**.

Guarantees exactly-one seat booking under concurrent load — no double-sells, no per-user limit breaches, no duplicate bookings on retried requests. All reservation decisions are a single conditional SQL statement inside a Postgres transaction; the application never reads-then-writes.

Key behaviours:
- **All-or-nothing multi-seat reservations** — one transaction, rolls back on any conflict.
- **Idempotent requests** — same key + same body always returns the original 201; different body returns 409.
- **Per-user quota** enforced in SQL, not in application memory.
- **No 5xx under a ~20k request burst** — 4xx for every business decline, 503 only after the DB retry window is exhausted.
- **JWT-based identity** — `user_id` comes from the token only; body fields are ignored.
- **CP over AP** — refuses to confirm if the DB is unreachable rather than risk a double-sell.

> **Database Note:** `db/schema.sql` is mounted to `/docker-entrypoint-initdb.d/` in docker compose and executes automatically only on the first start of a fresh volume.

