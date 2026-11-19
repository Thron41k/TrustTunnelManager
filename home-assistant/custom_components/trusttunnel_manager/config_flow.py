"""UI setup flow for TrustTunnel Manager."""

from __future__ import annotations

from urllib.parse import urlsplit

import voluptuous as vol
from homeassistant import config_entries
from homeassistant.config_entries import ConfigEntry, OptionsFlow
from homeassistant.core import callback
from homeassistant.helpers import selector
from homeassistant.helpers.aiohttp_client import async_get_clientsession

from .api import (
    TrustTunnelManagerApi,
    TrustTunnelManagerApiError,
    TrustTunnelManagerAuthenticationError,
)
from .const import (
    CONF_API_TOKEN,
    CONF_BASE_URL,
    CONF_MANAGEMENT_TOKEN,
    CONF_TLS_FINGERPRINT,
    DOMAIN,
)


class TrustTunnelManagerConfigFlow(config_entries.ConfigFlow, domain=DOMAIN):
    """Configure one TrustTunnel Manager node."""

    VERSION = 1

    @staticmethod
    @callback
    def async_get_options_flow(config_entry: ConfigEntry) -> OptionsFlow:
        """Allow adding or changing the restart token on an existing entry."""
        return TrustTunnelManagerOptionsFlow(config_entry)

    async def async_step_user(self, user_input: dict | None = None):
        errors: dict[str, str] = {}

        if user_input is not None:
            base_url = user_input[CONF_BASE_URL].strip().rstrip("/")
            api_token = user_input[CONF_API_TOKEN].strip()
            management_token = user_input.get(CONF_MANAGEMENT_TOKEN, "").strip()
            fingerprint = "".join(
                character
                for character in user_input[CONF_TLS_FINGERPRINT]
                if character not in ": -"
            ).lower()

            parsed = urlsplit(base_url)
            if (
                parsed.scheme != "https"
                or not parsed.hostname
                or parsed.path not in ("", "/")
                or parsed.username is not None
                or parsed.password is not None
                or parsed.query
                or parsed.fragment
            ):
                errors["base"] = "invalid_url"
            elif len(fingerprint) != 64 or any(
                character not in "0123456789abcdef" for character in fingerprint
            ):
                errors["base"] = "invalid_fingerprint"
            elif len(api_token) < 32:
                errors["base"] = "invalid_token"
            elif management_token and len(management_token) < 32:
                errors["base"] = "invalid_management_token"
            else:
                try:
                    api = TrustTunnelManagerApi(
                        async_get_clientsession(self.hass),
                        base_url,
                        api_token,
                        fingerprint,
                    )
                    status = await api.async_get_status()
                except TrustTunnelManagerAuthenticationError:
                    errors["base"] = "invalid_auth"
                except TrustTunnelManagerApiError:
                    errors["base"] = "cannot_connect"
                except Exception:  # noqa: BLE001 - convert setup errors to a form error
                    errors["base"] = "unknown"
                else:
                    node = status["node"]
                    await self.async_set_unique_id(node["nodeId"])
                    self._abort_if_unique_id_configured()
                    return self.async_create_entry(
                        title=f"TrustTunnel Manager — {node['name']}",
                        data={
                            CONF_BASE_URL: base_url,
                            CONF_API_TOKEN: api_token,
                            CONF_TLS_FINGERPRINT: fingerprint,
                            "node_id": node["nodeId"],
                            "node_name": node["name"],
                            **({CONF_MANAGEMENT_TOKEN: management_token} if management_token else {}),
                        },
                    )

        schema = vol.Schema(
            {
                vol.Required(CONF_BASE_URL): str,
                vol.Required(CONF_API_TOKEN): selector.TextSelector(
                    selector.TextSelectorConfig(type=selector.TextSelectorType.PASSWORD)
                ),
                vol.Required(CONF_TLS_FINGERPRINT): str,
                vol.Optional(CONF_MANAGEMENT_TOKEN, default=""): selector.TextSelector(
                    selector.TextSelectorConfig(type=selector.TextSelectorType.PASSWORD)
                ),
            }
        )
        return self.async_show_form(step_id="user", data_schema=schema, errors=errors)


class TrustTunnelManagerOptionsFlow(OptionsFlow):
    """Configure optional restart control for an existing Manager entry."""

    def __init__(self, config_entry: ConfigEntry) -> None:
        self._config_entry = config_entry

    async def async_step_init(self, user_input: dict | None = None):
        errors: dict[str, str] = {}
        current_token = self._config_entry.options.get(
            CONF_MANAGEMENT_TOKEN,
            self._config_entry.data.get(CONF_MANAGEMENT_TOKEN, ""),
        )

        if user_input is not None:
            management_token = user_input.get(CONF_MANAGEMENT_TOKEN, "").strip()
            if management_token and len(management_token) < 32:
                errors["base"] = "invalid_management_token"
            else:
                # Keep an explicit empty value to allow disabling a previously configured token.
                return self.async_create_entry(
                    title="",
                    data={CONF_MANAGEMENT_TOKEN: management_token},
                )

        schema = vol.Schema(
            {
                vol.Optional(CONF_MANAGEMENT_TOKEN, default=current_token): selector.TextSelector(
                    selector.TextSelectorConfig(type=selector.TextSelectorType.PASSWORD)
                ),
            }
        )
        return self.async_show_form(
            step_id="init",
            data_schema=schema,
            errors=errors,
        )
