using Microsoft.Data.Sqlite;
using TutorArchier.Core.Hardware;
using TutorArchier.Core.Settings;
using TutorArchier.Data;

namespace TutorArchier.Tests.Data;

public sealed class SqliteStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "TutorArchier.Tests", Guid.NewGuid().ToString("N"));
    private readonly AppDatabase _database;

    public SqliteStoreTests()
    {
        _database = new AppDatabase(Path.Combine(_directory, "test.db"));
        _database.Initialize();
    }

    public void Dispose()
    {
        // Pooled connections keep the file open on Windows.
        SqliteConnection.ClearAllPools();
        Directory.Delete(_directory, recursive: true);
    }

    [Fact]
    public void Initialize_is_idempotent_and_sets_the_schema_version()
    {
        _database.Initialize();

        using var connection = _database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA user_version;";
        Assert.Equal((long)AppDatabase.SchemaVersion, command.ExecuteScalar());
    }

    [Fact]
    public void Missing_settings_return_their_default()
    {
        var store = new SqliteSettingsStore(_database);

        Assert.Equal(AppTheme.System, store.Get(AppSettingKeys.Theme));
        Assert.False(store.Get(AppSettingKeys.OnboardingCompleted));
        Assert.Null(store.Get(AppSettingKeys.FfmpegDirectory));
    }

    [Fact]
    public void Settings_round_trip_and_survive_a_new_store_instance()
    {
        var store = new SqliteSettingsStore(_database);
        store.Set(AppSettingKeys.Theme, AppTheme.Light);
        store.Set(AppSettingKeys.OnboardingCompleted, true);
        store.Set(AppSettingKeys.FfmpegDirectory, @"D:\Araçlar\ffmpeg\bin");

        var reopened = new SqliteSettingsStore(_database);

        Assert.Equal(AppTheme.Light, reopened.Get(AppSettingKeys.Theme));
        Assert.True(reopened.Get(AppSettingKeys.OnboardingCompleted));
        Assert.Equal(@"D:\Araçlar\ffmpeg\bin", reopened.Get(AppSettingKeys.FfmpegDirectory));
    }

    [Fact]
    public void Setting_null_clears_back_to_default()
    {
        var store = new SqliteSettingsStore(_database);
        store.Set(AppSettingKeys.FfmpegDirectory, @"C:\ffmpeg");
        store.Set(AppSettingKeys.FfmpegDirectory, null);

        Assert.Null(new SqliteSettingsStore(_database).Get(AppSettingKeys.FfmpegDirectory));
    }

    [Fact]
    public void Unparseable_setting_falls_back_to_default()
    {
        using (var connection = _database.Open())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "INSERT INTO settings (key, value) VALUES ('appearance.theme', '\"Sepia\"');";
            command.ExecuteNonQuery();
        }

        Assert.Equal(AppTheme.System, new SqliteSettingsStore(_database).Get(AppSettingKeys.Theme));
    }

    [Fact]
    public void Hardware_report_round_trips()
    {
        var store = new SqliteHardwareReportStore(_database);
        Assert.Null(store.Load());

        var report = new HardwareReport(
            "ABCDEF0123456789",
            new DateTimeOffset(2026, 10, 10, 14, 30, 0, TimeSpan.FromHours(3)),
            new FfmpegInfo(@"C:\ffmpeg\ffmpeg.exe", @"C:\ffmpeg\ffprobe.exe", "9.0.2-full_build", new Version(9, 0, 2), true, true, false),
            "AMD Ryzen 5 4600H",
            [new GpuInfo("AMD Radeon(TM) Graphics", "27.20.11022.14001", GpuVendor.Amd)],
            [
                new EncoderCapability("hevc_amf", true, false, false, TenBitFailureReason: "AMD sürücüsü 10-bit için eski", BenchmarkFps: 212.5),
                new EncoderCapability("h264_nvenc", false, false, false, "NVIDIA ekran kartı bulunamadı", "Cannot load nvcuda.dll"),
            ],
            BenchmarkedAt: null);

        store.Save(report);
        store.Save(report with { CpuName = "updated" });
        var loaded = store.Load()!;

        Assert.Equal("updated", loaded.CpuName);
        Assert.Equal(report.Fingerprint, loaded.Fingerprint);
        Assert.Equal(report.TestedAt, loaded.TestedAt);
        Assert.Equal(report.Ffmpeg, loaded.Ffmpeg);
        Assert.Equal(report.Gpus, loaded.Gpus);
        Assert.Equal(report.Encoders, loaded.Encoders);
        Assert.Null(loaded.BenchmarkedAt);
    }
}
