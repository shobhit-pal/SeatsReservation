-- Schema for shows and seats (Commit C)
-- Invariant: available + held + confirmed == total_seats (available = reservation_id IS NULL)

CREATE TABLE IF NOT EXISTS shows (
  id             uuid PRIMARY KEY,
  name           text NOT NULL,
  price_paise    bigint NOT NULL CHECK (price_paise > 0),
  per_user_limit int NOT NULL DEFAULT 4 CHECK (per_user_limit BETWEEN 1 AND 10),
  total_seats    int NOT NULL CHECK (total_seats > 0),
  created_at     timestamptz NOT NULL DEFAULT now()
);

CREATE TABLE IF NOT EXISTS seats (
  show_id        uuid NOT NULL REFERENCES shows(id),
  seat_label     text NOT NULL,
  reservation_id uuid NULL,                 -- NULL = available. No FK on purpose: claimed before the reservation row exists.
  PRIMARY KEY (show_id, seat_label)
);

CREATE TABLE IF NOT EXISTS reservations (
  id            uuid PRIMARY KEY,
  show_id       uuid NOT NULL REFERENCES shows(id),
  user_id       text NOT NULL,
  seats         text[] NOT NULL,
  amount_paise  bigint NOT NULL,
  status        smallint NOT NULL,          -- 1 = confirmed, 2 = cancelled (C# enum)
  created_at    timestamptz NOT NULL DEFAULT now(),
  cancelled_at  timestamptz
);

CREATE TABLE IF NOT EXISTS idempotency_keys (
  user_id        text NOT NULL,
  key            text NOT NULL,
  request_hash   text NOT NULL,
  reservation_id uuid NULL,                 -- filled before commit, never null after commit
  response_json  jsonb NULL,                -- the stored 201 body
  PRIMARY KEY (user_id, key)
);

CREATE TABLE IF NOT EXISTS user_show_quota (
  show_id      uuid NOT NULL REFERENCES shows(id),
  user_id      text NOT NULL,
  active_seats int NOT NULL DEFAULT 0 CHECK (active_seats >= 0),
  PRIMARY KEY (show_id, user_id)
);

