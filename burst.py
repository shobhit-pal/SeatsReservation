#!/usr/bin/env python3
"""
One-Command Burst Stampede Test
Reproduces high-concurrency ticket stampede scenarios against the Seat Reservation API,
evaluates latency percentiles, and verifies 7 reconciliation invariants.
"""

# ==== options
import argparse
import collections
import concurrent.futures
import http.client
import json
import math
import os
import queue
import random
import sys
import threading
import time
import urllib.parse
import uuid

# Enable ANSI escape sequences on Windows console if supported
if os.name == "nt":
    os.system("")

# Ensure stdout and stderr flush line-by-line in non-interactive/piped environments
if hasattr(sys.stdout, "reconfigure"):
    sys.stdout.reconfigure(line_buffering=True)
if hasattr(sys.stderr, "reconfigure"):
    sys.stderr.reconfigure(line_buffering=True)

COLOR_GREEN = "\033[92m"
COLOR_RED = "\033[91m"
COLOR_YELLOW = "\033[93m"
COLOR_RESET = "\033[0m"


def parse_args():
    parser = argparse.ArgumentParser(
        description="Run concurrent burst stampede tests against the Seat Reservation API.",
        usage="python3 burst.py <BASE_URL> [--users 2000] [--hot 5] [--seats 500] [--concurrency 500] [--timeout 60] [--yes]",
    )
    parser.add_argument("base_url", help="Target API base URL (e.g. http://localhost:5041)")
    parser.add_argument("--users", type=int, default=2000, help="Number of simulated users (default: 2000)")
    parser.add_argument("--hot", type=int, default=5, dest="hot_seats", help="Number of hot seats (default: 5)")
    parser.add_argument("--seats", type=int, default=500, dest="total_seats", help="Total seats in show (default: 500, must be >= 40 + hot)")
    parser.add_argument("--concurrency", type=int, default=500, help="Max worker concurrency (default: 500)")
    parser.add_argument("--timeout", type=float, default=60.0, help="HTTP request timeout in seconds (default: 60)")
    parser.add_argument("--yes", "-y", action="store_true", help="Skip safety prompt for non-localhost targets")

    args = parser.parse_args()

    parsed = urllib.parse.urlparse(args.base_url)
    if not parsed.scheme or not parsed.netloc:
        sys.stderr.write(f"Error: Invalid BASE_URL '{args.base_url}'. Must include scheme (http:// or https://).\n")
        sys.exit(1)

    # Validate seat count requirement
    min_required_seats = 40 + args.hot_seats
    if args.total_seats < min_required_seats:
        sys.stderr.write(
            f"Error: --seats must be at least {min_required_seats} (40 + hot={args.hot_seats}), got {args.total_seats}.\n"
        )
        sys.exit(1)

    host = (parsed.hostname or "").lower()
    is_localhost = host in ("localhost", "127.0.0.1", "::1")

    # Safety confirmation if non-localhost and --yes is not passed
    if not is_localhost and not args.yes:
        estimated_requests = (args.hot_seats * min(args.users, 200)) + int(args.users * 1.5) + 30
        print(f"{COLOR_YELLOW}SAFETY WARNING: Target URL '{args.base_url}' is not localhost.{COLOR_RESET}")
        print(f"{COLOR_YELLOW}Planned requests: ~{estimated_requests} requests with concurrency {args.concurrency}.{COLOR_RESET}")
        try:
            choice = input(f"{COLOR_YELLOW}Are you sure you want to proceed? Type 'yes' to continue: {COLOR_RESET}").strip()
        except (KeyboardInterrupt, EOFError):
            print("\nAborted.")
            sys.exit(1)

        if choice.lower() != "yes":
            print("Aborted by user.")
            sys.exit(1)

    return args


# ==== client
class ConnectionPool:
    """Fixed-size pool of persistent HTTP/HTTPS connections reused across workers."""

    def __init__(self, scheme: str, host: str, port: int, timeout: float, max_size: int):
        self.scheme = scheme
        self.host = host
        self.port = port
        self.timeout = timeout
        self.max_size = max_size
        self._pool = queue.LifoQueue(maxsize=max_size)

    def _create_connection(self):
        if self.scheme == "https":
            return http.client.HTTPSConnection(self.host, self.port, timeout=self.timeout)
        return http.client.HTTPConnection(self.host, self.port, timeout=self.timeout)

    def warm(self, endpoint: str = "/health/live"):
        """Pre-establishes all connections and performs an initial handshake in parallel."""
        def _warm_one():
            conn = self._create_connection()
            try:
                conn.request("GET", endpoint)
                resp = conn.getresponse()
                resp.read()
            except Exception:
                pass
            return conn

        with concurrent.futures.ThreadPoolExecutor(max_workers=min(self.max_size, 32)) as tp:
            conns = list(tp.map(lambda _: _warm_one(), range(self.max_size)))
        for c in conns:
            try:
                self._pool.put_nowait(c)
            except queue.Full:
                pass

    def acquire(self):
        try:
            return self._pool.get_nowait()
        except queue.Empty:
            return self._create_connection()

    def release(self, conn, is_broken: bool = False):
        if is_broken:
            try:
                conn.close()
            except Exception:
                pass
            conn = self._create_connection()
        try:
            self._pool.put_nowait(conn)
        except queue.Full:
            try:
                conn.close()
            except Exception:
                pass


