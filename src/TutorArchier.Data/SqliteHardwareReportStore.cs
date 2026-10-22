using System.Text.Json;
using TutorArchier.Core.Hardware;

namespace TutorArchier.Data;

public sealed class SqliteHardwareReportStore(AppDatabase database) : IHardwareReportStore
{
    public HardwareReport? Load()
    {
        using var connection = database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT json FROM hardware_report WHERE id = 1;";
        if (command.ExecuteScalar() is not string json)
            return null;

        try
        {
            return JsonSerializer.Deserialize<HardwareReport>(json, DataJson.Options);
        }
        catch (JsonException)
        {
            // Shape changed between builds: treat as "never tested" and let detection rerun.
            return null;
        }
    }

    public void Save(HardwareReport report)
    {
        using var connection = database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO hardware_report (id, json, updated_at) VALUES (1, $json, $updated)
            ON CONFLICT(id) DO UPDATE SET json = excluded.json, updated_at = excluded.updated_at;
            """;
        command.Parameters.AddWithValue("$json", JsonSerializer.Serialize(report, DataJson.Options));
        command.Parameters.AddWithValue("$updated", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        command.ExecuteNonQuery();
    }
}
