namespace TutorArchier.Core;

/// <summary>Well-known per-user locations. Nothing here ever points inside a user's media folders.</summary>
public static class AppPaths
{
    public static string DataDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TutorArchier");

    public static string DatabasePath => Path.Combine(DataDirectory, "tutorarchier.db");

    public static string LogsDirectory => Path.Combine(DataDirectory, "logs");

    /// <summary>ffmpeg shipped or installed next to the app; preferred install target.</summary>
    public static string BundledFfmpegDirectory => Path.Combine(AppContext.BaseDirectory, "ffmpeg");

    /// <summary>Install target when the app folder is read-only (e.g. under Program Files).</summary>
    public static string UserFfmpegDirectory => Path.Combine(DataDirectory, "ffmpeg");

    /// <summary>Scratch space for short-lived work (encoder probes, benchmarks). Job temp lives next to the output instead.</summary>
    public static string TempRoot => Path.Combine(Path.GetTempPath(), "TutorArchier");

    public static string CreateScratchDirectory(string prefix)
    {
        var dir = Path.Combine(TempRoot, $"{prefix}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        return dir;
    }

    public static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch (IOException)
        {
            // Best effort: a lingering ffmpeg handle must not fail the caller.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
