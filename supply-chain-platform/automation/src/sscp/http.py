"""HTTP clients for platform APIs, always verifying TLS against the local root CA.

The automation runs on the host and reaches services through their 127.0.0.1 port
mappings; every certificate includes `localhost`, so verification stays on.
"""
from __future__ import annotations

import ssl
import time
from typing import Callable

import httpx

from sscp import pki

ENDPOINTS = {
    "gitea": "https://localhost:3000",
    "harbor": "https://localhost:8443",
    "vault": "https://localhost:8200",
    "keycloak": "https://localhost:9443",
    "minio": "https://localhost:9000",
    "controlplane": "https://localhost:7443",
    "sonarqube": "http://localhost:9100",
}


def tls_context() -> ssl.SSLContext:
    """TLS context that trusts only the platform's local root CA."""
    return ssl.create_default_context(cafile=str(pki.CA_CERT))


def client(service: str, **kwargs) -> httpx.Client:
    return httpx.Client(base_url=ENDPOINTS[service], verify=tls_context(), timeout=30.0, **kwargs)


def wait_for(description: str, probe: Callable[[], bool], timeout: float = 300, interval: float = 3) -> None:
    """Polls `probe` until it returns True; connection errors count as 'not yet'."""
    deadline = time.monotonic() + timeout
    last_error = ""
    while time.monotonic() < deadline:
        try:
            if probe():
                return
        except httpx.HTTPError as error:
            last_error = str(error)
        time.sleep(interval)
    raise TimeoutError(f"{description} not ready after {timeout:.0f}s {last_error}".strip())
