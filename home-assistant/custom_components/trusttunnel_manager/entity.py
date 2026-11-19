"""Shared entity helpers."""

from homeassistant.helpers.device_registry import DeviceInfo
from homeassistant.helpers.update_coordinator import CoordinatorEntity

from .const import DOMAIN
from .coordinator import TrustTunnelManagerCoordinator


class TrustTunnelManagerEntity(CoordinatorEntity[TrustTunnelManagerCoordinator]):
    """Base entity for one TrustTunnel Manager node."""

    def __init__(self, coordinator: TrustTunnelManagerCoordinator, key: str) -> None:
        super().__init__(coordinator)
        self._attr_unique_id = f"{coordinator.node_id}_{key}"

    @property
    def device_info(self) -> DeviceInfo:
        return DeviceInfo(
            identifiers={(DOMAIN, self.coordinator.node_id)},
            name=self.coordinator.node_name,
            manufacturer="TrustTunnelManager",
            model="TrustTunnel Client node",
            configuration_url=f"{self.coordinator.base_url}/nodes",
        )

    @property
    def manager_snapshot(self) -> dict | None:
        return self.coordinator.snapshot

    @property
    def available(self) -> bool:
        return self.coordinator.connected and self.manager_snapshot is not None
