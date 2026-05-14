from __future__ import annotations

import json
import os
import urllib.error
import urllib.parse
import urllib.request
from typing import Any


DEFAULT_BASE_URL = "http://127.0.0.1:8765"


class UnityBridgeError(RuntimeError):
    """Raised when the Unity bridge cannot be reached or returns invalid data."""


class UnityClient:
    def __init__(self, base_url: str | None = None, timeout: float = 30.0) -> None:
        self.base_url = (base_url or os.environ.get("UNITY_MCP_BRIDGE_URL") or DEFAULT_BASE_URL).rstrip("/")
        self.timeout = timeout

    def get(self, path: str, **params: Any) -> dict[str, Any]:
        query = {
            key: self._encode_value(value)
            for key, value in params.items()
            if value is not None
        }
        url = f"{self.base_url}{path}"
        if query:
            url = f"{url}?{urllib.parse.urlencode(query)}"

        request = urllib.request.Request(url, method="GET")
        try:
            with urllib.request.urlopen(request, timeout=self.timeout) as response:
                body = response.read().decode("utf-8")
        except urllib.error.HTTPError as exc:
            detail = exc.read().decode("utf-8", errors="replace")
            raise UnityBridgeError(f"Unity bridge HTTP {exc.code}: {detail}") from exc
        except urllib.error.URLError as exc:
            raise UnityBridgeError(
                f"Cannot reach Unity bridge at {self.base_url}. "
                "Open Unity and start Tools > Codex MCP Bridge > Start."
            ) from exc

        try:
            return json.loads(body)
        except json.JSONDecodeError as exc:
            raise UnityBridgeError(f"Unity bridge returned invalid JSON: {body[:500]}") from exc

    @staticmethod
    def _encode_value(value: Any) -> str:
        if isinstance(value, bool):
            return "true" if value else "false"
        if isinstance(value, (list, tuple)):
            return ",".join(str(item) for item in value)
        return str(value)
