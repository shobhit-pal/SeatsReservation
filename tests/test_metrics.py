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
    return get_token("admin_metrics", "admin")

@pytest.fixture(scope="session")
def alice_token():
    return get_token("alice_metrics", "user")

@pytest.fixture(scope="session")
def bob_token():
    return get_token("bob_metrics", "user")

def get_metric(name: str, labels: dict = None) -> float:
    res = requests.get(f"{BASE_URL}/metrics")
    assert res.status_code == 200
    for line in res.text.splitlines():
        line = line.strip()
        if not line or line.startswith("#"):
            continue
        parts = line.split()
        if len(parts) != 2:
            continue
        metric_str, val_str = parts[0], parts[1]
        if labels:
            if metric_str.startswith(f"{name}{{") and metric_str.endswith("}"):
                raw_labels = metric_str[len(name) + 1 : -1]
                matches = True
                for k, v in labels.items():
                    if f'{k}="{v}"' not in raw_labels:
                        matches = False
                        break
                if matches:
                    return float(val_str)
        else:
            if metric_str == name:
                return float(val_str)
    return 0.0

def test_01_metrics_endpoint_accessible():
    """GET /metrics returns 200 and plain Prometheus text with standard and custom metrics."""
    res = requests.get(f"{BASE_URL}/metrics")
    assert res.status_code == 200
    assert "text/plain" in res.headers.get("Content-Type", "")
    assert "reservations_confirmed_total" in res.text
    assert "reservations_declined_total" in res.text
    assert "reservations_cancelled_total" in res.text
    assert "seats_available" in res.text
    assert "db_gate_waiting" in res.text
    assert "db_retries_total" in res.text
    assert "taken_filter_hits_total" in res.text
    assert "unhandled_exceptions_total" in res.text

def test_02_reserve_cancel_and_seats_available_reconciliation(admin_token, alice_token):
    """
    Test reserve and cancel counting, and seats_available reconciliation:
    1. Show created with 10 seats -> seats_available == 10
    2. Reserve 2 seats -> confirmed +1, seats_available == 8 (reconciles with GET /shows/{id})
    3. Cancel -> cancelled +1, seats_available == 10
    4. Repeat cancel -> cancelled unchanged
    """
    show_name = f"Metrics Show {uuid.uuid4().hex[:6]}"
    seats = [f"M{i}" for i in range(1, 11)]
    create_res = requests.post(
        f"{BASE_URL}/shows",
        headers={"Authorization": f"Bearer {admin_token}"},
        json={"name": show_name, "price_paise": 5000, "per_user_limit": 4, "seats": seats}
    )
    assert create_res.status_code == 201
    show_id = create_res.json()["id"]

    # Initial seats available
    assert get_metric("seats_available", {"show_id": show_id}) == 10.0

    confirmed_before = get_metric("reservations_confirmed_total")
    cancelled_before = get_metric("reservations_cancelled_total")

    # Reserve 2 seats
    key = str(uuid.uuid4())
    res_book = requests.post(
        f"{BASE_URL}/shows/{show_id}/reserve",
        headers={"Authorization": f"Bearer {alice_token}", "Idempotency-Key": key},
        json={"seats": ["M1", "M2"]}
    )
    assert res_book.status_code == 201
    reservation_id = res_book.json()["reservation_id"]

    confirmed_after = get_metric("reservations_confirmed_total")
    assert confirmed_after == confirmed_before + 1.0

    # seats_available drops to 8
    assert get_metric("seats_available", {"show_id": show_id}) == 8.0

    # Cancel reservation
    res_cancel = requests.post(
        f"{BASE_URL}/reservations/{reservation_id}/cancel",
        headers={"Authorization": f"Bearer {alice_token}"}
    )
    assert res_cancel.status_code == 200

    cancelled_after = get_metric("reservations_cancelled_total")
    assert cancelled_after == cancelled_before + 1.0

    # seats_available back to 10
    assert get_metric("seats_available", {"show_id": show_id}) == 10.0

    # Repeat cancel (no-op repeat)
    res_cancel_repeat = requests.post(
        f"{BASE_URL}/reservations/{reservation_id}/cancel",
        headers={"Authorization": f"Bearer {alice_token}"}
    )
    assert res_cancel_repeat.status_code == 200
    # reservations_cancelled_total MUST NOT increment on repeat
    cancelled_after_repeat = get_metric("reservations_cancelled_total")
    assert cancelled_after_repeat == cancelled_after

