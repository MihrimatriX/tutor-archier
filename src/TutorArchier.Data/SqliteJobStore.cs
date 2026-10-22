using Microsoft.Data.Sqlite;
using TutorArchier.Core.Jobs;

namespace TutorArchier.Data;

public sealed class SqliteJobStore(AppDatabase database) : IJobStore
{
    private const string Columns = "id, name, source_folder, output_path, status, plan_json, completed_steps, total_steps, error, created_at";

    public JobRecord Add(JobRecord job)
    {
        using var connection = database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO jobs (name, source_folder, output_path, status, plan_json, completed_steps, total_steps, error, created_at)
            VALUES ($name, $source, $output, $status, $plan, $completed, $total, $error, $created)
            RETURNING id;
            """;
        Bind(command, job);
        var id = (long)command.ExecuteScalar()!;
        return job with { Id = id };
    }

    public void Update(JobRecord job)
    {
        using var connection = database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE jobs SET name = $name, source_folder = $source, output_path = $output, status = $status, plan_json = $plan,
                completed_steps = $completed, total_steps = $total, error = $error, created_at = $created
            WHERE id = $id;
            """;
        Bind(command, job);
        command.Parameters.AddWithValue("$id", job.Id);
        command.ExecuteNonQuery();
    }

    public void Delete(long id)
    {
        using var connection = database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM jobs WHERE id = $id;";
        command.Parameters.AddWithValue("$id", id);
        command.ExecuteNonQuery();
    }

    public IReadOnlyList<JobRecord> List()
    {
        using var connection = database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {Columns} FROM jobs ORDER BY id;";
        using var reader = command.ExecuteReader();
        var jobs = new List<JobRecord>();
        while (reader.Read())
        {
            jobs.Add(new JobRecord(
                reader.GetInt64(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                Enum.TryParse<JobStatus>(reader.GetString(4), out var status) ? status : JobStatus.Failed,
                reader.GetString(5),
                reader.GetInt32(6),
                reader.GetInt32(7),
                reader.IsDBNull(8) ? null : reader.GetString(8),
                DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(9))));
        }
        return jobs;
    }

    public void RequeueInterrupted()
    {
        using var connection = database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE jobs SET status = $queued WHERE status = $running;";
        command.Parameters.AddWithValue("$queued", nameof(JobStatus.Queued));
        command.Parameters.AddWithValue("$running", nameof(JobStatus.Running));
        command.ExecuteNonQuery();
    }

    private static void Bind(SqliteCommand command, JobRecord job)
    {
        command.Parameters.AddWithValue("$name", job.Name);
        command.Parameters.AddWithValue("$source", job.SourceFolder);
        command.Parameters.AddWithValue("$output", job.OutputPath);
        command.Parameters.AddWithValue("$status", job.Status.ToString());
        command.Parameters.AddWithValue("$plan", job.PlanJson);
        command.Parameters.AddWithValue("$completed", job.CompletedSteps);
        command.Parameters.AddWithValue("$total", job.TotalSteps);
        command.Parameters.AddWithValue("$error", (object?)job.Error ?? DBNull.Value);
        command.Parameters.AddWithValue("$created", job.CreatedAt.ToUnixTimeSeconds());
    }
}
