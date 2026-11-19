"""Diagnostic sensors for TrustTunnel Manager."""

from __future__ import annotations

from datetime import datetime

from homeassistant.components.sensor import (
    SensorDeviceClass,
    SensorEntity,
    SensorStateClass,
)
from homeassistant.const import EntityCategory, UnitOfInformation, UnitOfTime
from homeassistant.core import HomeAssistant
from homeassistant.config_entries import ConfigEntry
from homeassistant.helpers.entity_platform import AddEntitiesCallback
from homeassistant.util import dt as dt_util

from .const import DOMAIN
from .coordinator import TrustTunnelManagerCoordinator
from .entity import TrustTunnelManagerEntity


def _parse_optional_datetime(value: object) -> datetime | None:
    """Safely parse an optional datetime returned by the Manager API."""
    if not isinstance(value, str) or not value.strip():
        return None

    try:
        return dt_util.parse_datetime(value)
    except (TypeError, ValueError):
        return None


SENSOR_KEYS = (
    "service_state",
    "service_pid",
    "service_memory",
    "service_cpu_time",
    "service_rx_bytes",
    "service_tx_bytes",
    "watchdog_state",
    "watchdog_failures",
    "watchdog_last_check",
    "watchdog_last_check_message",
    "watchdog_last_restart",
    "watchdog_last_restart_message",
)


async def async_setup_entry(
    hass: HomeAssistant,
    entry: ConfigEntry,
    async_add_entities: AddEntitiesCallback,
) -> None:
    coordinator: TrustTunnelManagerCoordinator = hass.data[DOMAIN][entry.entry_id]
    async_add_entities([TrustTunnelManagerSensor(coordinator, key) for key in SENSOR_KEYS])


class TrustTunnelManagerSensor(TrustTunnelManagerEntity, SensorEntity):
    """Single metric from the node's service or watchdog snapshot."""

    _attr_entity_category = EntityCategory.DIAGNOSTIC

    def __init__(self, coordinator: TrustTunnelManagerCoordinator, key: str) -> None:
        names = {
            "service_state": "Client service state",
            "service_pid": "Client process ID",
            "service_memory": "Client memory",
            "service_cpu_time": "Client CPU time",
            "service_rx_bytes": "VPN interface received",
            "service_tx_bytes": "VPN interface sent",
            "watchdog_state": "VPN Watchdog State",
            "watchdog_failures": "Watchdog Consecutive Failures",
            "watchdog_last_check": "Watchdog Last Check",
            "watchdog_last_check_message": "Watchdog Last Check Message",
            "watchdog_last_restart": "Watchdog Last Restart",
            "watchdog_last_restart_message": "Watchdog Last Restart Message",
        }
        super().__init__(coordinator, key)
        self._key = key
        self._attr_name = names[key]

        if key == "service_memory":
            self._attr_native_unit_of_measurement = UnitOfInformation.BYTES
            self._attr_device_class = SensorDeviceClass.DATA_SIZE
            self._attr_state_class = SensorStateClass.MEASUREMENT
        elif key in ("service_rx_bytes", "service_tx_bytes"):
            self._attr_native_unit_of_measurement = UnitOfInformation.BYTES
            self._attr_device_class = SensorDeviceClass.DATA_SIZE
            self._attr_state_class = SensorStateClass.TOTAL_INCREASING
        elif key == "service_cpu_time":
            self._attr_native_unit_of_measurement = UnitOfTime.SECONDS
            self._attr_device_class = SensorDeviceClass.DURATION
            self._attr_state_class = SensorStateClass.TOTAL_INCREASING
        elif key == "watchdog_failures":
            self._attr_native_unit_of_measurement = "failures"
            self._attr_state_class = SensorStateClass.MEASUREMENT
        elif key in ("watchdog_last_check", "watchdog_last_restart"):
            self._attr_device_class = SensorDeviceClass.TIMESTAMP

    @property
    def native_value(self):
        snapshot = self.manager_snapshot
        if snapshot is None:
            return None

        service = snapshot["service"]
        watchdog = snapshot["watchdog"]
        if self._key == "service_state":
            return f"{service.get('activeState', 'unknown')}/{service.get('subState', 'unknown')}"
        if self._key == "service_pid":
            return service.get("mainProcessId")
        if self._key == "service_memory":
            return service.get("memoryBytes")
        if self._key == "service_cpu_time":
            nanoseconds = service.get("cpuTimeNanoseconds")
            return round(nanoseconds / 1_000_000_000, 3) if nanoseconds is not None else None
        if self._key == "service_rx_bytes":
            return service.get("rxBytes")
        if self._key == "service_tx_bytes":
            return service.get("txBytes")
        if self._key == "watchdog_state":
            return watchdog.get("state")
        if self._key == "watchdog_failures":
            return watchdog.get("consecutiveFailures")
        if self._key == "watchdog_last_check":
            return _parse_optional_datetime(watchdog.get("lastCheckAtUtc"))
        if self._key == "watchdog_last_check_message":
            message = watchdog.get("lastCheckMessage")
            return message[:250] if message else None
        if self._key == "watchdog_last_restart":
            return _parse_optional_datetime(watchdog.get("lastRestartAtUtc"))
        if self._key == "watchdog_last_restart_message":
            message = watchdog.get("lastRestartMessage")
            return message[:250] if message else None
        return None

    @property
    def extra_state_attributes(self) -> dict:
        snapshot = self.manager_snapshot
        if snapshot is None:
            data = self.coordinator.data or {}
            return {"connection_error": data.get("error")}

        attributes = {
            "node_id": snapshot["node"].get("nodeId"),
            "manager_url": self.coordinator.base_url,
            "collected_at": snapshot.get("collectedAtUtc"),
        }
        if self._key.startswith("watchdog"):
            watchdog = snapshot["watchdog"]
            attributes.update(
                {
                    "enabled": watchdog.get("enabled"),
                    "interface": watchdog.get("interfaceName"),
                    "failure_threshold": watchdog.get("failureThreshold"),
                    "last_check_succeeded": watchdog.get("lastCheckSucceeded"),
                    "last_restart_message": watchdog.get("lastRestartMessage"),
                }
            )
        return attributes
