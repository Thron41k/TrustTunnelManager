using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using TrustTunnelManager.Application.Abstractions;
using TrustTunnelManager.Domain.Models;

namespace TrustTunnelManager.Infrastructure.Monitoring;

/// <summary>
/// Reads the native health, Prometheus metrics, and optional per-client endpoints
/// exposed by TrustTunnel.
/// </summary>
public sealed class TrustTunnelMonitoringService(HttpClient httpClient)
    : ITrustTunnelMonitoringService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public async Task<TrustTunnelDashboardSnapshot> GetSnapshotAsync(
        CancellationToken cancellationToken = default)
    {
        var collectedAt = DateTimeOffset.UtcNow;

        try
        {
            using var healthResponse = await httpClient.GetAsync(
                "health-check",
                cancellationToken);

            if (!healthResponse.IsSuccessStatusCode)
            {
                return TrustTunnelDashboardSnapshot.Offline(
                    collectedAt,
                    $"TrustTunnel вернул HTTP {(int)healthResponse.StatusCode} при проверке состояния.");
            }

            using var metricsResponse = await httpClient.GetAsync(
                "metrics",
                cancellationToken);

            if (!metricsResponse.IsSuccessStatusCode)
            {
                return CreateOnlineSnapshot(
                    collectedAt,
                    [],
                    [],
                    warningMessage: $"Endpoint доступен, но метрики вернули HTTP {(int)metricsResponse.StatusCode}.");
            }

            var exposition = await metricsResponse.Content.ReadAsStringAsync(
                cancellationToken);

            var samples = PrometheusSampleParser.Parse(exposition);
            var sessionsByProtocol = PrometheusSampleParser.GetSessionsByProtocol(samples);

            var clientsResult = await ReadClientsAsync(cancellationToken);

            return CreateOnlineSnapshot(
                collectedAt,
                sessionsByProtocol,
                clientsResult.Clients,
                clientsResult.Available,
                clientsResult.WarningMessage,
                samples);
        }
        catch (HttpRequestException exception)
        {
            return TrustTunnelDashboardSnapshot.Offline(
                collectedAt,
                $"Не удалось подключиться к TrustTunnel Metrics API: {exception.Message}");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return TrustTunnelDashboardSnapshot.Offline(
                collectedAt,
                "Истекло время ожидания ответа от TrustTunnel Metrics API.");
        }
    }

    private async Task<ClientsReadResult> ReadClientsAsync(
        CancellationToken cancellationToken)
    {
        try
        {
            using var response = await httpClient.GetAsync("clients", cancellationToken);

            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return new ClientsReadResult(
                    Available: false,
                    Clients: [],
                    WarningMessage: null);
            }

            if (!response.IsSuccessStatusCode)
            {
                return new ClientsReadResult(
                    Available: false,
                    Clients: [],
                    WarningMessage: $"Не удалось получить статистику пользователей: HTTP {(int)response.StatusCode}.");
            }

            await using var contentStream = await response.Content.ReadAsStreamAsync(
                cancellationToken);

            var clients = await JsonSerializer.DeserializeAsync<List<ClientMetricsResponse>>(
                contentStream,
                JsonOptions,
                cancellationToken);

            var result = (clients ?? [])
                .Where(client => !string.IsNullOrWhiteSpace(client.Username))
                .Select(client => new TrustTunnelClientUsage(
                    client.Username,
                    client.Ip,
                    Math.Max(0, client.Sessions),
                    Math.Max(0, client.Inbound),
                    Math.Max(0, client.Outbound)))
                .OrderByDescending(client => client.ActiveSessions)
                .ThenBy(client => client.Username, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            return new ClientsReadResult(
                Available: true,
                Clients: result,
                WarningMessage: null);
        }
        catch (JsonException)
        {
            return new ClientsReadResult(
                Available: false,
                Clients: [],
                WarningMessage: "TrustTunnel вернул ответ /clients в неизвестном формате.");
        }
        catch (HttpRequestException exception)
        {
            return new ClientsReadResult(
                Available: false,
                Clients: [],
                WarningMessage: $"Не удалось прочитать список пользователей: {exception.Message}");
        }
    }

    private static TrustTunnelDashboardSnapshot CreateOnlineSnapshot(
        DateTimeOffset collectedAt,
        IReadOnlyDictionary<string, long> sessionsByProtocol,
        IReadOnlyList<TrustTunnelClientUsage> clients,
        bool clientsAvailable = false,
        string? warningMessage = null,
        IReadOnlyList<PrometheusSample>? samples = null)
    {
        samples ??= [];

        return new TrustTunnelDashboardSnapshot(
            IsOnline: true,
            CollectedAtUtc: collectedAt,
            SessionsByProtocol: sessionsByProtocol,
            InboundTrafficBytes: PrometheusSampleParser.Sum(samples, "inbound_traffic_bytes"),
            OutboundTrafficBytes: PrometheusSampleParser.Sum(samples, "outbound_traffic_bytes"),
            OutboundTcpSockets: PrometheusSampleParser.Sum(samples, "outbound_tcp_sockets"),
            OutboundUdpSockets: PrometheusSampleParser.Sum(samples, "outbound_udp_sockets"),
            Clients: clients,
            PerClientMetricsEnabled: clientsAvailable,
            WarningMessage: warningMessage);
    }

    private sealed record ClientsReadResult(
        bool Available,
        IReadOnlyList<TrustTunnelClientUsage> Clients,
        string? WarningMessage);

    private sealed class ClientMetricsResponse
    {
        [JsonPropertyName("username")]
        public string Username { get; init; } = string.Empty;

        [JsonPropertyName("ip")]
        public string? Ip { get; init; }

        [JsonPropertyName("sessions")]
        public long Sessions { get; init; }

        [JsonPropertyName("inbound")]
        public long Inbound { get; init; }

        [JsonPropertyName("outbound")]
        public long Outbound { get; init; }
    }
}
