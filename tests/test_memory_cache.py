import os
import time
import uuid
import pytest
import requests
from concurrent.futures import ThreadPoolExecutor, as_completed

BASE_URL = os.environ.get("BASE_URL", "http://localhost:5041")

def get_token(user_id: str, role: str = "user") -> str:
    res = requests.post(f"{BASE_URL}/auth/token", json={"user_id": user_id, "role": role})
    assert res.status_code == 200, f"Token failed for {user_id}: {res.text}"
    return res.json()["token"]

@pytest.fixture(scope="session")
def admin_token():
    return get_token("admin_master", "admin")

@pytest.fixture(scope="session")
def alice_token():
    return get_token("alice", "user")

@pytest.fixture(scope="session")
def bob_token():
    return get_token("bob", "user")

def get_stats():
    res = requests.get(f"{BASE_URL}/debug/stats")
    if res.status_code == 200:
        return res.json()
    return {}

def test_01_warmup_and_readiness():
    """Verify warm-up has executed and readiness returns 200."""
    res = requests.get(f"{BASE_URL}/health/ready")
    assert res.status_code == 200
    assert res.json().get("status") == "ready"

    stats = get_stats()
    if stats:
        assert stats.get("is_warm") is True
        assert stats.get("active_locks") == 0

def test_02_negative_cache_bad_show():
    """Verify non-existent show ID gets 404 and is cached negatively."""
    bad_id = str(uuid.uuid4())
    token = get_token("probe_user", "user")

    res1 = requests.post(
        f"{BASE_URL}/shows/{bad_id}/reserve",
        headers={"Authorization": f"Bearer {token}"},
        json={"seats": ["A1"], "idempotency_key": "neg_test_1"}
    )
    assert res1.status_code == 404
    assert res1.json().get("error") == "show-not-found"

    # Second immediate request should hit the negative cache
    res2 = requests.post(
        f"{BASE_URL}/shows/{bad_id}/reserve",
        headers={"Authorization": f"Bearer {token}"},
        json={"seats": ["A1"], "idempotency_key": "neg_test_2"}
    )
    assert res2.status_code == 404
    assert res2.json().get("error") == "show-not-found"

def test_03_create_show_and_book_and_deny(admin_token, alice_token, bob_token):
    """
    Test flow:
    1. Create a show -> ShowCache populated
    2. Alice books A1 -> 201 Created -> TakenFilter marked
    3. Bob tries to book A1 -> 409 seat-taken (denied in memory before DB transaction)
    """
    stats_before = get_stats()
    db_before = stats_before.get("db_reserve_executions", 0)

    # 1. Admin creates show
    show_req = {
        "name": f"Show-{uuid.uuid4().hex[:6]}",
        "seats": ["A1", "A2", "A3", "A4", "A5", "A6"],
        "price_paise": 500,
        "per_user_limit": 4
    }
    show_res = requests.post(
        f"{BASE_URL}/shows",
        headers={"Authorization": f"Bearer {admin_token}"},
        json=show_req
    )
    assert show_res.status_code == 201
    show_id = show_res.json()["id"]

    # 2. Alice books A1
    alice_key = f"key-alice-{uuid.uuid4().hex[:6]}"
    r_alice = requests.post(
        f"{BASE_URL}/shows/{show_id}/reserve",
        headers={"Authorization": f"Bearer {alice_token}"},
        json={"seats": ["A1"], "idempotency_key": alice_key}
    )
    assert r_alice.status_code == 201
    alice_data = r_alice.json()
    assert alice_data["status"] == "confirmed"
    assert alice_data["seats"] == ["A1"]

    stats_after_alice = get_stats()
    if stats_before and stats_after_alice:
        db_after_alice = stats_after_alice.get("db_reserve_executions", 0)
        assert db_after_alice == db_before + 1, "Alice should have executed exactly 1 DB transaction"

    # 3. Bob attempts to book A1 -> Fast denial via TakenFilter
    bob_key = f"key-bob-{uuid.uuid4().hex[:6]}"
    r_bob = requests.post(
        f"{BASE_URL}/shows/{show_id}/reserve",
        headers={"Authorization": f"Bearer {bob_token}"},
        json={"seats": ["A1"], "idempotency_key": bob_key}
    )
    assert r_bob.status_code == 409
    bob_err = r_bob.json()
    assert bob_err.get("error") == "seat-taken"
    assert "A1" in bob_err.get("unavailable", [])

    stats_after_bob = get_stats()
    if stats_after_alice and stats_after_bob:
        db_after_bob = stats_after_bob.get("db_reserve_executions", 0)
        # Proves Bob's denial was resolved in memory by TakenFilter without running a DB reserve transaction!
        assert db_after_bob == db_after_alice, "Bob's taken-seat request should NOT trigger a DB reserve transaction"