class HttpClient:
    """Thread-safe HTTP client utilizing a fixed connection pool with latency metrics."""

    def __init__(self, base_url: str, concurrency: int, timeout: float = 60.0):
        self.parsed = urllib.parse.urlparse(base_url)
        self.scheme = self.parsed.scheme.lower()
        self.host = self.parsed.hostname
        self.port = self.parsed.port or (443 if self.scheme == "https" else 80)
        self.path_prefix = self.parsed.path.rstrip("/")
        self.timeout = timeout

        # Use a fixed pool of at most min(concurrency, 100) persistent connections
        self.pool_size = min(concurrency, 100)
        self.pool = ConnectionPool(self.scheme, self.host, self.port, timeout, self.pool_size)

        self.first_5xx_errors = []
        self._5xx_lock = threading.Lock()

    def warm_connections(self):
        """Warm all connections with GET /health/live before benchmark scenarios run."""
        path = f"{self.path_prefix}/health/live" if self.path_prefix else "/health/live"
        self.pool.warm(path)

    def request(self, method: str, endpoint: str, headers: dict = None, body: any = None):
        """Sends an HTTP request using a pooled persistent connection with latency timing."""
        path = f"{self.path_prefix}/{endpoint.lstrip('/')}"
        hdrs = dict(headers) if headers else {}
        body_bytes = None

        if body is not None:
            if isinstance(body, (dict, list)):
                body_bytes = json.dumps(body).encode("utf-8")
                if "Content-Type" not in hdrs:
                    hdrs["Content-Type"] = "application/json"
            elif isinstance(body, str):
                body_bytes = body.encode("utf-8")
            elif isinstance(body, bytes):
                body_bytes = body

        start = time.perf_counter()
        conn = self.pool.acquire()
        is_broken = False
        try:
            conn.request(method, path, body=body_bytes, headers=hdrs)
            resp = conn.getresponse()
            raw_data = resp.read().decode("utf-8", errors="replace")
            duration_ms = (time.perf_counter() - start) * 1000.0

            # Capture first 5 responses with 5xx status code
            if 500 <= resp.status < 600:
                req_id = resp.getheader("x-request-id") or resp.getheader("X-Request-Id") or "none"
                with self._5xx_lock:
                    if len(self.first_5xx_errors) < 5:
                        self.first_5xx_errors.append({
                            "status": resp.status,
                            "body": raw_data[:200].replace("\n", " ").replace("\r", ""),
                            "request_id": req_id
                        })

            return resp.status, raw_data, duration_ms, False, None
        except (http.client.CannotSendRequest, http.client.RemoteDisconnected,
                BrokenPipeError, ConnectionResetError):
            # Connection dropped or stale: recreate and retry once
            is_broken = True
            try:
                conn = self.pool._create_connection()
                conn.request(method, path, body=body_bytes, headers=hdrs)
                resp = conn.getresponse()
                raw_data = resp.read().decode("utf-8", errors="replace")
                duration_ms = (time.perf_counter() - start) * 1000.0

                if 500 <= resp.status < 600:
                    req_id = resp.getheader("x-request-id") or resp.getheader("X-Request-Id") or "none"
                    with self._5xx_lock:
                        if len(self.first_5xx_errors) < 5:
                            self.first_5xx_errors.append({
                                "status": resp.status,
                                "body": raw_data[:200].replace("\n", " ").replace("\r", ""),
                                "request_id": req_id
                            })

                is_broken = False
                return resp.status, raw_data, duration_ms, False, None
            except Exception as ex2:
                duration_ms = (time.perf_counter() - start) * 1000.0
                return 0, "", duration_ms, True, str(ex2)
        except Exception as ex:
            is_broken = True
            duration_ms = (time.perf_counter() - start) * 1000.0
            return 0, "", duration_ms, True, str(ex)
        finally:
            self.pool.release(conn, is_broken=is_broken)

    def get_token(self, user_id: str, role: str = "user") -> str:
        status, body, _, is_timeout, err = self.request(
            "POST", "auth/token", body={"user_id": user_id, "role": role}
        )
        if status != 200:
            raise RuntimeError(f"Failed to get token for {user_id} (status={status}): {body or err}")
        data = json.loads(body)
        return data["token"]

    def create_show(self, admin_token: str, name: str, price_paise: int, per_user_limit: int, seats: list) -> str:
        status, body, _, is_timeout, err = self.request(
            "POST",
            "shows",
            headers={"Authorization": f"Bearer {admin_token}"},
            body={"name": name, "price_paise": price_paise, "per_user_limit": per_user_limit, "seats": seats},
        )
        if status != 201:
            raise RuntimeError(f"Failed to create show (status={status}): {body or err}")
        data = json.loads(body)
        return data["id"]

    def reserve(self, token: str, show_id: str, seats: list, key: str, body_user_id: str = None) -> dict:
        headers = {
            "Authorization": f"Bearer {token}",
            "Idempotency-Key": key,
        }
        payload = {"seats": seats, "idempotency_key": key}
        if body_user_id is not None:
            payload["user_id"] = body_user_id

        status, body, duration_ms, is_timeout, err_msg = self.request(
            "POST", f"shows/{show_id}/reserve", headers=headers, body=payload
        )

        error_code = None
        message = None
        reservation_id = None
        returned_user_id = None
        returned_seats = None

        if body:
            try:
                data = json.loads(body)
                error_code = data.get("error")
                message = data.get("message")
                reservation_id = data.get("reservation_id")
                returned_user_id = data.get("user_id")
                returned_seats = data.get("seats")
            except Exception:
                pass

        return {
            "status_code": status,
            "duration_ms": duration_ms,
            "is_timeout": is_timeout,
            "error_code": error_code,
            "error_message": message or err_msg,
            "reservation_id": reservation_id,
            "user_id": returned_user_id,
            "seats": returned_seats or seats,
            "is_replay": False,
        }

    def cancel(self, token: str, reservation_id: str) -> dict:
        headers = {"Authorization": f"Bearer {token}"}
        status, body, duration_ms, is_timeout, err_msg = self.request(
            "POST", f"reservations/{reservation_id}/cancel", headers=headers
        )

        error_code = None
        message = None
        if body:
            try:
                data = json.loads(body)
                error_code = data.get("error")
                message = data.get("message")
            except Exception:
                pass

        return {
            "status_code": status,
            "duration_ms": duration_ms,
            "is_timeout": is_timeout,
            "error_code": error_code,
            "error_message": message or err_msg,
            "reservation_id": reservation_id,
            "is_replay": False,
        }

    def get_show_summary(self, token: str, show_id: str) -> tuple:
        status, body, _, _, _ = self.request(
            "GET", f"shows/{show_id}?summary=true", headers={"Authorization": f"Bearer {token}"}
        )
        if status != 200:
            return status, 0, 0, 0, 0
        data = json.loads(body)
        counts = data.get("counts", {})
        return (
            status,
            counts.get("available", 0),
            counts.get("held", 0),
            counts.get("confirmed", 0),
            data.get("total_seats", 0),
        )

    def get_show_full(self, token: str, show_id: str) -> tuple:
        status, body, _, _, _ = self.request(
            "GET", f"shows/{show_id}", headers={"Authorization": f"Bearer {token}"}
        )
        if status != 200:
            return status, 0, 0, 0, 0, []
        data = json.loads(body)
        counts = data.get("counts", {})
        seats = [(s.get("seat"), s.get("status")) for s in data.get("seats", [])]
        return (
            status,
            counts.get("available", 0),
            counts.get("held", 0),
            counts.get("confirmed", 0),
            data.get("total_seats", 0),
            seats,
        )

    def get_metrics(self) -> dict:
        status, body, _, _, _ = self.request("GET", "metrics")
        if status != 200:
            return {}
        metrics = {}
        for line in body.splitlines():
            line = line.strip()
            if not line or line.startswith("#"):
                continue
            parts = line.split(" ")
            if len(parts) == 2:
                try:
                    metrics[parts[0]] = float(parts[1])
                except ValueError:
                    pass
        return metrics


