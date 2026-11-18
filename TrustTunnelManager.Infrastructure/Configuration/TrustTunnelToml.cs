using Tomlyn;
using Tomlyn.Model;

namespace TrustTunnelManager.Infrastructure.Configuration;

internal static class TrustTunnelToml
{
    public static TomlTable Parse(string text, string description)
    {
        try
        {
            return TomlSerializer.Deserialize<TomlTable>(text)
                   ?? throw new InvalidDataException($"{description} is empty.");
        }
        catch (Exception exception) when (exception is TomlException or InvalidOperationException)
        {
            throw new InvalidDataException(
                $"Не удалось разобрать {description}: {exception.Message}",
                exception);
        }
    }

    public static string Serialize(TomlTable table) =>
        TomlSerializer.Serialize(table);

    public static TomlTableArray GetRequiredTableArray(TomlTable root, string key, string description)
    {
        if (!root.TryGetValue(key, out var value) || value is not TomlTableArray array)
        {
            throw new InvalidDataException(
                $"В {description} отсутствует массив таблиц [[{key}]].");
        }

        return array;
    }

    public static TomlTableArray GetOrCreateTableArray(TomlTable root, string key)
    {
        if (root.TryGetValue(key, out var value))
        {
            return value as TomlTableArray
                   ?? throw new InvalidDataException($"Параметр '{key}' должен быть массивом таблиц.");
        }

        var array = new TomlTableArray();
        root[key] = array;
        return array;
    }

    public static string? GetString(TomlTable table, string key) =>
        table.TryGetValue(key, out var value) ? value as string : null;

    public static uint? GetUInt32(TomlTable table, string key)
    {
        if (!table.TryGetValue(key, out var value))
        {
            return null;
        }

        return value switch
        {
            long number when number >= 0 && number <= uint.MaxValue => (uint)number,
            int number when number >= 0 => (uint)number,
            _ => null
        };
    }

    public static void SetOptionalUInt32(TomlTable table, string key, uint? value)
    {
        if (value.HasValue)
        {
            table[key] = (long)value.Value;
        }
        else
        {
            table.Remove(key);
        }
    }
}