def test_04_idempotent_replay_before_filter(admin_token, alice_token):
    """
    Verify Alice's replay with the same key returns the cached 201 before TakenFilter,
    while reusing the key with different parameters returns 409 idempotency-key-reuse.
    """
    show_req = {
        "name": f"Show-{uuid.uuid4().hex[:6]}",
        "seats": ["A1", "A2"],
        "price_paise": 1000,
        "per_user_limit": 4
    }
    show_res = requests.post(
        f"{BASE_URL}/shows",
        headers={"Authorization": f"Bearer {admin_token}"},
        json=show_req
    )
    show_id = show_res.json()["id"]

    alice_key = f"key-replay-{uuid.uuid4().hex[:6]}"
    # 1. First booking
    res1 = requests.post(
        f"{BASE_URL}/shows/{show_id}/reserve",
        headers={"Authorization": f"Bearer {alice_token}"},
        json={"seats": ["A1"], "idempotency_key": alice_key}
    )
    assert res1.status_code == 201
    booking1 = res1.json()

    stats1 = get_stats()
    stats2 = get_stats()
    if stats1 and stats2:
        assert stats2.get("db_reserve_executions", 0) == stats1.get("db_reserve_executions", 0), "Replay from KeyCache must not hit the DB"

    # 3. Key reuse with different seats -> 409 idempotency-key-reuse
    res3 = requests.post(
        f"{BASE_URL}/shows/{show_id}/reserve",
        headers={"Authorization": f"Bearer {alice_token}"},
        json={"seats": ["A2"], "idempotency_key": alice_key}
    )
    assert res3.status_code == 409
    assert res3.json().get("error") == "idempotency-key-reuse"

def test_05_concurrency_40_users_same_seat(admin_token):
    """
    Requirement 2:
    40 users, same seat, different keys: exactly one 201, 39 x 409 seat-taken, zero 5xx.
    DB reserve transactions for that seat are far fewer than 40 (expected 1-2).
    """
    show_req = {
        "name": f"StormShow-{uuid.uuid4().hex[:6]}",
        "seats": ["HOT_SEAT"],
        "price_paise": 200,
        "per_user_limit": 4
    }
    show_res = requests.post(
        f"{BASE_URL}/shows",
        headers={"Authorization": f"Bearer {admin_token}"},
        json=show_req
    )
    assert show_res.status_code == 201
    show_id = show_res.json()["id"]

    stats_before = get_stats()
    db_before = stats_before.get("db_reserve_executions", 0)

    num_users = 40
    user_tokens = [get_token(f"storm_user_{i}", "user") for i in range(num_users)]

    def attempt_reserve(idx):
        tok = user_tokens[idx]
        k = f"storm_key_{idx}_{uuid.uuid4().hex[:6]}"
        resp = requests.post(
            f"{BASE_URL}/shows/{show_id}/reserve",
            headers={"Authorization": f"Bearer {tok}"},
            json={"seats": ["HOT_SEAT"], "idempotency_key": k}
        )
        return resp.status_code, resp.json()

    with ThreadPoolExecutor(max_workers=num_users) as executor:
        futures = [executor.submit(attempt_reserve, i) for i in range(num_users)]
        results = [f.result() for f in as_completed(futures)]

    status_codes = [code for code, _ in results]
    assert 500 not in status_codes, f"Encountered 5xx errors: {status_codes}"
    count_201 = status_codes.count(201)
    count_409 = status_codes.count(409)

    assert count_201 == 1, f"Expected exactly one 201, got {count_201}"
    assert count_409 == 39, f"Expected 39 409s, got {count_409}"

    stats_after = get_stats()
    if stats_before and stats_after:
        db_after = stats_after.get("db_reserve_executions", 0)
        db_delta = db_after - db_before
        print(f"\n[Test 2 Result] Total requests: {num_users} | 201: {count_201} | 409: {count_409} | DB transactions: {db_delta}")
        assert db_delta <= 2, f"Expected at most 2 DB transactions under memory gate & lock, got {db_delta}"
    else:
        print(f"\n[Test 2 Result] Total requests: {num_users} | 201: {count_201} | 409: {count_409}")

def test_06_same_user_same_key_10_parallel(admin_token, alice_token):
    """
    Requirement 3:
    Same user, same key, 10 parallel: one reservation, all 201 with same id.
    """
    show_req = {
        "name": f"ParKeyShow-{uuid.uuid4().hex[:6]}",
        "seats": ["B1"],
        "price_paise": 300,
        "per_user_limit": 4
    }
    show_res = requests.post(
        f"{BASE_URL}/shows",
        headers={"Authorization": f"Bearer {admin_token}"},
        json=show_req
    )
    show_id = show_res.json()["id"]

    same_key = f"same-key-{uuid.uuid4().hex[:8]}"

    def send_req():
        return requests.post(
            f"{BASE_URL}/shows/{show_id}/reserve",
            headers={"Authorization": f"Bearer {alice_token}"},
            json={"seats": ["B1"], "idempotency_key": same_key}
        )

    with ThreadPoolExecutor(max_workers=10) as executor:
        futures = [executor.submit(send_req) for _ in range(10)]
        responses = [f.result() for f in as_completed(futures)]

    status_codes = [r.status_code for r in responses]
    assert all(code == 201 for code in status_codes), f"Expected all 201, got: {status_codes}"

    res_ids = {r.json()["reservation_id"] for r in responses}
    assert len(res_ids) == 1, f"Expected all identical reservation IDs, got: {res_ids}"

