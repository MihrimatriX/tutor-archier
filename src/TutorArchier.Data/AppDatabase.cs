using System.Globalization;
using Microsoft.Data.Sqlite;

namespace TutorArchier.Data;

/// <summary>
/// The single SQLite file behind settings and caches. Schema changes are append-only
/// migrations tracked with <c>PRAGMA user_version</c>.
/// </summary>
public sealed class AppDatabase
{
    // Index + 1 is the schema version after the step runs. Never edit a shipped step; append a new one.
    private static readonly string[] Migrations =
    [
        """
        CREATE TABLE settings (
            key   TEXT PRIMARY KEY,
            value TEXT NOT NULL
        );
        CREATE TABLE hardware_report (
            id         INTEGER PRIMARY KEY CHECK (id = 1),
            json       TEXT    NOT NULL,
            updated_at INTEGER NOT NULL
        );
        """,
        // Phase 1 (analysis) and phase 2 (merge queue). Step progress is a counter on the job:
        // steps are derived from the stored plan, so a separate job_steps table would only mirror it.
        """
        CREATE TABLE probe_cache (
            path        TEXT    PRIMARY KEY,
            size        INTEGER NOT NULL,
            write_ticks INTEGER NOT NULL,
            json        TEXT    NOT NULL
        );
        CREATE TABLE jobs (
            id              INTEGER PRIMARY KEY AUTOINCREMENT,
            name            TEXT    NOT NULL,
            source_folder   TEXT    NOT NULL,
            output_path     TEXT    NOT NULL,
            status          TEXT    NOT NULL,
            plan_json       TEXT    NOT NULL,
            completed_steps INTEGER NOT NULL,
            total_steps     INTEGER NOT NULL,
            error           TEXT,
            created_at      INTEGER NOT NULL
        );
        """,
    ];

    private readonly string _connectionString;

    public AppDatabase(string path)
    {
        Path = path;
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Private,
            Pooling = true,
        }.ToString();
    }

    public string Path { get; }

    public static int SchemaVersion => Migrations.Length;

    public void Initialize()
    {
        var directory = System.IO.Path.GetDirectoryName(Path);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        using var connection = Open();
        Execute(connection, "PRAGMA journal_mode = WAL;");

        var current = Convert.ToInt32(Scalar(connection, "PRAGMA user_version;"), CultureInfo.InvariantCulture);
        for (var version = current; version < Migrations.Length; version++)
        {
            using var transaction = connection.BeginTransaction();
            Execute(connection, Migrations[version], transaction);
            // PRAGMA does not accept parameters; the value is our own integer.
            Execute(connection, $"PRAGMA user_version = {version + 1};", transaction);
            transaction.Commit();
        }
    }

    public SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        return connection;
    }

    private static void Execute(SqliteConnection connection, string sql, SqliteTransaction? transaction = null)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Transaction = transaction;
        command.ExecuteNonQuery();
    }

    private static object? Scalar(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar();
    }
}
