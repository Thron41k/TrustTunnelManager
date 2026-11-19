from datetime import timedelta

DOMAIN = "trusttunnel_manager"
PLATFORMS = ["binary_sensor", "sensor", "button"]
DEFAULT_SCAN_INTERVAL = timedelta(seconds=30)

CONF_BASE_URL = "base_url"
CONF_API_TOKEN = "api_token"
CONF_MANAGEMENT_TOKEN = "management_token"
CONF_TLS_FINGERPRINT = "tls_fingerprint"

API_PATH = "/api/v1/monitoring/status"
RESTART_PATH = "/api/v1/management/restart"
REQUEST_TIMEOUT_SECONDS = 10
MANAGEMENT_TIMEOUT_SECONDS = 30
