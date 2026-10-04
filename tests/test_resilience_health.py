import os
import time
import uuid
import pytest
import requests
from concurrent.futures import ThreadPoolExecutor, as_completed

BASE_URL = os.environ.get("BASE_URL", "http://localhost:5041")

def get_token(user_id: str, role: str = "user") -> str:
    res = requests.post(f"{BASE_URL}/auth/token", json={"user_id": user_id, "role": role})
    assert res.status_code == 200
    return res.json()["token"]

@pytest.fixture(scope="session")
def admin_token():
    return get_token("admin_resilience", "admin")

@pytest.fixture(scope="session")
def alice_token():
    return get_token("alice_resilience", "user")

@pytest.fixture(scope="session")
def bob_token():
    return get_token("bob_resilience", "user")

def set_fault(count=None, probability=None):
    params = {}
    if count is not None:
        params["count"] = count
    if probability is not None:
        params["probability"] = probability
    try:
        r = requests.post(f"{BASE_URL}/debug/fault-transient", params=params)
        return r.status_code == 200
    except Exception:
        return False

def clear_fault():
    try:
        requests.post(f"{BASE_URL}/debug/fault-transient")
    except Exception:
        pass

def test_01_health_probes_and_error_middleware():
    """Verify live and ready probes, correlation ID header, and 404 envelope."""
    # Live probe (no dependencies)
    res_live = requests.get(f"{BASE_URL}/health/live")
    assert res_live.status_code == 200
    assert res_live.json().get("status") == "live"

    # Ready probe
    res_ready = requests.get(f"{BASE_URL}/health/ready")
    assert res_ready.status_code == 200
    assert res_ready.json().get("status") == "ready"

    # Correlation ID
    test_cid = f"test-trace-{uuid.uuid4().hex[:8]}"
    res_cid = requests.get(f"{BASE_URL}/health/live", headers={"X-Request-Id": test_cid})
    assert res_cid.headers.get("X-Request-Id") == test_cid

    # 404 unknown route
    res_404 = requests.get(f"{BASE_URL}/unknown_route_does_not_exist")
    assert res_404.status_code == 404
    assert res_404.json().get("error") == "not-found"

def test_02_normal_reserve_and_cancel(admin_token, alice_token):
    """Verify normal reservation and cancellation work seamlessly under retry wrapper."""
    show_req = {
        "name": f"ResilienceShow-{uuid.uuid4().hex[:6]}",
        "seats": ["R1", "R2", "R3"],
        "price_paise": 500,
        "per_user_limit": 4
    }
    show_res = requests.post(f"{BASE_URL}/shows", headers={"Authorization": f"Bearer {admin_token}"}, json=show_req)
    assert show_res.status_code == 201
    show_id = show_res.json()["id"]

    # Reserve R1
    key = f"key-r1-{uuid.uuid4().hex[:6]}"
    r_res = requests.post(
        f"{BASE_URL}/shows/{show_id}/reserve",
        headers={"Authorization": f"Bearer {alice_token}"},
        json={"seats": ["R1"], "idempotency_key": key}
    )
    assert r_res.status_code == 201
    res_id = r_res.json()["reservation_id"]

    # Cancel R1
    r_cancel = requests.post(
        f"{BASE_URL}/reservations/{res_id}/cancel",
        headers={"Authorization": f"Bearer {alice_token}"}
    )
    assert r_cancel.status_code == 200
    assert r_cancel.json()["status"] == "cancelled"

def test_03_simulated_transient_failure_recovery(admin_token, alice_token):
    """
    Requirement 2:
    Simulate a transient failure: temporarily inject a fault that throws on the first 2 attempts.
    The request must succeed with 201 after retries, with no 5xx.
    """
    show_req = {
        "name": f"TransientShow-{uuid.uuid4().hex[:6]}",
        "seats": ["T1", "T2"],
        "price_paise": 500,
        "per_user_limit": 4
    }
    show_res = requests.post(f"{BASE_URL}/shows", headers={"Authorization": f"Bearer {admin_token}"}, json=show_req)
    assert show_res.status_code == 201
    show_id = show_res.json()["id"]

    try:
        # Inject transient fault on the next 2 attempts
        if not set_fault(count=2):
            pytest.skip("Fault injection endpoint removed in production build")

        start = time.time()
        key = f"key-transient-{uuid.uuid4().hex[:6]}"
        res = requests.post(
            f"{BASE_URL}/shows/{show_id}/reserve",
            headers={"Authorization": f"Bearer {alice_token}"},
            json={"seats": ["T1"], "idempotency_key": key}
        )
        elapsed = time.time() - start

        # Must succeed with 201 after 2 retries (with backoff ~50ms + ~100ms)
        assert res.status_code == 201, f"Expected 201, got {res.status_code}: {res.text}"
        assert res.json()["status"] == "confirmed"
        assert elapsed >= 0.1, f"Expected retry backoff delay, completed in {elapsed}s"
    finally:
        clear_fault()

