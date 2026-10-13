using Microsoft.Extensions.Logging;
using TutorArchier.Core;
using TutorArchier.Core.Hardware;

namespace TutorArchier.Engine.Ffmpeg;

/// <summary>Finds a working ffmpeg + ffprobe pair and reads what the build can do.</summary>
public sealed class FfmpegLocator(FfmpegRunner runner, ILogger<FfmpegLocator> logger)
{
    private static readonly string FfmpegExe = OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg";
    private static readonly string FfprobeExe = OperatingSystem.IsWindows() ? "ffprobe.exe" : "ffprobe";

    public async Task<FfmpegInfo?> LocateAsync(string? preferredDirectory, CancellationToken cancellationToken = default)
    {
        foreach (var directory in CandidateDirectories(preferredDirectory))
        {
            var info = await InspectAsync(directory, cancellationToken);
            if (info is not null)
            {
                logger.LogInformation("Using ffmpeg {Version} at {Path}", info.VersionText, info.FfmpegPath);
                return info;
            }
        }

        logger.LogWarning("ffmpeg not found (preferred directory: {Preferred})", preferredDirectory ?? "<none>");
        return null;
    }

    /// <summary>Checks one folder (or its <c>bin</c> subfolder). Null when it holds no usable ffmpeg/ffprobe pair.</summary>
    public async Task<FfmpegInfo?> InspectAsync(string directory, CancellationToken cancellationToken = default)
    {
        var ffmpeg = ResolveExecutable(directory, FfmpegExe);
        var ffprobe = ResolveExecutable(directory, FfprobeExe);
        if (ffmpeg is null || ffprobe is null)
            return null;

        var version = await runner.RunAsync(ffmpeg, ["-hide_banner", "-version"], cancellationToken: cancellationToken);
        if (!version.Success)
        {
            logger.LogWarning("ffmpeg at {Path} failed to report its version: {Error}", ffmpeg, version.StdErr);
            return null;
        }

        var filters = await runner.RunAsync(ffmpeg, ["-hide_banner", "-filters"], cancellationToken: cancellationToken);
        var filterNames = FfmpegOutputParser.ParseFilterNames(filters.StdOut);
        var (text, parsed) = FfmpegOutputParser.ParseVersion(version.StdOut);

        return new FfmpegInfo(
            ffmpeg,
            ffprobe,
            text,
            parsed,
            HasLibVmaf: filterNames.Contains("libvmaf"),
            HasSubtitlesFilter: filterNames.Contains("subtitles"),
            HasZscale: filterNames.Contains("zscale"));
    }

    /// <summary>Search order: user choice → bundled/installed copy → PATH → common install locations.</summary>
    public static IEnumerable<string> CandidateDirectories(string? preferredDirectory)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in RawCandidates(preferredDirectory))
        {
            string full;
            try
            {
                var expanded = Environment.ExpandEnvironmentVariables(raw.Trim().Trim('"'));
                if (expanded.Length == 0)
                    continue;
                full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(expanded));
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                continue;
            }

            if (seen.Add(full))
                yield return full;
        }
    }

    private static IEnumerable<string> RawCandidates(string? preferredDirectory)
    {
        if (!string.IsNullOrWhiteSpace(preferredDirectory))
        {
            yield return preferredDirectory;
            yield return Path.Combine(preferredDirectory, "bin");
        }

        yield return AppPaths.BundledFfmpegDirectory;
        yield return Path.Combine(AppPaths.BundledFfmpegDirectory, "bin");
        yield return AppPaths.UserFfmpegDirectory;

        // The process PATH can be stale (e.g. ffmpeg installed while the app was running),
        // so the persisted user and machine values are read as well.
        foreach (var target in new[] { EnvironmentVariableTarget.Process, EnvironmentVariableTarget.User, EnvironmentVariableTarget.Machine })
        {
            var path = Environment.GetEnvironmentVariable("PATH", target);
            if (string.IsNullOrEmpty(path))
                continue;
            foreach (var entry in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
                yield return entry;
        }

        if (!OperatingSystem.IsWindows())
            yield break;

        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        yield return Path.Combine(localAppData, "Microsoft", "WinGet", "Links");
        foreach (var dir in WingetFfmpegBinDirectories(Path.Combine(localAppData, "Microsoft", "WinGet", "Packages")))
            yield return dir;

        yield return @"C:\ffmpeg\bin";
        yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "ffmpeg", "bin");
        yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "scoop", "apps", "ffmpeg", "current", "bin");
        yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "chocolatey", "bin");
    }

    // winget extracts e.g. Packages\Gyan.FFmpeg_…\ffmpeg-9.0.2-full_build\bin
    private static List<string> WingetFfmpegBinDirectories(string packagesRoot)
    {
        if (!Directory.Exists(packagesRoot))
            return [];
        try
        {
            return Directory.EnumerateDirectories(packagesRoot, "*FFmpeg*")
                .SelectMany(package => Directory.EnumerateDirectories(package))
                .Select(build => Path.Combine(build, "bin"))
                .ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <summary>Returns the real file behind a symlink (winget "Links" are symlinks), or null if absent.</summary>
    private static string? ResolveExecutable(string directory, string fileName)
    {
        var path = Path.Combine(directory, fileName);
        if (!File.Exists(path))
            return null;

        try
        {
            var target = File.ResolveLinkTarget(path, returnFinalTarget: true);
            return target is { Exists: true } ? target.FullName : path;
        }
        catch (IOException)
        {
            return path;
        }
    }
}
