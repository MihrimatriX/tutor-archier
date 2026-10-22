using System.Text.Json;
using TutorArchier.Core.Media;

namespace TutorArchier.Data;

public sealed class SqliteProbeCache(AppDatabase database) : IProbeCache
{
    public MediaInfo? Get(string path, long sizeBytes, long lastWriteTicks)
    {
        using var connection = database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT json FROM probe_cache WHERE path = $path AND size = $size AND write_ticks = $ticks;";
        command.Parameters.AddWithValue("$path", path);
        command.Parameters.AddWithValue("$size", sizeBytes);
        command.Parameters.AddWithValue("$ticks", lastWriteTicks);
        if (command.ExecuteScalar() is not string json)
            return null;

        try
        {
            return JsonSerializer.Deserialize<MediaInfo>(json, DataJson.Options);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public void Set(string path, long sizeBytes, long lastWriteTicks, MediaInfo info)
    {
        using var connection = database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO probe_cache (path, size, write_ticks, json) VALUES ($path, $size, $ticks, $json)
            ON CONFLICT(path) DO UPDATE SET size = excluded.size, write_ticks = excluded.write_ticks, json = excluded.json;
            """;
        command.Parameters.AddWithValue("$path", path);
        command.Parameters.AddWithValue("$size", sizeBytes);
        command.Parameters.AddWithValue("$ticks", lastWriteTicks);
        command.Parameters.AddWithValue("$json", JsonSerializer.Serialize(info, DataJson.Options));
        command.ExecuteNonQuery();
    }
}
