"""A job command must keep a valid Control Plane token for as long as it runs: Keycloak
issues five-minute tokens, and building every image can take longer than that."""
import pytest

from sscp_ci import zone


class _Response:
    def __init__(self, body: dict):
        self._body = body

    def json(self) -> dict:
        return self._body


@pytest.fixture
def keycloak(monkeypatch):
    """Counts token requests and lets the test move the clock."""
    issued = []
    clock = {"now": 1000.0}

    def request(method, url, **kwargs):
        issued.append(url)
        return _Response({"access_token": f"token-{len(issued)}", "expires_in": 300})

    monkeypatch.setenv("SSCP_KEYCLOAK_TOKEN_URL", "https://keycloak.test/token")
    monkeypatch.setattr(zone.http, "request", request)
    monkeypatch.setattr(zone.time, "monotonic", lambda: clock["now"])
    monkeypatch.setattr(zone, "_controlplane_client", lambda: {"client_id": "sscp-build", "client_secret": "s"})
    monkeypatch.setattr(zone, "_token", None)
    return issued, clock


def test_the_token_is_reused_while_it_is_valid(keycloak):
    issued, clock = keycloak

    first = zone.controlplane_token()
    clock["now"] += 200
    second = zone.controlplane_token()

    assert first == second == "token-1"
    assert len(issued) == 1


def test_a_new_token_is_fetched_before_the_old_one_expires(keycloak):
    issued, clock = keycloak

    zone.controlplane_token()
    clock["now"] += 280  # 20 seconds before expiry: too close to start a request with it
    renewed = zone.controlplane_token()

    assert renewed == "token-2"
    assert len(issued) == 2