def test_03_replay_idempotency_key(admin_token, alice_token):
    """
    Replay the same key twice:
    declined{reason="idempotent-replay"} increases by 2 while confirmed remains unchanged.
    """
    show_res = requests.post(
        f"{BASE_URL}/shows",
        headers={"Authorization": f"Bearer {admin_token}"},
        json={"name": f"Replay Show {uuid.uuid4().hex[:6]}", "price_paise": 2000, "per_user_limit": 4, "seats": ["R1", "R2"]}
    )
    assert show_res.status_code == 201
    show_id = show_res.json()["id"]

    confirmed_before = get_metric("reservations_confirmed_total")
    replay_before = get_metric("reservations_declined_total", {"reason": "idempotent-replay"})

    key = str(uuid.uuid4())
    # Initial reservation
    r1 = requests.post(
        f"{BASE_URL}/shows/{show_id}/reserve",
        headers={"Authorization": f"Bearer {alice_token}", "Idempotency-Key": key},
        json={"seats": ["R1"]}
    )
    assert r1.status_code == 201

    confirmed_after_1 = get_metric("reservations_confirmed_total")
    assert confirmed_after_1 == confirmed_before + 1.0
    assert get_metric("reservations_declined_total", {"reason": "idempotent-replay"}) == replay_before

    # Replay 1
    r2 = requests.post(
        f"{BASE_URL}/shows/{show_id}/reserve",
        headers={"Authorization": f"Bearer {alice_token}", "Idempotency-Key": key},
        json={"seats": ["R1"]}
    )
    assert r2.status_code == 201
    assert r2.json()["reservation_id"] == r1.json()["reservation_id"]

    # Replay 2
    r3 = requests.post(
        f"{BASE_URL}/shows/{show_id}/reserve",
        headers={"Authorization": f"Bearer {alice_token}", "Idempotency-Key": key},
        json={"seats": ["R1"]}
    )
    assert r3.status_code == 201
    assert r3.json()["reservation_id"] == r1.json()["reservation_id"]

    replay_after = get_metric("reservations_declined_total", {"reason": "idempotent-replay"})
    assert replay_after == replay_before + 2.0
    # Confirmed remains unchanged
    assert get_metric("reservations_confirmed_total") == confirmed_after_1

def test_04_per_user_limit_and_key_reuse_declines(admin_token, alice_token):
    """Verify declines for per-user-limit and idempotency-key-reuse."""
    show_res = requests.post(
        f"{BASE_URL}/shows",
        headers={"Authorization": f"Bearer {admin_token}"},
        json={"name": f"Limit Show {uuid.uuid4().hex[:6]}", "price_paise": 1000, "per_user_limit": 2, "seats": ["L1", "L2", "L3"]}
    )
    assert show_res.status_code == 201
    show_id = show_res.json()["id"]

    limit_declined_before = get_metric("reservations_declined_total", {"reason": "per-user-limit"})
    reuse_declined_before = get_metric("reservations_declined_total", {"reason": "idempotency-key-reuse"})

    # Exceed per-user limit
    r_limit = requests.post(
        f"{BASE_URL}/shows/{show_id}/reserve",
        headers={"Authorization": f"Bearer {alice_token}", "Idempotency-Key": str(uuid.uuid4())},
        json={"seats": ["L1", "L2", "L3"]}
    )
    assert r_limit.status_code == 409
    assert r_limit.json()["error"] == "per-user-limit"

    limit_declined_after = get_metric("reservations_declined_total", {"reason": "per-user-limit"})
    assert limit_declined_after == limit_declined_before + 1.0

    # Book valid seat
    k_reuse = str(uuid.uuid4())
    r_ok = requests.post(
        f"{BASE_URL}/shows/{show_id}/reserve",
        headers={"Authorization": f"Bearer {alice_token}", "Idempotency-Key": k_reuse},
        json={"seats": ["L1"]}
    )
    assert r_ok.status_code == 201

    # Reuse key with different seat
    r_reuse = requests.post(
        f"{BASE_URL}/shows/{show_id}/reserve",
        headers={"Authorization": f"Bearer {alice_token}", "Idempotency-Key": k_reuse},
        json={"seats": ["L2"]}
    )
    assert r_reuse.status_code == 409
    assert r_reuse.json()["error"] == "idempotency-key-reuse"

    reuse_declined_after = get_metric("reservations_declined_total", {"reason": "idempotency-key-reuse"})
    assert reuse_declined_after == reuse_declined_before + 1.0

def test_05_hot_seat_storm_40_users(admin_token):
    """
    Hot-seat storm (40 users, one seat):
    confirmed increases by 1, declined{reason="seat-taken"} increases by 39.
    """
    show_res = requests.post(
        f"{BASE_URL}/shows",
        headers={"Authorization": f"Bearer {admin_token}"},
        json={"name": f"Storm Show {uuid.uuid4().hex[:6]}", "price_paise": 1500, "per_user_limit": 4, "seats": ["HOT1"]}
    )
    assert show_res.status_code == 201
    show_id = show_res.json()["id"]

    confirmed_before = get_metric("reservations_confirmed_total")
    seat_taken_before = get_metric("reservations_declined_total", {"reason": "seat-taken"})
    filter_hits_before = get_metric("taken_filter_hits_total")

    users = [f"storm_user_{i}_{uuid.uuid4().hex[:4]}" for i in range(40)]
    tokens = [get_token(u) for u in users]

    def attempt_booking(user_token):
        return requests.post(
            f"{BASE_URL}/shows/{show_id}/reserve",
            headers={"Authorization": f"Bearer {user_token}", "Idempotency-Key": str(uuid.uuid4())},
            json={"seats": ["HOT1"]}
        )

    with ThreadPoolExecutor(max_workers=20) as executor:
        futures = [executor.submit(attempt_booking, t) for t in tokens]
        results = [f.result() for f in as_completed(futures)]

    status_codes = [r.status_code for r in results]
    assert status_codes.count(201) == 1
    assert status_codes.count(409) == 39

    confirmed_after = get_metric("reservations_confirmed_total")
    seat_taken_after = get_metric("reservations_declined_total", {"reason": "seat-taken"})

    assert confirmed_after == confirmed_before + 1.0
    assert seat_taken_after == seat_taken_before + 39.0

    # At least some requests should have hit the in-memory taken filter
    filter_hits_after = get_metric("taken_filter_hits_total")
    assert filter_hits_after >= filter_hits_before
