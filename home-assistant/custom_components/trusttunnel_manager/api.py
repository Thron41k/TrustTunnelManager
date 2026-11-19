"""HTTPS API client for TrustTunnel Manager."""

from __future__ import annotations

import asyncio
from typing import Any

import aiohttp

from .const import (
    API_PATH,
    MANAGEMENT_TIMEOUT_SECONDS,
    RESTART_PATH,
    REQUEST_TIMEOUT_SECONDS,
)


class TrustTunnelManagerApiError(Exception):
    """Base error for TrustTunnel Manager API failures."""


class TrustTunnelManagerAuthenticationError(TrustTunnelManagerApiError):
    """The configured read-only token is rejected."""


class TrustTunnelManagerManagementError(TrustTunnelManagerApiError):
    """A management action was rejected or failed."""


class TrustTunnelManagerConnectionError(TrustTunnelManagerApiError):
    """The Manager endpoint cannot be reached or returned invalid data."""


class TrustTunnelManagerApi:
    """Client with SHA-256 TLS certificate pinning."""

    def __init__(
        self,
        session: aiohttp.ClientSession,
        base_url: str,
        api_token: str,
        tls_fingerprint: str,
        management_token: str | None = None,
    ) -> None:
        self._session = session
        self._base_url = base_url.rstrip("/")
        self._url = f"{self._base_url}{API_PATH}"
        self._headers = {"Authorization": f"Bearer {api_token}"}
        self._management_headers = (
            {"Authorization": f"Bearer {management_token}"}
            if management_token
            else None
        )
        self._ssl = aiohttp.Fingerprint(bytes.fromhex(tls_fingerprint))

    async def async_get_status(self) -> dict[str, Any]:
        """Fetch the local node status without exposing management operations."""
        try:
            async with self._session.get(
                self._url,
                headers=self._headers,
                ssl=self._ssl,
                timeout=aiohttp.ClientTimeout(total=REQUEST_TIMEOUT_SECONDS),
            ) as response:
                if response.status == 401:
                    raise TrustTunnelManagerAuthenticationError(
                        "Monitoring token rejected by TrustTunnel Manager"
                    )
                if response.status == 404:
                    raise TrustTunnelManagerConnectionError(
                        "Monitoring API is not enabled on this Manager"
                    )
                if response.status >= 500:
                    raise TrustTunnelManagerConnectionError(
                        f"TrustTunnel Manager returned HTTP {response.status}"
                    )
                if response.status != 200:
                    raise TrustTunnelManagerConnectionError(
                        f"Unexpected HTTP status {response.status}"
                    )

                payload = await response.json()
        except (aiohttp.ClientError, asyncio.TimeoutError, ValueError) as err:
            raise TrustTunnelManagerConnectionError(str(err)) from err

        if not isinstance(payload, dict):
            raise TrustTunnelManagerConnectionError("API response is not a JSON object")

        node = payload.get("node")
        service = payload.get("service")
        watchdog = payload.get("watchdog")
        if not isinstance(node, dict) or not node.get("nodeId") or not node.get("name"):
            raise TrustTunnelManagerConnectionError("API response does not contain node identity")
        if not isinstance(service, dict) or not isinstance(watchdog, dict):
            raise TrustTunnelManagerConnectionError("API response does not contain service/watchdog status")

        return payload

    async def async_restart_client(self) -> dict[str, Any]:
        """Restart only the configured TrustTunnel Client service."""
        if self._management_headers is None:
            raise TrustTunnelManagerManagementError(
                "Restart control is not configured for this Manager"
            )

        try:
            async with self._session.post(
                f"{self._base_url}{RESTART_PATH}",
                headers=self._management_headers,
                ssl=self._ssl,
                timeout=aiohttp.ClientTimeout(total=MANAGEMENT_TIMEOUT_SECONDS),
            ) as response:
                if response.status == 401:
                    raise TrustTunnelManagerManagementError(
                        "Management token rejected by TrustTunnel Manager"
                    )
                if response.status == 404:
                    raise TrustTunnelManagerManagementError(
                        "Restart API is not enabled on this Manager"
                    )
                if response.status == 429:
                    raise TrustTunnelManagerManagementError(
                        "Restart request rate limit exceeded; wait before trying again"
                    )

                payload: Any = await response.json()
                if response.status != 200:
                    message = (
                        payload.get("message")
                        if isinstance(payload, dict)
                        else None
                    )
                    raise TrustTunnelManagerManagementError(
                        message or f"Restart request failed with HTTP {response.status}"
                    )
        except (aiohttp.ClientError, asyncio.TimeoutError, ValueError) as err:
            raise TrustTunnelManagerConnectionError(str(err)) from err

        if not isinstance(payload, dict) or payload.get("success") is not True:
            message = payload.get("message") if isinstance(payload, dict) else None
            raise TrustTunnelManagerManagementError(
                message or "TrustTunnel Manager did not confirm the restart"
            )

        return payload