# ==== setup
def compute_stats(outcomes: list, duration_s: float) -> dict:
    total_requests = len(outcomes)
    rps = (total_requests / duration_s) if duration_s > 0 else 0

    count_201 = 0
    count_replay = 0
    count_409_seat_taken = 0
    count_409_per_user_limit = 0
    count_409_key_reuse = 0
    count_409_other = 0
    other_4xx = collections.defaultdict(int)
    count_5xx = collections.defaultdict(int)
    count_timeouts = 0

    durations = sorted(o["duration_ms"] for o in outcomes)

    def percentile(p):
        if not durations:
            return 0.0
        idx = int(math.ceil(p / 100.0 * len(durations))) - 1
        idx = max(0, min(idx, len(durations) - 1))
        return durations[idx]

    for o in outcomes:
        if o.get("is_timeout"):
            count_timeouts += 1
            continue

        sc = o.get("status_code", 0)
        if sc == 201:
            if o.get("is_replay"):
                count_replay += 1
            else:
                count_201 += 1
        elif sc == 409:
            err = o.get("error_code")
            if err == "seat-taken":
                count_409_seat_taken += 1
            elif err == "per-user-limit":
                count_409_per_user_limit += 1
            elif err == "idempotency-key-reuse":
                count_409_key_reuse += 1
            else:
                count_409_other += 1
        elif 400 <= sc < 500:
            other_4xx[sc] += 1
        elif 500 <= sc < 600:
            count_5xx[sc] += 1

    return {
        "total_requests": total_requests,
        "total_duration_s": duration_s,
        "rps": rps,
        "count_201": count_201,
        "count_replay": count_replay,
        "count_409_seat_taken": count_409_seat_taken,
        "count_409_per_user_limit": count_409_per_user_limit,
        "count_409_key_reuse": count_409_key_reuse,
        "count_409_other": count_409_other,
        "other_4xx": dict(other_4xx),
        "count_5xx": dict(count_5xx),
        "count_timeouts": count_timeouts,
        "latency_p50": percentile(50),
        "latency_p95": percentile(95),
        "latency_p99": percentile(99),
        "latency_max": durations[-1] if durations else 0.0,
    }


