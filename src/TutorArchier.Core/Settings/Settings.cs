namespace TutorArchier.Core.Settings;

public enum AppTheme
{
    System,
    Dark,
    Light,
}

/// <summary>A typed settings entry. <paramref name="Name"/> is the row key in the settings table.</summary>
public sealed record SettingKey<T>(string Name, T Default);

/// <summary>A course opened before, as shown on Home. <paramref name="Cover"/> is a lecture whose still stands for it.</summary>
public sealed record RecentFolder(string Path, string Name, int Lectures, TimeSpan Duration, long SizeBytes, string? Cover, DateTimeOffset OpenedAt);

public static class AppSettingKeys
{
    /// <summary>Newest first.</summary>
    public static readonly SettingKey<IReadOnlyList<RecentFolder>> RecentFolders = new("workspace.recentFolders", []);

    /// <summary>User-chosen folder containing ffmpeg.exe; null means auto-detect.</summary>
    public static readonly SettingKey<string?> FfmpegDirectory = new("ffmpeg.directory", null);

    public static readonly SettingKey<AppTheme> Theme = new("appearance.theme", AppTheme.System);

    public static readonly SettingKey<bool> OnboardingCompleted = new("onboarding.completed", false);

    /// <summary>
    /// Draw the UI with the GPU. Off by default: some drivers present nothing for WPF's Direct3D path.
    /// Unrelated to video encoding. Takes effect on restart.
    /// </summary>
    public static readonly SettingKey<bool> HardwareAcceleratedUi = new("appearance.hardwareAcceleratedUi", false);
}

public interface ISettingsStore
{
    T Get<T>(SettingKey<T> key);

    void Set<T>(SettingKey<T> key, T value);
}
