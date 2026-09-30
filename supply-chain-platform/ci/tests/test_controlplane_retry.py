"""Parallel jobs of one build can update the same artifact at once. The Control Plane then
refuses the later request with `409 concurrency.conflict` and stores nothing, so the helper
sends it again instead of failing the job. Other refusals must reach the caller unchanged."""
import json

import pytest

from sscp_ci import controlplane, http


def _response(status: int, body: dict | None = None) -> http.Response:
    return http.Response(status, json.dumps(body).encode() if body is not None else b"")


@pytest.fixture
def control_plane(monkeypatch):
    """Answers requests from a prepared list and records them."""
    answers: list[http.Response] = []
    calls: list[tuple[int, ...]] = []

    def request(method, url, *, ok=(), **kwargs):
        calls.append(ok)
        response = answers.pop(0)
        if response.status >= 400 and response.status not in ok:
            raise http.HttpError(method, url, response)
        return response

    monkeypatch.setenv("SSCP_CONTROLPLANE_URL", "https://controlplane.test")
    monkeypatch.setattr(controlplane.zone, "controlplane_token", lambda: "token")
    monkeypatch.setattr(controlplane.http, "request", request)
    monkeypatch.setattr(controlplane.time, "sleep", lambda seconds: None)
    return answers, calls


CONFLICT = {"code": "concurrency.conflict", "message": "The record was changed by another request; retry."}


def test_a_concurrency_conflict_is_retried_until_the_request_succeeds(control_plane):
    answers, calls = control_plane
    answers += [_response(409, CONFLICT), _response(409, CONFLICT), _response(201, {"id": "e1"})]

    response = controlplane.request("POST", "/api/evidence", body=b"report")

    assert response.status == 201
    assert len(calls) == 3


def test_a_conflict_that_persists_fails_after_the_last_attempt(control_plane):
    answers, calls = control_plane
    answers += [_response(409, CONFLICT)] * controlplane.CONFLICT_ATTEMPTS

    with pytest.raises(http.HttpError) as error:
        controlplane.request("POST", "/api/evidence", body=b"report")

    assert error.value.response.status == 409
    assert len(calls) == controlplane.CONFLICT_ATTEMPTS


def test_other_conflicts_are_not_retried_and_reach_a_caller_that_accepts_them(control_plane):
    answers, calls = control_plane
    answers.append(_response(409, {"code": "evidence.run-mismatch"}))

    response = controlplane.request("POST", "/api/evidence", body=b"report", ok=(400, 409))

    assert response.status == 409
    assert len(calls) == 1


def test_other_errors_keep_raising_when_the_caller_does_not_accept_them(control_plane):
    answers, _ = control_plane
    answers.append(_response(403, {"code": "zone.forbidden"}))

    with pytest.raises(http.HttpError):
        controlplane.request("GET", "/api/builds/b1")
