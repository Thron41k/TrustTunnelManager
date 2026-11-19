"""Binary sensors for Manager, client service and watchdog state."""

from homeassistant.components.binary_sensor import (
    BinarySensorDeviceClass,
    BinarySensorEntity,
)
from homeassistant.core import HomeAssistant
from homeassistant.config_entries import ConfigEntry
from homeassistant.helpers.entity_platform import AddEntitiesCallback

from .const import DOMAIN
from .coordinator import TrustTunnelManagerCoordinator
from .entity import TrustTunnelManagerEntity


async def async_setup_entry(
    hass: HomeAssistant,
    entry: ConfigEntry,
    async_add_entities: AddEntitiesCallback,
) -> None:
    coordinator: TrustTunnelManagerCoordinator = hass.data[DOMAIN][entry.entry_id]
    async_add_entities(
        [
            ManagerApiBinarySensor(coordinator),
            TrustTunnelClientBinarySensor(coordinator),
            WatchdogHealthyBinarySensor(coordinator),
        ]
    )


class ManagerApiBinarySensor(TrustTunnelManagerEntity, BinarySensorEntity):
    """Whether the Manager read-only API answers successfully."""

    _attr_name = "Manager API"
    _attr_device_class = BinarySensorDeviceClass.CONNECTIVITY
    _attr_entity_registry_enabled_default = True

    def __init__(self, coordinator: TrustTunnelManagerCoordinator) -> None:
        super().__init__(coordinator, "manager_api")

    @property
    def available(self) -> bool:
        # This entity must remain available to report an API outage as OFF.
        return True

    @property
    def is_on(self) -> bool:
        return self.coordinator.connected

    @property
    def extra_state_attributes(self) -> dict:
        data = self.coordinator.data or {}
        return {
            "base_url": self.coordinator.base_url,
            "last_success": data.get("last_success"),
            "connection_error": data.get("error"),
        }


class TrustTunnelClientBinarySensor(TrustTunnelManagerEntity, BinarySensorEntity):
    """Whether trusttunnel.service is active and running."""

    _attr_name = "TrustTunnel Client"
    _attr_device_class = BinarySensorDeviceClass.RUNNING

    def __init__(self, coordinator: TrustTunnelManagerCoordinator) -> None:
        super().__init__(coordinator, "client_running")

    @property
    def is_on(self) -> bool | None:
        snapshot = self.manager_snapshot
        if snapshot is None:
            return None
        service = snapshot["service"]
        return (
            service.get("activeState", "").lower() == "active"
            and service.get("subState", "").lower() == "running"
        )

    @property
    def extra_state_attributes(self) -> dict:
        snapshot = self.manager_snapshot
        if snapshot is None:
            return {}
        service = snapshot["service"]
        return {
            "installed": service.get("isInstalled"),
            "supported": service.get("isSupported"),
            "active_state": service.get("activeState"),
            "sub_state": service.get("subState"),
            "main_pid": service.get("mainProcessId"),
            "error": service.get("error"),
        }


class WatchdogHealthyBinarySensor(TrustTunnelManagerEntity, BinarySensorEntity):
    """Whether the embedded VPN watchdog most recently succeeded."""

    _attr_name = "VPN Watchdog Healthy"
    _attr_device_class = BinarySensorDeviceClass.CONNECTIVITY

    def __init__(self, coordinator: TrustTunnelManagerCoordinator) -> None:
        super().__init__(coordinator, "watchdog_healthy")

    @property
    def is_on(self) -> bool | None:
        snapshot = self.manager_snapshot
        if snapshot is None:
            return None
        watchdog = snapshot["watchdog"]
        return watchdog.get("enabled", False) and watchdog.get("state") == "Healthy"

    @property
    def extra_state_attributes(self) -> dict:
        snapshot = self.manager_snapshot
        if snapshot is None:
            return {}
        watchdog = snapshot["watchdog"]
        return {
            "state": watchdog.get("state"),
            "interface": watchdog.get("interfaceName"),
            "consecutive_failures": watchdog.get("consecutiveFailures"),
            "failure_threshold": watchdog.get("failureThreshold"),
            "last_check_at": watchdog.get("lastCheckAtUtc"),
            "last_check_succeeded": watchdog.get("lastCheckSucceeded"),
            "last_check_message": watchdog.get("lastCheckMessage"),
            "last_restart_at": watchdog.get("lastRestartAtUtc"),
            "last_restart_message": watchdog.get("lastRestartMessage"),
        }