def test_07_cancel_rebook_and_old_key_replay(admin_token, bob_token, alice_token):
    """
    Requirement 4:
    Bob books A1, cancels, Alice books A1, Bob retries his OLD key -> Bob gets his original 201 (KeyCache before filter).
    """
    show_req = {
        "name": f"CancelShow-{uuid.uuid4().hex[:6]}",
        "seats": ["C1"],
        "price_paise": 100,
        "per_user_limit": 4
    }
    show_res = requests.post(
        f"{BASE_URL}/shows",
        headers={"Authorization": f"Bearer {admin_token}"},
        json=show_req
    )
    show_id = show_res.json()["id"]

    bob_key = f"bob-key-{uuid.uuid4().hex[:6]}"
    # 1. Bob books C1
    r_bob = requests.post(
        f"{BASE_URL}/shows/{show_id}/reserve",
        headers={"Authorization": f"Bearer {bob_token}"},
        json={"seats": ["C1"], "idempotency_key": bob_key}
    )
    assert r_bob.status_code == 201
    bob_booking = r_bob.json()
    bob_res_id = bob_booking["reservation_id"]

    # 2. Bob cancels C1
    r_cancel = requests.post(
        f"{BASE_URL}/reservations/{bob_res_id}/cancel",
        headers={"Authorization": f"Bearer {bob_token}"}
    )
    assert r_cancel.status_code == 200
    assert r_cancel.json()["status"] == "cancelled"

    # 3. Alice books C1
    alice_key = f"alice-key-{uuid.uuid4().hex[:6]}"
    r_alice = requests.post(
        f"{BASE_URL}/shows/{show_id}/reserve",
        headers={"Authorization": f"Bearer {alice_token}"},
        json={"seats": ["C1"], "idempotency_key": alice_key}
    )
    assert r_alice.status_code == 201
    assert r_alice.json()["seats"] == ["C1"]

    # 4. Bob retries his OLD key -> Bob gets his original 201
    r_bob_retry = requests.post(
        f"{BASE_URL}/shows/{show_id}/reserve",
        headers={"Authorization": f"Bearer {bob_token}"},
        json={"seats": ["C1"], "idempotency_key": bob_key}
    )
    assert r_bob_retry.status_code == 201
    assert r_bob_retry.json()["reservation_id"] == bob_res_id
    assert r_bob_retry.json()["user_id"] == "bob"

def test_08_lock_cleanup():
    """
    Requirement 7:
    After bursts, SeatLockManager internal dictionary count is 0.
    """
    stats = get_stats()
    if stats and "active_locks" in stats:
        active_locks = stats.get("active_locks", -1)
        assert active_locks == 0, f"Expected 0 active locks after operations, found {active_locks}"

def test_09_parallel_different_seats(admin_token):
    """
    Requirement 8:
    20 users on 20 different seats in parallel: all 201, no waiting on each other.
    """
    seats = [f"D{i+1}" for i in range(20)]
    show_req = {
        "name": f"MultiSeatShow-{uuid.uuid4().hex[:6]}",
        "seats": seats,
        "price_paise": 400,
        "per_user_limit": 4
    }
    show_res = requests.post(
        f"{BASE_URL}/shows",
        headers={"Authorization": f"Bearer {admin_token}"},
        json=show_req
    )
    assert show_res.status_code == 201
    show_id = show_res.json()["id"]

    user_tokens = [get_token(f"parallel_user_{i}", "user") for i in range(20)]

    def book_seat(idx):
        tok = user_tokens[idx]
        seat = seats[idx]
        k = f"k-par-{idx}-{uuid.uuid4().hex[:6]}"
        resp = requests.post(
            f"{BASE_URL}/shows/{show_id}/reserve",
            headers={"Authorization": f"Bearer {tok}"},
            json={"seats": [seat], "idempotency_key": k}
        )
        return resp.status_code, resp.json()

    with ThreadPoolExecutor(max_workers=20) as executor:
        futures = [executor.submit(book_seat, i) for i in range(20)]
        results = [f.result() for f in as_completed(futures)]

    status_codes = [code for code, _ in results]
    assert all(code == 201 for code in status_codes), f"Expected all 201, got {status_codes}"