# ==== scenarios
class Scenarios:
    def __init__(self, client: HttpClient, pool: concurrent.futures.ThreadPoolExecutor,
                 admin_token: str, users: list, show_id: str,
                 general_seats: list, hot_seats: list, private_seats: list, concurrency: int):
        self.client = client
        self.pool = pool
        self.admin_token = admin_token
        self.users = users
        self.show_id = show_id
        self.general_seats = general_seats
        self.hot_seats = hot_seats
        self.private_seats = private_seats
        self.concurrency = concurrency

        self.confirmed_reservations = {}  # res_id -> seat
        self.cancelled_reservations = set()
        self.user_held_seats = collections.defaultdict(list)  # user_id -> [seat]
        self.winning_hot_seats = {}  # seat -> res_id
        self.all_outcomes = []
        self._lock = threading.Lock()

    def _get_available_general_seats(self, count: int) -> list:
        """Gets available seats strictly from general_seats (never private pool)."""
        _, _, _, _, _, seats_list = self.client.get_show_full(self.admin_token, self.show_id)
        with self._lock:
            taken_by_client = set(self.confirmed_reservations.values())
        free = [
            s for s, status in seats_list
            if s in self.general_seats and status == "available" and s not in taken_by_client
        ]
        return free[:count]

    def run_s1(self):
        """S1 Hot-seat storm: concurrency barrier releases waves of users targeting hot seats simultaneously."""
        start = time.perf_counter()
        outcomes = []
        users_count = min(len(self.users), self.concurrency)

        for seat in self.hot_seats:
            barrier = threading.Barrier(users_count)

            def task(u_idx, target_seat=seat, b=barrier):
                user = self.users[u_idx % len(self.users)]
                key = str(uuid.uuid4())
                b.wait()
                res = self.client.reserve(user[1], self.show_id, [target_seat], key)
                with self._lock:
                    if res["status_code"] == 201 and res["reservation_id"]:
                        res_id = res["reservation_id"]
                        if res_id not in self.confirmed_reservations:
                            self.confirmed_reservations[res_id] = target_seat
                            self.winning_hot_seats[target_seat] = res_id
                            self.user_held_seats[user[0]].append(target_seat)
                        else:
                            res["is_replay"] = True
                    outcomes.append(res)

            wave_futures = [self.pool.submit(task, i) for i in range(users_count)]
            for f in wave_futures:
                f.result()

        duration = time.perf_counter() - start
        with self._lock:
            self.all_outcomes.extend(outcomes)

        stats = compute_stats(outcomes, duration)
        passed = (
            stats["count_201"] == len(self.hot_seats)
            and stats["count_409_seat_taken"] == len(outcomes) - len(self.hot_seats)
            and len(stats["count_5xx"]) == 0
            and stats["count_timeouts"] == 0
        )
        summary = f"Hot seats: {len(self.hot_seats)} | 201 wins: {stats['count_201']}/{len(self.hot_seats)} | 409 seat-taken: {stats['count_409_seat_taken']}"
        return "S1 Hot-seat storm", stats, passed, summary

    def run_s2_and_s3(self):
        """S2 On-sale mix + S3 Retry storm: full capacity requests with 20% retry storm sharing keys (S1-S4 never touch private_seats)."""
        start = time.perf_counter()
        s2_outcomes = []
        s3_outcomes = []
        rng = random.Random(42)

        s2_requests = []
        for i in range(len(self.users)):
            user = self.users[i]
            if rng.random() < 0.3 and self.hot_seats:
                seat = rng.choice(self.hot_seats)
            else:
                seat = rng.choice(self.general_seats)
            key = str(uuid.uuid4())
            s2_requests.append((user[0], user[1], seat, key))

        futures = []

        for i, req in enumerate(s2_requests):
            u_id, token, seat, key = req

            def task_s2(t=token, s=seat, k=key, uid=u_id):
                res = self.client.reserve(t, self.show_id, [s], k)
                with self._lock:
                    if res["status_code"] == 201 and res["reservation_id"]:
                        res_id = res["reservation_id"]
                        if res_id not in self.confirmed_reservations:
                            self.confirmed_reservations[res_id] = s
                            self.user_held_seats[uid].append(s)
                        else:
                            res["is_replay"] = True
                    s2_outcomes.append(res)

            futures.append(self.pool.submit(task_s2))

            # S3: 20% of requests re-sent 2-3 times with exact same key
            if i % 5 == 0:
                repeats = rng.randint(2, 3)
                for _ in range(repeats):
                    def task_s3(t=token, s=seat, k=key, uid=u_id):
                        res = self.client.reserve(t, self.show_id, [s], k)
                        with self._lock:
                            if res["status_code"] == 201 and res["reservation_id"]:
                                res_id = res["reservation_id"]
                                if res_id not in self.confirmed_reservations:
                                    self.confirmed_reservations[res_id] = s
                                    self.user_held_seats[uid].append(s)
                                else:
                                    res["is_replay"] = True
                            s3_outcomes.append(res)

                    futures.append(self.pool.submit(task_s3))

        for f in futures:
            f.result()

        duration = time.perf_counter() - start
        with self._lock:
            self.all_outcomes.extend(s2_outcomes)
            self.all_outcomes.extend(s3_outcomes)

        s2_stats = compute_stats(s2_outcomes, duration)
        s3_stats = compute_stats(s3_outcomes, duration)

        s2_passed = len(s2_stats["count_5xx"]) == 0 and s2_stats["count_timeouts"] == 0
        s2_summary = f"Requests: {len(s2_outcomes)} | 201: {s2_stats['count_201']} | 409: {s2_stats['count_409_seat_taken'] + s2_stats['count_409_per_user_limit']}"

        s3_passed = len(s3_stats["count_5xx"]) == 0 and s3_stats["count_timeouts"] == 0
        s3_summary = f"Retry requests: {len(s3_outcomes)} | Replays: {s3_stats['count_replay']} | 201s: {s3_stats['count_201']}"

        return (
            ("S2 On-sale mix", s2_stats, s2_passed, s2_summary),
            ("S3 Retry storm", s3_stats, s3_passed, s3_summary),
        )

    def run_s4(self):
        """S4 Key reuse: reusing idempotency key on a different seat produces 409 idempotency-key-reuse."""
        start = time.perf_counter()
        outcomes = []
        free_seats = self._get_available_general_seats(10)
        sample_count = min(3, len(free_seats) // 2)

        for i in range(sample_count):
            u_id = f"user-keyreuse-{i}-{uuid.uuid4().hex[:4]}"
            token = self.client.get_token(u_id)
            seat1 = free_seats[i * 2]
            seat2 = free_seats[i * 2 + 1]
            key = str(uuid.uuid4())

            # Original request on seat1 -> 201
            r1 = self.client.reserve(token, self.show_id, [seat1], key)
            if r1["status_code"] == 201 and r1["reservation_id"]:
                with self._lock:
                    self.confirmed_reservations[r1["reservation_id"]] = seat1
                    self.user_held_seats[u_id].append(seat1)

            # Reusing key on seat2 -> 409 idempotency-key-reuse
            r2 = self.client.reserve(token, self.show_id, [seat2], key)
            outcomes.append(r2)

        duration = time.perf_counter() - start
        with self._lock:
            self.all_outcomes.extend(outcomes)

        stats = compute_stats(outcomes, duration)
        passed = (
            len(outcomes) > 0
            and stats["count_409_key_reuse"] == len(outcomes)
            and len(stats["count_5xx"]) == 0
        )
        summary = f"Key reuse attempts: {len(outcomes)} | 409 key-reuse: {stats['count_409_key_reuse']}"
        return "S4 Key reuse", stats, passed, summary

    def run_s5(self):
        """S5 Per-user limit: fires exactly 10 parallel reserves from private seat pool; max 4 succeed."""
        start = time.perf_counter()
        outcomes = []
        u_id = "user-limit-s5"
        token = self.client.get_token(u_id)
        # S5 uses the first 10 seats of the private pool (never touched by S1-S4)
        s5_seats = self.private_seats[0:10]

        futures = []
        for seat in s5_seats:
            def task(s=seat):
                k = str(uuid.uuid4())
                res = self.client.reserve(token, self.show_id, [s], k)
                with self._lock:
                    if res["status_code"] == 201 and res["reservation_id"]:
                        self.confirmed_reservations[res["reservation_id"]] = s
                        self.user_held_seats[u_id].append(s)
                    outcomes.append(res)

            futures.append(self.pool.submit(task))

        for f in futures:
            f.result()

        duration = time.perf_counter() - start
        with self._lock:
            self.all_outcomes.extend(outcomes)

        stats = compute_stats(outcomes, duration)
        passed = (
            stats["count_201"] <= 4
            and stats["count_409_per_user_limit"] > 0
            and len(stats["count_5xx"]) == 0
        )
        summary = f"Parallel reserves: {len(outcomes)} | 201: {stats['count_201']} (limit 4) | 409 limit: {stats['count_409_per_user_limit']}"
        return "S5 Per-user limit", stats, passed, summary

    def run_s6(self):
        """S6 Identity: token user_id enforced, other user cannot cancel, owner cancel succeeds and is idempotent."""
        start = time.perf_counter()
        outcomes = []
        user_a = ("user-identity-a", self.client.get_token("user-identity-a"))
        user_b = ("user-identity-b", self.client.get_token("user-identity-b"))

        # Uses private seat at index 10
        seat = self.private_seats[10]
        key = str(uuid.uuid4())

        # 1. Spoofed body user_id
        r1 = self.client.reserve(user_a[1], self.show_id, [seat], key, body_user_id="someone-else")
        outcomes.append(r1)

        if r1["status_code"] != 201:
            print(f"{COLOR_RED}[FAIL] S6 setup booking failed loudly! Status: {r1['status_code']}, Error: {r1.get('error_code')}, Reason: {r1.get('error_message')}{COLOR_RESET}")
            raise RuntimeError(f"S6 setup booking failed loudly: status={r1['status_code']}, error={r1.get('error_code')}, reason={r1.get('error_message')}")

        identity_preserved = (r1["status_code"] == 201 and r1.get("user_id") == user_a[0])
        res_id = r1.get("reservation_id")
        if res_id:
            with self._lock:
                self.confirmed_reservations[res_id] = seat
                self.user_held_seats[user_a[0]].append(seat)

        # 2. Other user cancel -> 404
        r2 = self.client.cancel(user_b[1], res_id)
        outcomes.append(r2)
        other_cancel_404 = (r2["status_code"] == 404)

        # 3. Owner cancel -> 200
        r3 = self.client.cancel(user_a[1], res_id)
        outcomes.append(r3)
        owner_cancel_200 = (r3["status_code"] == 200)
        if owner_cancel_200:
            with self._lock:
                self.cancelled_reservations.add(res_id)
                if seat in self.user_held_seats[user_a[0]]:
                    self.user_held_seats[user_a[0]].remove(seat)

        # 4. Repeated cancel -> 200
        r4 = self.client.cancel(user_a[1], res_id)
        outcomes.append(r4)
        repeat_cancel_200 = (r4["status_code"] == 200)

        duration = time.perf_counter() - start
        with self._lock:
            self.all_outcomes.extend(outcomes)

        stats = compute_stats(outcomes, duration)
        passed = identity_preserved and other_cancel_404 and owner_cancel_200 and repeat_cancel_200
        summary = f"Spoof token match: {identity_preserved} | Other cancel 404: {other_cancel_404} | Owner cancel: {owner_cancel_200} | Repeat cancel: {repeat_cancel_200}"
        return "S6 Identity", stats, passed, summary

    def run_s7(self):
        """S7 Cancel and rebook: seat released upon cancellation can immediately be booked by another user."""
        start = time.perf_counter()
        outcomes = []
        user1 = ("user-rebook-1", self.client.get_token("user-rebook-1"))
        user2 = ("user-rebook-2", self.client.get_token("user-rebook-2"))

        # Uses private seat at index 11
        seat = self.private_seats[11]
        k1 = str(uuid.uuid4())

        # 1. User 1 reserves
        r1 = self.client.reserve(user1[1], self.show_id, [seat], k1)
        outcomes.append(r1)

        if r1["status_code"] != 201:
            print(f"{COLOR_RED}[FAIL] S7 setup booking 1 failed loudly! Status: {r1['status_code']}, Error: {r1.get('error_code')}, Reason: {r1.get('error_message')}{COLOR_RESET}")
            raise RuntimeError(f"S7 setup booking failed loudly: status={r1['status_code']}, error={r1.get('error_code')}, reason={r1.get('error_message')}")

        res_id = r1.get("reservation_id")
        if res_id:
            with self._lock:
                self.confirmed_reservations[res_id] = seat
                self.user_held_seats[user1[0]].append(seat)

        # 2. User 1 cancels
        r2 = self.client.cancel(user1[1], res_id)
        outcomes.append(r2)
        if r2["status_code"] == 200:
            with self._lock:
                self.cancelled_reservations.add(res_id)
                if seat in self.user_held_seats[user1[0]]:
                    self.user_held_seats[user1[0]].remove(seat)

        # 3. User 2 rebooks same seat -> 201
        k2 = str(uuid.uuid4())
        r3 = self.client.reserve(user2[1], self.show_id, [seat], k2)
        outcomes.append(r3)

        if r3["status_code"] != 201:
            print(f"{COLOR_RED}[FAIL] S7 rebook booking failed loudly! Status: {r3['status_code']}, Error: {r3.get('error_code')}, Message: {r3.get('error_message')}{COLOR_RESET}")

        if r3["status_code"] == 201 and r3.get("reservation_id"):
            with self._lock:
                self.confirmed_reservations[r3["reservation_id"]] = seat
                self.user_held_seats[user2[0]].append(seat)

        duration = time.perf_counter() - start
        with self._lock:
            self.all_outcomes.extend(outcomes)

        stats = compute_stats(outcomes, duration)
        passed = (r1["status_code"] == 201 and r2["status_code"] == 200 and r3["status_code"] == 201)
        summary = f"Reserve 201: {r1['status_code'] == 201} | Cancel 200: {r2['status_code'] == 200} | Rebook 201: {r3['status_code'] == 201}"
        return "S7 Cancel and rebook", stats, passed, summary


# ==== report
def print_scenario_block(name: str, stats: dict, passed: bool, summary: str):
    tag_color = COLOR_GREEN if passed else COLOR_RED
    tag = "PASS" if passed else "FAIL"

    print()
    print("-" * 80)
    print(f"{tag_color}[{tag}]{COLOR_RESET} {name} ({stats['total_requests']} reqs in {stats['total_duration_s']:.2f}s, {stats['rps']:.0f} rps)")
    print(f"  Details:  {summary}")
    print(
        f"  Outcomes: 201 (distinct)={stats['count_201']}, Replay={stats['count_replay']}, "
        f"409(seat-taken)={stats['count_409_seat_taken']}, "
        f"409(limit)={stats['count_409_per_user_limit']}, "
        f"409(key-reuse)={stats['count_409_key_reuse']}"
    )

    if stats["count_5xx"] or stats["count_timeouts"] > 0:
        errs = ",".join(f"{k}:{v}" for k, v in stats["count_5xx"].items())
        print(f"{COLOR_RED}  Errors:   5xx={errs}, Timeouts={stats['count_timeouts']}{COLOR_RESET}")

    print(
        f"  Latency:  p50={stats['latency_p50']:.1f}ms, "
        f"p95={stats['latency_p95']:.1f}ms, "
        f"p99={stats['latency_p99']:.1f}ms, "
        f"max={stats['latency_max']:.1f}ms"
    )


def print_total_distribution(stats: dict, invariant_samples: int, invariant_violations: int,
                             first_5xx_errors: list = None):
    print()
    print("=" * 80)
    print("                           TOTAL OUTCOME DISTRIBUTION                           ")
    print("=" * 80)
    print(f"Total requests:               {stats['total_requests']}")
    print(f"Total duration:               {stats['total_duration_s']:.2f}s ({stats['rps']:.0f} req/s)")
    print(f"201 Confirmed (Distinct IDs): {stats['count_201']}")
    print(f"201 Idempotent Replays:       {stats['count_replay']}")
    print(f"409 seat-taken:               {stats['count_409_seat_taken']}")
    print(f"409 per-user-limit:           {stats['count_409_per_user_limit']}")
    print(f"409 key-reuse:                {stats['count_409_key_reuse']}")
    if stats["count_409_other"] > 0:
        print(f"409 other:                    {stats['count_409_other']}")

    for sc, count in sorted(stats["other_4xx"].items()):
        print(f"HTTP {sc}:                    {count}")

    if stats["count_5xx"]:
        for sc, count in sorted(stats["count_5xx"].items()):
            print(f"HTTP {sc} (Errors):            {count}")
    else:
        print("5xx Errors:                   0")

    print(f"Timeouts / Connect:           {stats['count_timeouts']}")
    print(
        f"Latency (ms):                 p50={stats['latency_p50']:.1f} | "
        f"p95={stats['latency_p95']:.1f} | "
        f"p99={stats['latency_p99']:.1f} | "
        f"max={stats['latency_max']:.1f}"
    )
    print(f"Invariant samples:            {invariant_samples} sampled, {invariant_violations} violations")

    if first_5xx_errors:
        print("-" * 80)
        print("First 5xx responses (up to 5):")
        for i, err in enumerate(first_5xx_errors[:5], start=1):
            print(f"  [{i}] HTTP {err['status']} | X-Request-Id: {err['request_id']} | Body: {err['body']}")

    print("=" * 80)


# ==== reconciliation
def print_reconciliation_line(label: str, pass_bool: bool, details: str):
    tag_color = COLOR_GREEN if pass_bool else COLOR_RED
    tag = "PASS" if pass_bool else "FAIL"
    print(f"  {tag_color}{tag}{COLOR_RESET} - {label} ({details})")


def run_reconciliation(
    final_show_state: tuple,
    distinct_201s: int,
    active_confirmed: int,
    hot_seats_count: int,
    winning_hot_seats: dict,
    user_held_seats: dict,
    per_user_limit: int,
    metrics_before: dict,
    metrics_after: dict,
    show_id: str,
    total_stats: dict,
) -> bool:
    print()
    print("FINAL RECONCILIATION:")
    all_passed = True

    status, avail, held, conf, total_seats = final_show_state

    # 1. GET /shows/{id}: available + held + confirmed == total_seats
    inv1 = (status == 200 and (avail + held + conf == total_seats))
    print_reconciliation_line(
        "GET /shows/{id}: available + held + confirmed == total_seats",
        inv1,
        f"available={avail}, held={held}, confirmed={conf}, total={total_seats}",
    )
    all_passed = all_passed and inv1

    # 2. confirmed == number of distinct reservation_ids returned with 201 minus cancelled ones
    inv2 = (conf == active_confirmed)
    print_reconciliation_line(
        "confirmed == number of distinct reservation_ids returned with 201 minus cancelled ones",
        inv2,
        f"show.confirmed={conf}, client.activeConfirmed={active_confirmed}",
    )
    all_passed = all_passed and inv2

    # 3. For each hot seat: exactly one winning 201
    inv3 = (len(winning_hot_seats) == hot_seats_count)
    print_reconciliation_line(
        "For each hot seat: exactly one winning 201 (no seat appears in two reservations)",
        inv3,
        f"hotSeats={hot_seats_count}, winningSeats={len(winning_hot_seats)}",
    )
    all_passed = all_passed and inv3

    # 4. No user holds more than per_user_limit seats
    over_limit = [f"{u}:{len(seats)}" for u, seats in user_held_seats.items() if len(seats) > per_user_limit]
    inv4 = (len(over_limit) == 0)
    details4 = f"max held <= {per_user_limit}" if inv4 else f"{len(over_limit)} users exceeded quota: {','.join(over_limit)}"
    print_reconciliation_line(
        f"No user holds more than per_user_limit ({per_user_limit}) seats",
        inv4,
        details4,
    )
    all_passed = all_passed and inv4

    # 5. Scrape GET /metrics: confirmed_total delta equals distinct 201s
    conf_before = metrics_before.get("reservations_confirmed_total", 0.0)
    conf_after = metrics_after.get("reservations_confirmed_total", 0.0)
    conf_delta = int(conf_after - conf_before)
    inv5 = (conf_delta == distinct_201s)
    print_reconciliation_line(
        "Scrape GET /metrics: confirmed_total delta equals distinct 201s",
        inv5,
        f"delta={conf_delta}, distinct201s={distinct_201s}",
    )
    all_passed = all_passed and inv5

    # 6. seats_available{show_id} equals counts.available
    show_metric_key = f'seats_available{{show_id="{show_id}"}}'
    seats_gauge = int(metrics_after.get(show_metric_key, -1))
    inv6 = (seats_gauge == avail)
    print_reconciliation_line(
        "seats_available{show_id} equals counts.available",
        inv6,
        f"gauge={seats_gauge}, counts.available={avail}",
    )
    all_passed = all_passed and inv6

    # 7. 5xx count == 0 and timeouts == 0
    count_5xx_total = sum(total_stats["count_5xx"].values())
    inv7 = (count_5xx_total == 0 and total_stats["count_timeouts"] == 0)
    print_reconciliation_line(
        "5xx count == 0 and timeouts == 0",
        inv7,
        f"5xx={count_5xx_total}, timeouts={total_stats['count_timeouts']}",
    )
    all_passed = all_passed and inv7

    print()
    if all_passed:
        print(f"{COLOR_GREEN}ALL RECONCILIATION CHECKS PASSED!{COLOR_RESET}")
    else:
        print(f"{COLOR_RED}ONE OR MORE RECONCILIATION CHECKS FAILED!{COLOR_RESET}")

    return all_passed


# ==== main
def main():
    args = parse_args()

    print("=" * 80)
    print("                       ONE-COMMAND BURST STAMPEDE TEST                          ")
    print("=" * 80)
    print(f"Base URL:     {args.base_url}")
    print(f"Users:        {args.users}")
    print(f"Hot Seats:    {args.hot_seats}")
    print(f"Total Seats:  {args.total_seats}")
    print(f"Concurrency:  {args.concurrency}")
    print(f"Timeout:      {args.timeout:.0f}s")
    print("=" * 80)

    client = HttpClient(args.base_url, concurrency=args.concurrency, timeout=args.timeout)

    # 1. Scrape initial metrics
    print("[Setup] Scraping initial baseline metrics...")
    metrics_before = client.get_metrics()

    # 2. Provision admin and simulated user tokens (timed separately with rate report)
    print(f"[Setup] Provisioning admin and {args.users} users (batches of 50)...")
    token_start = time.perf_counter()
    admin_token = client.get_token("admin-burst", "admin")

    users = [None] * args.users
    with concurrent.futures.ThreadPoolExecutor(max_workers=min(args.concurrency, 50)) as setup_pool:
        batch_size = 50
        for b_start in range(0, args.users, batch_size):
            b_count = min(batch_size, args.users - b_start)
            futures = []
            for idx in range(b_start, b_start + b_count):
                u_id = f"user-{idx + 1:05d}"
                futures.append((idx, u_id, setup_pool.submit(client.get_token, u_id, "user")))
            for idx, u_id, fut in futures:
                users[idx] = (u_id, fut.result())

    token_duration = time.perf_counter() - token_start
    total_tokens = args.users + 1
    token_rate = (total_tokens / token_duration) if token_duration > 0 else 0
    print(f"[Setup] Tokens provisioned: {total_tokens} tokens in {token_duration:.2f}s ({token_rate:.0f} tokens/s).")

    # 3. Create Show with General & Private seat pools
    # Last 20 seats are reserved for S5-S7; S1-S4 only touch general_seats
    all_seat_labels = [f"A{n}" for n in range(1, args.total_seats + 1)]
    private_seats = all_seat_labels[-20:]
    general_seats = all_seat_labels[:-20]
    hot_seats = general_seats[:args.hot_seats]

    timestamp_str = time.strftime("%Y%m%d%H%M%S")
    rand_suffix = uuid.uuid4().hex[:4]
    show_name = f"Burst-Show-{timestamp_str}-{rand_suffix}"

    print(f"[Setup] Creating show '{show_name}' with {args.total_seats} seats (per_user_limit=4)...")
    show_id = client.create_show(admin_token, show_name, 2500, 4, all_seat_labels)
    print(f"[Setup] Created show ID: {show_id}")

    # 4. Warm connection pool with GET /health/live before S1
    print(f"[Setup] Warming {client.pool_size} worker connections with GET /health/live...")
    warm_start = time.perf_counter()
    client.warm_connections()
    warm_duration = time.perf_counter() - warm_start
    print(f"[Setup] Warmed {client.pool_size} connections in {warm_duration:.2f}s.")

    # 5. Start Background Invariant Poller
    stop_poller = threading.Event()
    invariant_samples = 0
    invariant_violations = 0
    poller_lock = threading.Lock()

    def poller_worker():
        nonlocal invariant_samples, invariant_violations
        while not stop_poller.wait(timeout=1.0):
            try:
                status, avail, held, conf, total = client.get_show_summary(admin_token, show_id)
                if status == 200 and total > 0:
                    with poller_lock:
                        invariant_samples += 1
                        if avail + held + conf != total:
                            invariant_violations += 1
            except Exception:
                pass

    poller_thread = threading.Thread(target=poller_worker, daemon=True)
    poller_thread.start()

    # 6. Run Scenarios S1 -> S7
    print()
    print("Running Scenarios S1 -> S7...")
    total_start = time.perf_counter()

    with concurrent.futures.ThreadPoolExecutor(max_workers=args.concurrency) as pool:
        scenarios = Scenarios(
            client=client,
            pool=pool,
            admin_token=admin_token,
            users=users,
            show_id=show_id,
            general_seats=general_seats,
            hot_seats=hot_seats,
            private_seats=private_seats,
            concurrency=args.concurrency,
        )

        s1_name, s1_stats, s1_passed, s1_summary = scenarios.run_s1()
        print_scenario_block(s1_name, s1_stats, s1_passed, s1_summary)

        (s2_res, s3_res) = scenarios.run_s2_and_s3()
        print_scenario_block(s2_res[0], s2_res[1], s2_res[2], s2_res[3])
        print_scenario_block(s3_res[0], s3_res[1], s3_res[2], s3_res[3])

        s4_name, s4_stats, s4_passed, s4_summary = scenarios.run_s4()
        print_scenario_block(s4_name, s4_stats, s4_passed, s4_summary)

        s5_name, s5_stats, s5_passed, s5_summary = scenarios.run_s5()
        print_scenario_block(s5_name, s5_stats, s5_passed, s5_summary)

        s6_name, s6_stats, s6_passed, s6_summary = scenarios.run_s6()
        print_scenario_block(s6_name, s6_stats, s6_passed, s6_summary)

        s7_name, s7_stats, s7_passed, s7_summary = scenarios.run_s7()
        print_scenario_block(s7_name, s7_stats, s7_passed, s7_summary)

    total_duration = time.perf_counter() - total_start

    # Stop background poller
    stop_poller.set()
    poller_thread.join(timeout=2.0)

    # 7. Total Outcome Distribution
    total_stats = compute_stats(scenarios.all_outcomes, total_duration)
    with poller_lock:
        samples = invariant_samples
        violations = invariant_violations
    print_total_distribution(total_stats, samples, violations, client.first_5xx_errors)

    # 8. Final Reconciliation
    time.sleep(1.0)  # settle background sync / metrics
    metrics_after = client.get_metrics()
    final_show_state = client.get_show_summary(admin_token, show_id)

    distinct_201s = len(scenarios.confirmed_reservations)
    active_confirmed = len(set(scenarios.confirmed_reservations.keys()) - scenarios.cancelled_reservations)

    all_passed = run_reconciliation(
        final_show_state=final_show_state,
        distinct_201s=distinct_201s,
        active_confirmed=active_confirmed,
        hot_seats_count=len(hot_seats),
        winning_hot_seats=scenarios.winning_hot_seats,
        user_held_seats=scenarios.user_held_seats,
        per_user_limit=4,
        metrics_before=metrics_before,
        metrics_after=metrics_after,
        show_id=show_id,
        total_stats=total_stats,
    )

    exit_code = 0 if (all_passed and violations == 0) else 1
    sys.exit(exit_code)


if __name__ == "__main__":
    main()
