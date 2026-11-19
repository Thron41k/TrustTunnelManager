"""Management buttons for TrustTunnel Manager."""

from homeassistant.components.button import ButtonDeviceClass, ButtonEntity
from homeassistant.config_entries import ConfigEntry
from homeassistant.core import HomeAssistant
from homeassistant.exceptions import HomeAssistantError
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
    if coordinator.management_enabled:
        async_add_entities([RestartTrustTunnelClientButton(coordinator)])


class RestartTrustTunnelClientButton(TrustTunnelManagerEntity, ButtonEntity):
    """Restart the local TrustTunnel Client on this Manager node."""

    _attr_name = "Перезапустить TrustTunnel Client"
    _attr_device_class = ButtonDeviceClass.RESTART

    def __init__(self, coordinator: TrustTunnelManagerCoordinator) -> None:
        super().__init__(coordinator, "restart_client")

    @property
    def extra_state_attributes(self) -> dict:
        return {
            "last_command": "restart" if self.coordinator.last_restart_at else None,
            "last_command_at": self.coordinator.last_restart_at,
            "last_command_success": self.coordinator.last_restart_success,
            "last_command_message": self.coordinator.last_restart_message,
        }

    async def async_press(self) -> None:
        try:
            await self.coordinator.async_restart_client()
        except Exception as err:  # noqa: BLE001 - report action failures in the HA UI
            raise HomeAssistantError(
                f"Failed to restart TrustTunnel Client on {self.coordinator.node_name}: {err}"
            ) from err