def test_04_concurrency_25_parallel_with_random_faults(admin_token):
    """
    Requirement 6:
    25 parallel reserves while the injected fault fires on random attempts (30% chance): zero 5xx.
    """
    seats = [f"F{i+1}" for i in range(25)]
    show_req = {
        "name": f"FaultBurst-{uuid.uuid4().hex[:6]}",
        "seats": seats,
        "price_paise": 300,
        "per_user_limit": 4
    }
    show_res = requests.post(f"{BASE_URL}/shows", headers={"Authorization": f"Bearer {admin_token}"}, json=show_req)
    assert show_res.status_code == 201
    show_id = show_res.json()["id"]

    user_tokens = [get_token(f"user_f_{i}", "user") for i in range(25)]

    try:
        # Injected fault fires with 30% probability on random attempts
        if not set_fault(probability=0.3):
            pytest.skip("Fault injection endpoint removed in production build")

        def book_seat(idx):
            tok = user_tokens[idx]
            seat = seats[idx]
            k = f"k-burst-{idx}-{uuid.uuid4().hex[:6]}"
            resp = requests.post(
                f"{BASE_URL}/shows/{show_id}/reserve",
                headers={"Authorization": f"Bearer {tok}"},
                json={"seats": [seat], "idempotency_key": k}
            )
            return resp.status_code, resp.json()

        with ThreadPoolExecutor(max_workers=25) as executor:
            futures = [executor.submit(book_seat, i) for i in range(25)]
            results = [f.result() for f in as_completed(futures)]

        status_codes = [code for code, _ in results]
        assert 500 not in status_codes, f"500 Internal Server Error encountered: {status_codes}"
        assert 503 not in status_codes, f"503 Service Unavailable encountered: {status_codes}"
        assert all(code == 201 for code in status_codes), f"Expected all 201, got {status_codes}"
    finally:
        clear_fault()

def test_05_in_memory_paths_during_db_outage(admin_token, alice_token, bob_token):
    """
    Requirement 3 & 4:
    During DB outage:
    - taken-filter hits still return 409
    - known keys still replay 201
    - bad validation still returns 400
    - requests requiring DB exhaust retry window and return 503 with Retry-After: 2
    """
    show_req = {
        "name": f"OutageShow-{uuid.uuid4().hex[:6]}",
        "seats": ["M1", "M2"],
        "price_paise": 600,
        "per_user_limit": 4
    }
    show_res = requests.post(f"{BASE_URL}/shows", headers={"Authorization": f"Bearer {admin_token}"}, json=show_req)
    assert show_res.status_code == 201
    show_id = show_res.json()["id"]

    # Alice books M1 while DB is healthy -> cached in KeyCache and TakenFilter
    alice_key = f"key-outage-{uuid.uuid4().hex[:6]}"
    r1 = requests.post(
        f"{BASE_URL}/shows/{show_id}/reserve",
        headers={"Authorization": f"Bearer {alice_token}"},
        json={"seats": ["M1"], "idempotency_key": alice_key}
    )
    assert r1.status_code == 201

    try:
        # Simulate complete DB outage via persistent faults
        if not set_fault(count=999999):
            pytest.skip("Fault injection endpoint removed in production build")

        # 1. Taken-filter hit returns 409 without hitting DB
        r_taken = requests.post(
            f"{BASE_URL}/shows/{show_id}/reserve",
            headers={"Authorization": f"Bearer {bob_token}"},
            json={"seats": ["M1"], "idempotency_key": f"bob-key-{uuid.uuid4().hex[:6]}"}
        )
        assert r_taken.status_code == 409
        assert r_taken.json().get("error") == "seat-taken"

        # 2. Known key replay returns 201 without hitting DB
        r_replay = requests.post(
            f"{BASE_URL}/shows/{show_id}/reserve",
            headers={"Authorization": f"Bearer {alice_token}"},
            json={"seats": ["M1"], "idempotency_key": alice_key}
        )
        assert r_replay.status_code == 201
        assert r_replay.json()["reservation_id"] == r1.json()["reservation_id"]

        # 3. Bad input validation returns 400 without hitting DB
        r_val = requests.post(
            f"{BASE_URL}/shows/{show_id}/reserve",
            headers={"Authorization": f"Bearer {bob_token}"},
            json={"seats": [], "idempotency_key": "some-key"}
        )
        assert r_val.status_code == 400

        # 4. Request for available seat M2 must hit DB -> retries until window -> 503 unavailable
        start = time.time()
        r_db = requests.post(
            f"{BASE_URL}/shows/{show_id}/reserve",
            headers={"Authorization": f"Bearer {bob_token}"},
            json={"seats": ["M2"], "idempotency_key": f"bob-m2-{uuid.uuid4().hex[:6]}"}
        )
        elapsed = time.time() - start

        assert r_db.status_code == 503, f"Expected 503, got {r_db.status_code}: {r_db.text}"
        assert r_db.json().get("error") == "unavailable"
        assert r_db.headers.get("Retry-After") == "2"
        # Verify it retried over the window (~10s)
        assert elapsed >= 9.0, f"Expected at least 9s wait over retry window, took {elapsed}s"
    finally:
        clear_fault()

    # 5. After clearing fault, readiness returns 200 without app restart
    r_ready = requests.get(f"{BASE_URL}/health/ready")
    assert r_ready.status_code == 200
    assert r_ready.json().get("status") == "ready"
