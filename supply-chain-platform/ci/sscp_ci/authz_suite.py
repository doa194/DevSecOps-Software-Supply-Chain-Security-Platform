"""Deterministic authorization and API-security tests for the commerce workload.

The platform security team owns these checks (not the application team): they run in the
security zone against the candidate images of a main build, through the gateway, with
real tokens from the Keycloak `commerce` realm. Each check states one rule of the
workload's access model. The result is the `SecurityTests` evidence; any failed check
makes the trust decision `FAIL`.
"""
from __future__ import annotations

import base64
import json
import time
import uuid
from dataclasses import dataclass, field
from typing import Callable

from sscp_ci import http

KEYCLOAK_TOKEN_URL = "https://keycloak.sscp.test:9443/realms/commerce/protocol/openid-connect/token"
PERSONAS = ["carol", "dave", "sam", "cathy", "ivan", "olga", "fiona", "aldo", "ada"]


@dataclass
class Result:
    name: str
    passed: bool
    detail: str = ""


@dataclass
class Suite:
    gateway: str
    passwords: dict[str, str]
    results: list[Result] = field(default_factory=list)
    tokens: dict[str, str] = field(default_factory=dict)
    state: dict[str, object] = field(default_factory=dict)

    # ------------------------------------------------------------ plumbing

    def token(self, persona: str) -> str:
        if persona not in self.tokens:
            response = http.request("POST", KEYCLOAK_TOKEN_URL, form={
                "grant_type": "password", "client_id": "commerce-cli", "username": persona,
                "password": self.passwords[persona], "scope": "openid"})
            self.tokens[persona] = response.json()["access_token"]
        return self.tokens[persona]

    def call(self, method: str, path: str, persona: str | None = None, *, token: str | None = None, body=None,
             headers: dict[str, str] | None = None, raw: bytes | None = None) -> http.Response:
        all_headers = dict(headers or {})
        bearer = token or (self.token(persona) if persona else None)
        if bearer:
            all_headers["Authorization"] = f"Bearer {bearer}"
        if raw is not None:
            return http.request(method, f"{self.gateway}{path}", headers=all_headers, body=raw, ok=tuple(range(400, 600)))
        return http.request(method, f"{self.gateway}{path}", headers=all_headers, json_body=body, ok=tuple(range(400, 600)))

    def check(self, name: str, test: Callable[[], tuple[bool, str]]) -> None:
        try:
            passed, detail = test()
        except Exception as error:  # a check that cannot run counts as failed, never as passed
            passed, detail = False, f"{type(error).__name__}: {error}"[:500]
        self.results.append(Result(name, passed, "" if passed else detail))
        print(f"{'PASS' if passed else 'FAIL'} {name}" + ("" if passed else f"  [{detail[:300]}]"), flush=True)

    @staticmethod
    def poll(fetch: Callable[[], object], done: Callable[[object], bool], timeout: float = 90) -> object:
        deadline = time.monotonic() + timeout
        value = fetch()
        while not done(value) and time.monotonic() < deadline:
            time.sleep(2)
            value = fetch()
        return value

    def status(self, expected: int, response: http.Response) -> tuple[bool, str]:
        return response.status == expected, f"expected {expected}, got {response.status}: {response.body[:200]!r}"

    # ------------------------------------------------------------ scenario

    def setup(self) -> None:
        """Creates the data the checks need: a product with stock, two customers, one order."""
        # SKUs look like ABC-1234; a random number keeps runs against a reused database apart.
        product = self.call("POST", "/api/catalog/products", "cathy", body={
            "sku": f"SEC-{uuid.uuid4().int % 9000 + 1000}", "name": "Security test item", "description": "", "category": "test", "price": 40.00, "currency": "EUR"})
        if product.status != 201:
            raise RuntimeError(f"cannot create the test product: {product.status} {product.body[:200]!r}")
        self.state["product"] = product.json()
        self.call("POST", f"/api/catalog/products/{product.json()['id']}/activate", "cathy")
        sku = product.json()["sku"]
        stocked = self.poll(lambda: self.call("POST", f"/api/inventory/{sku}/receipts", "ivan", body={"quantity": 20, "reference": "SEC"}),
                            lambda r: r.status == 200, 60)
        if stocked.status != 200:
            raise RuntimeError(f"cannot stock the test product: {stocked.status}")
        for persona in ("carol", "dave"):
            self.call("POST", "/api/customers/me/", persona, body={"displayName": persona.title(), "phone": "+49 1234"})
        self.state["dave"] = self.call("GET", "/api/customers/me/", "dave").json()
        placed = self.call("POST", "/api/orders", "carol", body={"lines": [{"sku": sku, "quantity": 1}], "paymentMethodToken": "card_approved_4242"},
                           headers={"Idempotency-Key": str(uuid.uuid4())})
        if placed.status != 201:
            raise RuntimeError(f"cannot place the test order: {placed.status} {placed.body[:200]!r}")
        self.state["order"] = placed.json()
        self.poll(lambda: self.call("GET", f"/api/orders/{placed.json()['id']}", "carol").json(),
                  lambda o: o.get("status") in ("Confirmed", "Rejected", "Cancelled"))

    def run(self) -> None:
        self.setup()
        order = self.state["order"]
        product = self.state["product"]
        sku = product["sku"]

        self.check("anonymous users can browse the catalogue", lambda: self.status(200, self.call("GET", "/api/catalog/products")))
        self.check("anonymous users cannot list orders", lambda: self.status(401, self.call("GET", "/api/orders/mine")))
        self.check("a token with a forged signature is rejected", lambda: self.status(401, self.call("GET", "/api/orders/mine", token=_forged(self.token("carol")))))
        self.check("an unsigned token (alg none) is rejected", lambda: self.status(401, self.call("GET", "/api/orders/mine", token=_unsigned(self.token("carol")))))
        self.check("customers cannot create products", lambda: self.status(403, self.call("POST", "/api/catalog/products", "carol", body={
            "sku": "HAX-1", "name": "x", "description": "", "category": "c", "price": 1, "currency": "EUR"})))
        self.check("inventory clerks cannot change prices", lambda: self.status(403, self.call(
            "PUT", f"/api/catalog/products/{product['id']}/price", "ivan", body={"price": 0.01, "currency": "EUR"})))
        self.check("the server prices orders; client-supplied prices are ignored", lambda: self._server_pricing(sku))
        self.check("placing an order requires an idempotency key", lambda: self.status(400, self.call(
            "POST", "/api/orders", "carol", body={"lines": [{"sku": sku, "quantity": 1}], "paymentMethodToken": "card_approved_4242"})))
        self.check("customers cannot read another customer's order", lambda: self.status(404, self.call("GET", f"/api/orders/{order['id']}", "dave")))
        self.check("customers cannot list all orders", lambda: self.status(403, self.call("GET", "/api/orders", "carol")))
        self.check("order managers can read any order", lambda: self.status(200, self.call("GET", f"/api/orders/{order['id']}", "olga")))
        self.check("customers cannot look up other customers", lambda: self.status(403, self.call("GET", f"/api/customers/{self.state['dave']['id']}", "carol")))
        self.check("support agents see personal data masked", self._masked_for_support)
        self.check("customers cannot download another customer's invoice", self._foreign_invoice)
        self.check("customers cannot read payments", lambda: self.status(403, self.call("GET", f"/api/payments/orders/{order['id']}", "carol")))
        self.check("administrators cannot issue refunds (separation of duties)", self._admin_refund)
        self.check("customers cannot read reports", lambda: self.status(403, self.call("GET", "/api/reports/sales/daily", "carol")))
        self.check("customers cannot read the audit trail", lambda: self.status(403, self.call("GET", "/api/audit/entries", "carol")))
        self.check("auditors can verify the audit chain", self._audit_intact)
        self.check("customers cannot toggle feature flags", lambda: self.status(403, self.call(
            "PUT", "/api/admin/features/Payments.PartialRefunds", "carol", body={"enabled": True, "reason": "security test"})))
        self.check("administrators cannot change their own roles", self._self_role_change)
        self.check("uploads whose content does not match their type are rejected", self._disguised_upload)
        self.check("oversized request bodies are rejected at the gateway", lambda: self.status(413, self.call(
            "POST", "/api/orders", "carol", raw=b'{"lines":[' + b" " * (2 * 1024 * 1024) + b"]}",
            headers={"Content-Type": "application/json", "Idempotency-Key": str(uuid.uuid4())})))
        self.check("responses carry security headers", self._security_headers)
        # Last: it deliberately exhausts the anonymous rate limit for a minute.
        self.check("anonymous clients are rate limited", self._rate_limited)

    # ------------------------------------------------------------ individual checks

    def _server_pricing(self, sku: str) -> tuple[bool, str]:
        response = self.call("POST", "/api/orders", "carol", body={
            "lines": [{"sku": sku, "quantity": 1, "unitPrice": 0.01}], "paymentMethodToken": "card_approved_4242"},
            headers={"Idempotency-Key": str(uuid.uuid4())})
        if response.status != 201:
            return False, f"order not accepted: {response.status}"
        total = response.json()["total"]
        return abs(total - 40.00) < 0.001, f"total {total}, catalogue price 40.00"

    def _masked_for_support(self) -> tuple[bool, str]:
        email = self.call("GET", f"/api/customers/{self.state['dave']['id']}", "sam").json()["email"]
        return "@" not in email and email.endswith("***"), f"support agent saw {email!r}"

    def _foreign_invoice(self) -> tuple[bool, str]:
        documents = self.poll(lambda: self.call("GET", "/api/documents/mine", "carol").json(),
                              lambda d: any(x["kind"] == "Invoice" and x["status"] == "Available" for x in d))
        invoice = next((x for x in documents if x["kind"] == "Invoice"), None)
        if invoice is None:
            return False, "no invoice was generated for the test order"
        return self.status(404, self.call("GET", f"/api/documents/{invoice['id']}/content", "dave"))

    def _admin_refund(self) -> tuple[bool, str]:
        payment = self.call("GET", f"/api/payments/orders/{self.state['order']['id']}", "fiona")
        if payment.status != 200:
            return False, f"finance could not read the payment: {payment.status}"
        return self.status(403, self.call("POST", f"/api/payments/{payment.json()['id']}/refunds", "ada", body={"amount": 1, "reason": "security test"}))

    def _audit_intact(self) -> tuple[bool, str]:
        verification = self.call("GET", "/api/audit/verify", "aldo")
        return verification.status == 200 and verification.json().get("intact") is True, f"{verification.status} {verification.body[:200]!r}"

    def _self_role_change(self) -> tuple[bool, str]:
        me = self.call("GET", "/api/identity/me", "ada").json()
        return self.status(403, self.call("PUT", f"/api/identity/users/{me['subject']}/roles", "ada", body={"roles": ["admin", "finance"]}))

    def _disguised_upload(self) -> tuple[bool, str]:
        body, content_type = http.multipart({}, {"file": ("statement.pdf", b"MZ\x90\x00" + b"\x00" * 200, "application/pdf")})
        response = self.call("POST", "/api/documents/", "carol", raw=body, headers={"Content-Type": content_type})
        return response.status in (400, 415, 422), f"expected a refusal, got {response.status}: {response.body[:200]!r}"

    def _security_headers(self) -> tuple[bool, str]:
        raw = self.call("GET", "/api/catalog/products")
        headers = raw.headers
        expected = {"X-Content-Type-Options": "nosniff", "X-Frame-Options": "DENY", "Cache-Control": "no-store"}
        missing = {name: value for name, value in expected.items() if headers.get(name) != value}
        return not missing and "Server" not in headers, f"missing or wrong: {missing}; server header: {headers.get('Server')}"

    def _rate_limited(self) -> tuple[bool, str]:
        statuses = [self.call("GET", "/api/catalog/products").status for _ in range(80)]
        return 429 in statuses, f"no 429 in {len(statuses)} anonymous requests"

    def report(self, target: str) -> dict:
        return {
            "schemaVersion": 1, "suite": "commerce-authorization", "target": target,
            "results": [{"name": r.name, "passed": r.passed, "detail": r.detail} for r in self.results],
        }


def _b64(data: bytes) -> str:
    return base64.urlsafe_b64encode(data).rstrip(b"=").decode()


def _forged(token: str) -> str:
    header, payload, signature = token.split(".")
    flipped = ("A" if signature[0] != "A" else "B") + signature[1:]
    return f"{header}.{payload}.{flipped}"


def _unsigned(token: str) -> str:
    payload = token.split(".")[1]
    return f"{_b64(json.dumps({'alg': 'none', 'typ': 'JWT'}).encode())}.{payload}."
