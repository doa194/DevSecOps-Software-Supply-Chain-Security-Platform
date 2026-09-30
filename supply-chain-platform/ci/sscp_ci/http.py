"""Small HTTPS client on urllib. TLS is always verified; the job image trusts the platform's
local root CA through the system store."""
from __future__ import annotations

import json
import ssl
import urllib.error
import urllib.parse
import urllib.request
import uuid
from dataclasses import dataclass, field
from typing import Any, Mapping

_CONTEXT = ssl.create_default_context()


@dataclass
class Response:
    status: int
    body: bytes
    headers: Mapping[str, str] = field(default_factory=dict)   # case-insensitive lookups

    def json(self) -> Any:
        return json.loads(self.body) if self.body else None


class HttpError(RuntimeError):
    def __init__(self, method: str, url: str, response: Response):
        self.response = response
        detail = response.body[:500].decode("utf-8", "replace")
        super().__init__(f"{method} {url} -> {response.status}: {detail}")


def request(method: str, url: str, *, headers: dict[str, str] | None = None, body: bytes | None = None,
            json_body: Any = None, form: dict[str, str] | None = None, timeout: float = 60, ok: tuple[int, ...] = ()) -> Response:
    headers = dict(headers or {})
    if json_body is not None:
        body = json.dumps(json_body).encode()
        headers["Content-Type"] = "application/json"
    elif form is not None:
        body = urllib.parse.urlencode(form).encode()
        headers["Content-Type"] = "application/x-www-form-urlencoded"
    req = urllib.request.Request(url, data=body, headers=headers, method=method)
    try:
        with urllib.request.urlopen(req, timeout=timeout, context=_CONTEXT) as raw:
            return Response(raw.status, raw.read(), raw.headers)
    except urllib.error.HTTPError as error:
        response = Response(error.code, error.read(), error.headers)
        if error.code in ok:
            return response
        raise HttpError(method, url, response) from None


def multipart(fields: dict[str, str], files: dict[str, tuple[str, bytes, str]]) -> tuple[bytes, str]:
    """Encodes multipart/form-data. `files` maps field -> (filename, content, content type)."""
    boundary = f"sscp-{uuid.uuid4().hex}"
    parts: list[bytes] = []
    for name, value in fields.items():
        parts.append(f'--{boundary}\r\nContent-Disposition: form-data; name="{name}"\r\n\r\n{value}\r\n'.encode())
    for name, (filename, content, content_type) in files.items():
        parts.append(f'--{boundary}\r\nContent-Disposition: form-data; name="{name}"; filename="{filename}"\r\n'
                     f"Content-Type: {content_type}\r\n\r\n".encode() + content + b"\r\n")
    parts.append(f"--{boundary}--\r\n".encode())
    return b"".join(parts), f"multipart/form-data; boundary={boundary}"
