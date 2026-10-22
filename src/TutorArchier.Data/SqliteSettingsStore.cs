using System.Collections.Concurrent;
using System.Text.Json;
using TutorArchier.Core.Settings;

namespace TutorArchier.Data;

/// <summary>Key/value settings with JSON-encoded values. Reads are served from memory after the first hit.</summary>
public sealed class SqliteSettingsStore(AppDatabase database) : ISettingsStore
{
    private readonly ConcurrentDictionary<string, string?> _cache = new();

    public T Get<T>(SettingKey<T> key)
    {
        var json = _cache.GetOrAdd(key.Name, ReadRaw);
        if (json is null)
            return key.Default;

        try
        {
            return JsonSerializer.Deserialize<T>(json, DataJson.Options)!;
        }
        catch (JsonException)
        {
            // A value written by a future/older build that no longer parses falls back to the default.
            return key.Default;
        }
    }

    public void Set<T>(SettingKey<T> key, T value)
    {
        var json = JsonSerializer.Serialize(value, DataJson.Options);

        using var connection = database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO settings (key, value) VALUES ($key, $value)
            ON CONFLICT(key) DO UPDATE SET value = excluded.value;
            """;
        command.Parameters.AddWithValue("$key", key.Name);
        command.Parameters.AddWithValue("$value", json);
        command.ExecuteNonQuery();

        _cache[key.Name] = json;
    }

    private string? ReadRaw(string name)
    {
        using var connection = database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT value FROM settings WHERE key = $key;";
        command.Parameters.AddWithValue("$key", name);
        return command.ExecuteScalar() as string;
    }
}
