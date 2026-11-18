using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Tomlyn.Model;
using TrustTunnelManager.Application.Abstractions;
using TrustTunnelManager.Application.Models;
using TrustTunnelManager.Domain.Models;
using TrustTunnelManager.Infrastructure.Configuration;

namespace TrustTunnelManager.Infrastructure.Clients;

public sealed class TrustTunnelClientManagementService(
    TrustTunnelPathResolver pathResolver,
    ConfigurationChangeCoordinator changeCoordinator)
    : ITrustTunnelClientManagementService
{
    private static readonly Regex UsernamePattern = new(
        @"^[A-Za-z0-9_.@+-]{1,64}$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public async Task<IReadOnlyList<TrustTunnelClient>> GetClientsAsync(
        CancellationToken cancellationToken = default)
    {
        var path = await pathResolver.ResolveCredentialsFileAsync(cancellationToken);

        if (!File.Exists(path))
        {
            throw new FileNotFoundException("Не найден файл credentials.toml.", path);
        }

        var root = TrustTunnelToml.Parse(
            await File.ReadAllTextAsync(path, cancellationToken),
            "credentials.toml");

        return ReadClients(root);
    }

    public async Task<TrustTunnelClient> CreateClientAsync(
        CreateTrustTunnelClientCommand command,
        CancellationToken cancellationToken = default)
    {
        ValidateUsername(command.Username);
        ValidatePassword(command.Password);
        ValidateConnectionLimit(command.MaxHttp2Connections, nameof(command.MaxHttp2Connections));
        ValidateConnectionLimit(command.MaxHttp3Connections, nameof(command.MaxHttp3Connections));

        var path = await pathResolver.ResolveCredentialsFileAsync(cancellationToken);

        await changeCoordinator.ApplyAsync(
            path,
            original =>
            {
                var root = TrustTunnelToml.Parse(original, "credentials.toml");
                var clients = TrustTunnelToml.GetOrCreateTableArray(root, "client");

                if (clients.Any(client => string.Equals(
                        TrustTunnelToml.GetString(client, "username"),
                        command.Username,
                        StringComparison.OrdinalIgnoreCase)))
                {
                    throw new InvalidOperationException(
                        $"Пользователь '{command.Username}' уже существует.");
                }

                var client = new TomlTable
                {
                    ["username"] = command.Username,
                    ["password"] = command.Password
                };

                TrustTunnelToml.SetOptionalUInt32(
                    client, "max_http2_conns", command.MaxHttp2Connections);
                TrustTunnelToml.SetOptionalUInt32(
                    client, "max_http3_conns", command.MaxHttp3Connections);

                clients.Add(client);

                return TrustTunnelToml.Serialize(root);
            },
            cancellationToken);

        return new TrustTunnelClient(
            command.Username,
            command.MaxHttp2Connections,
            command.MaxHttp3Connections);
    }

    public async Task<TrustTunnelClient> UpdateClientAsync(
        string username,
        UpdateTrustTunnelClientCommand command,
        CancellationToken cancellationToken = default)
    {
        ValidateUsername(username);

        if (!string.IsNullOrWhiteSpace(command.NewPassword))
        {
            ValidatePassword(command.NewPassword);
        }

        ValidateConnectionLimit(command.MaxHttp2Connections, nameof(command.MaxHttp2Connections));
        ValidateConnectionLimit(command.MaxHttp3Connections, nameof(command.MaxHttp3Connections));

        var path = await pathResolver.ResolveCredentialsFileAsync(cancellationToken);

        await changeCoordinator.ApplyAsync(
            path,
            original =>
            {
                var root = TrustTunnelToml.Parse(original, "credentials.toml");
                var clients = TrustTunnelToml.GetRequiredTableArray(root, "client", "credentials.toml");
                var client = FindClient(clients, username);

                if (!string.IsNullOrWhiteSpace(command.NewPassword))
                {
                    client["password"] = command.NewPassword;
                }

                TrustTunnelToml.SetOptionalUInt32(
                    client, "max_http2_conns", command.MaxHttp2Connections);
                TrustTunnelToml.SetOptionalUInt32(
                    client, "max_http3_conns", command.MaxHttp3Connections);

                return TrustTunnelToml.Serialize(root);
            },
            cancellationToken);

        return new TrustTunnelClient(
            username,
            command.MaxHttp2Connections,
            command.MaxHttp3Connections);
    }

    public async Task DeleteClientAsync(
        string username,
        CancellationToken cancellationToken = default)
    {
        ValidateUsername(username);

        var path = await pathResolver.ResolveCredentialsFileAsync(cancellationToken);

        await changeCoordinator.ApplyAsync(
            path,
            original =>
            {
                var root = TrustTunnelToml.Parse(original, "credentials.toml");
                var clients = TrustTunnelToml.GetRequiredTableArray(root, "client", "credentials.toml");
                var client = FindClient(clients, username);

                if (!clients.Remove(client))
                {
                    throw new InvalidOperationException($"Не удалось удалить пользователя '{username}'.");
                }

                return TrustTunnelToml.Serialize(root);
            },
            cancellationToken);
    }

    public static string GeneratePassword(int byteLength = 24)
    {
        if (byteLength is < 16 or > 64)
        {
            throw new ArgumentOutOfRangeException(nameof(byteLength));
        }

        return Convert.ToBase64String(RandomNumberGenerator.GetBytes(byteLength))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

    private static IReadOnlyList<TrustTunnelClient> ReadClients(TomlTable root)
    {
        var clients = TrustTunnelToml.GetRequiredTableArray(root, "client", "credentials.toml");

        return clients.Select(client =>
        {
            var username = TrustTunnelToml.GetString(client, "username");
            var password = TrustTunnelToml.GetString(client, "password");

            if (string.IsNullOrWhiteSpace(username) || string.IsNullOrEmpty(password))
            {
                throw new InvalidDataException(
                    "В credentials.toml найдена запись без username или password.");
            }

            return new TrustTunnelClient(
                username,
                TrustTunnelToml.GetUInt32(client, "max_http2_conns"),
                TrustTunnelToml.GetUInt32(client, "max_http3_conns"));
        }).OrderBy(client => client.Username, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static TomlTable FindClient(TomlTableArray clients, string username) =>
        clients.FirstOrDefault(client => string.Equals(
            TrustTunnelToml.GetString(client, "username"),
            username,
            StringComparison.OrdinalIgnoreCase))
        ?? throw new KeyNotFoundException($"Пользователь '{username}' не найден.");

    private static void ValidateUsername(string username)
    {
        if (string.IsNullOrWhiteSpace(username) || !UsernamePattern.IsMatch(username))
        {
            throw new ArgumentException(
                "Имя пользователя должно содержать 1–64 символа: латинские буквы, цифры, точку, подчёркивание, @, + или -.",
                nameof(username));
        }
    }

    private static void ValidatePassword(string password)
    {
        if (string.IsNullOrWhiteSpace(password) || password.Length is < 16 or > 256)
        {
            throw new ArgumentException(
                "Пароль должен содержать от 16 до 256 символов.",
                nameof(password));
        }
    }

    private static void ValidateConnectionLimit(uint? value, string parameterName)
    {
        if (value > 1_000_000)
        {
            throw new ArgumentOutOfRangeException(
                parameterName,
                "Лимит соединений не должен превышать 1 000 000.");
        }
    }
}
