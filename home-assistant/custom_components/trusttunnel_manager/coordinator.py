"""Polling coordinator for TrustTunnel Manager."""

from __future__ import annotations

import asyncio
import logging
from datetime import datetime
from typing import Any

from homeassistant.core import HomeAssistant
from homeassistant.helpers.aiohttp_client import async_get_clientsession
from homeassistant.helpers.update_coordinator import DataUpdateCoordinator
from homeassistant.util import dt as dt_util

from .api import TrustTunnelManagerApi, TrustTunnelManagerApiError
from .const import (
    CONF_API_TOKEN,
    CONF_BASE_URL,
    CONF_MANAGEMENT_TOKEN,
    CONF_TLS_FINGERPRINT,
    DEFAULT_SCAN_INTERVAL,
)

_LOGGER = logging.getLogger(__name__)


class TrustTunnelManagerCoordinator(DataUpdateCoordinator[dict[str, Any]]):
    """Fetch one Manager's local status every 30 seconds."""

    def __init__(self, hass: HomeAssistant, entry) -> None:
        super().__init__(
            hass,
            _LOGGER,
            name=f"TrustTunnel Manager ({entry.data['node_name']})",
            update_interval=DEFAULT_SCAN_INTERVAL,
        )
        self.api = TrustTunnelManagerApi(
            async_get_clientsession(hass),
            entry.data[CONF_BASE_URL],
            entry.data[CONF_API_TOKEN],
            entry.data[CONF_TLS_FINGERPRINT],
            entry.options.get(CONF_MANAGEMENT_TOKEN, entry.data.get(CONF_MANAGEMENT_TOKEN)),
        )
        self.node_id: str = entry.data["node_id"]
        self.node_name: str = entry.data["node_name"]
        self.base_url: str = entry.data[CONF_BASE_URL]
        self.management_enabled = bool(
            entry.options.get(CONF_MANAGEMENT_TOKEN, entry.data.get(CONF_MANAGEMENT_TOKEN))
        )
        self._restart_lock = asyncio.Lock()
        self.last_restart_at: datetime | None = None
        self.last_restart_success: bool | None = None
        self.last_restart_message: str | None = None
        self._last_snapshot: dict[str, Any] | None = None
        self._last_success: datetime | None = None
        self._last_error: str | None = None

    async def _async_update_data(self) -> dict[str, Any]:
        try:
            snapshot = await self.api.async_get_status()
        except TrustTunnelManagerApiError as err:
            error = str(err)
            if error != self._last_error:
                _LOGGER.warning("Cannot update %s: %s", self.node_name, err)
            self._last_error = error
            return {
                "connected": False,
                "snapshot": self._last_snapshot,
                "last_success": self._last_success,
                "error": error,
            }

        if self._last_error is not None:
            _LOGGER.info("Connection to %s restored", self.node_name)
        self._last_error = None
        self._last_snapshot = snapshot
        self._last_success = dt_util.utcnow()
        self.node_id = snapshot["node"]["nodeId"]
        self.node_name = snapshot["node"]["name"]
        return {
            "connected": True,
            "snapshot": snapshot,
            "last_success": self._last_success,
            "error": None,
        }

    async def async_restart_client(self) -> str:
        """Restart the remote TrustTunnel Client, never an arbitrary command."""
        if not self.management_enabled:
            raise TrustTunnelManagerApiError("Restart control is not configured")
        if self._restart_lock.locked():
            raise TrustTunnelManagerApiError("A restart request is already in progress")

        async with self._restart_lock:
            try:
                result = await self.api.async_restart_client()
            except TrustTunnelManagerApiError as err:
                self.last_restart_at = dt_util.utcnow()
                self.last_restart_success = False
                self.last_restart_message = str(err)
                await self.async_request_refresh()
                raise

            self.last_restart_at = dt_util.utcnow()
            self.last_restart_success = True
            self.last_restart_message = str(
                result.get("message", "TrustTunnel Client restart completed")
            )
            await self.async_request_refresh()
            return self.last_restart_message

    @property
    def connected(self) -> bool:
        return bool(self.data and self.data.get("connected"))

    @property
    def snapshot(self) -> dict[str, Any] | None:
        if not self.connected:
            return None
        return self.data.get("snapshot") if self.data else None
