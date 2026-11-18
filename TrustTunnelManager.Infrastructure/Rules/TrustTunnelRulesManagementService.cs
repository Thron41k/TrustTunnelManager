using System.Net;
using System.Text.RegularExpressions;
using Tomlyn.Model;
using TrustTunnelManager.Application.Abstractions;
using TrustTunnelManager.Domain.Models;
using TrustTunnelManager.Infrastructure.Configuration;

namespace TrustTunnelManager.Infrastructure.Rules;

public sealed class TrustTunnelRulesManagementService(
    TrustTunnelPathResolver pathResolver,
    ConfigurationChangeCoordinator changeCoordinator)
    : ITrustTunnelRulesManagementService
{
    private static readonly Regex HexPattern = new(
        @"^(?:[0-9a-fA-F]{2})+$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public async Task<IReadOnlyList<TrustTunnelAccessRule>> GetRulesAsync(
        CancellationToken cancellationToken = default)
    {
        var path = await pathResolver.ResolveRulesFileAsync(cancellationToken);

        if (!File.Exists(path))
        {
            return [];
        }

        var root = TrustTunnelToml.Parse(
            await File.ReadAllTextAsync(path, cancellationToken),
            "rules.toml");

        if (!root.TryGetValue("rule", out var value))
        {
            return [];
        }

        if (value is not TomlTableArray rules)
        {
            throw new InvalidDataException("Параметр rule в rules.toml должен быть массивом таблиц.");
        }

        return rules.Select((rule, index) => ReadRule(rule, index)).ToArray();
    }

    public async Task ReplaceRulesAsync(
        IReadOnlyList<TrustTunnelAccessRule> rules,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(rules);

        if (rules.Count > 1000)
        {
            throw new ArgumentOutOfRangeException(nameof(rules), "Допускается не более 1000 правил.");
        }

        foreach (var rule in rules)
        {
            ValidateRule(rule);
        }

        var path = await pathResolver.ResolveRulesFileAsync(cancellationToken);

        await changeCoordinator.ApplyAsync(
            path,
            original =>
            {
                var root = string.IsNullOrWhiteSpace(original)
                    ? new TomlTable()
                    : TrustTunnelToml.Parse(original, "rules.toml");

                var array = new TomlTableArray();

                foreach (var rule in rules)
                {
                    var table = new TomlTable();

                    SetOptionalString(table, "cidr", rule.Cidr);
                    SetOptionalString(table, "client_random_prefix", rule.ClientRandomPrefix);
                    SetOptionalString(table, "client_random_auth_key", rule.ClientRandomAuthKey);
                    table["action"] = rule.Action.ToLowerInvariant();

                    array.Add(table);
                }

                root["rule"] = array;

                return TrustTunnelToml.Serialize(root);
            },
            cancellationToken);
    }

    private static TrustTunnelAccessRule ReadRule(TomlTable rule, int index)
    {
        var action = TrustTunnelToml.GetString(rule, "action");

        if (string.IsNullOrWhiteSpace(action))
        {
            throw new InvalidDataException($"У правила №{index + 1} отсутствует action.");
        }

        return new TrustTunnelAccessRule(
            index,
            TrustTunnelToml.GetString(rule, "cidr"),
            TrustTunnelToml.GetString(rule, "client_random_prefix"),
            TrustTunnelToml.GetString(rule, "client_random_auth_key"),
            action);
    }

    private static void ValidateRule(TrustTunnelAccessRule rule)
    {
        if (rule.Action is not ("allow" or "deny"))
        {
            throw new ArgumentException("Действие правила должно быть allow или deny.");
        }

        if (!string.IsNullOrWhiteSpace(rule.Cidr) &&
            !IPNetwork.TryParse(rule.Cidr, out _))
        {
            throw new ArgumentException($"Некорректная CIDR-сеть: {rule.Cidr}.");
        }

        if (!string.IsNullOrWhiteSpace(rule.ClientRandomPrefix))
        {
            var parts = rule.ClientRandomPrefix.Split('/', StringSplitOptions.None);

            if (parts.Length is < 1 or > 2 ||
                !HexPattern.IsMatch(parts[0]) ||
                (parts.Length == 2 && !HexPattern.IsMatch(parts[1])))
            {
                throw new ArgumentException(
                    "Client Random Prefix должен быть hex-строкой либо иметь формат prefix/mask.");
            }

            if (parts.Length == 2 &&
                Convert.FromHexString(parts[0]).Length != Convert.FromHexString(parts[1]).Length)
            {
                throw new ArgumentException(
                    "Длина Client Random Prefix и маски должна совпадать.");
            }
        }

        if (!string.IsNullOrWhiteSpace(rule.ClientRandomAuthKey))
        {
            if (!HexPattern.IsMatch(rule.ClientRandomAuthKey))
            {
                throw new ArgumentException("Client Random Auth Key должен быть hex-строкой.");
            }

        }

        if (!string.IsNullOrWhiteSpace(rule.ClientRandomPrefix) &&
            !string.IsNullOrWhiteSpace(rule.ClientRandomAuthKey))
        {
            throw new ArgumentException(
                "Нельзя одновременно задавать Client Random Prefix и Auth Key.");
        }

        if (string.IsNullOrWhiteSpace(rule.Cidr) &&
            string.IsNullOrWhiteSpace(rule.ClientRandomPrefix) &&
            string.IsNullOrWhiteSpace(rule.ClientRandomAuthKey))
        {
            // A conditionless rule is a valid catch-all rule in TrustTunnel.
            // The UI displays an explicit warning for it.
        }
    }

    private static void SetOptionalString(TomlTable table, string key, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            table[key] = value.Trim();
        }
    }
}
