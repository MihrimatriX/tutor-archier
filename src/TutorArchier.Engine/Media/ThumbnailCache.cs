using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using TutorArchier.Core;
using TutorArchier.Core.Hardware;
using TutorArchier.Engine.Ffmpeg;

namespace TutorArchier.Engine.Media;

/// <summary>
/// One still per video for lists and cards, grabbed by ffmpeg and kept as a small JPEG until the video changes.
/// ponytail: never evicted; ~15 KB each, so a thousand lectures stay around 15 MB.
/// </summary>
public sealed class ThumbnailCache(MediaProber prober, FfmpegRunner runner, ILogger<ThumbnailCache> logger) : IDisposable
{
    public const int Width = 320;

    // A few at a time: stills share the disk with the analysis and any running job.
    private readonly SemaphoreSlim _gate = new(3);
    private readonly ConcurrentDictionary<string, Task<string?>> _pending = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Where the stills live; tests point it at a scratch folder.</summary>
    public string Directory { get; init; } = Path.Combine(AppPaths.DataDirectory, "thumbs");

    /// <returns>The JPEG's path, or null when the video has no frame to show.</returns>
    public Task<string?> GetAsync(FfmpegInfo ffmpeg, string videoPath)
    {
        var file = new FileInfo(videoPath);
        if (!file.Exists)
            return Task.FromResult<string?>(null);

        var target = Path.Combine(Directory, Key(file) + ".jpg");
        if (File.Exists(target))
            return Task.FromResult<string?>(target);

        // Rows scrolling back into view ask again while the first request still runs.
        return _pending.GetOrAdd(target, async _ =>
        {
            try
            {
                return await CreateAsync(ffmpeg, file, target);
            }
            finally
            {
                _pending.TryRemove(target, out Task<string?>? _);
            }
        });
    }

    /// <summary>Where in a lecture its still comes from.</summary>
    internal static TimeSpan StillTime(TimeSpan duration) => duration * 0.25;

    private async Task<string?> CreateAsync(FfmpegInfo ffmpeg, FileInfo file, string target)
    {
        await _gate.WaitAsync();
        try
        {
            System.IO.Directory.CreateDirectory(Directory);
            var (info, _) = await prober.ProbeAsync(ffmpeg.FfprobePath, file);
            if (info?.Video is null)
                return null;

            // Very short clips can have no keyframe after the seek point; the first frame is the fallback.
            foreach (var at in new[] { StillTime(info.Duration), TimeSpan.Zero }.Distinct())
            {
                var partial = target + ".partial";
                var result = await runner.RunAsync(
                    ffmpeg.FfmpegPath,
                    [
                        "-hide_banner", "-v", "error", "-y",
                        // Nearest keyframe is close enough for a still and skips decoding up to the exact time.
                        "-noaccurate_seek", "-ss", at.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture),
                        "-i", file.FullName,
                        "-map", "0:V:0", "-frames:v", "1", "-vf", $"scale={Width}:-2", "-q:v", "5",
                        "-c:v", "mjpeg", "-f", "mjpeg", partial,
                    ],
                    new FfmpegRunOptions { LowPriority = true });
                if (result.Success && new FileInfo(partial) is { Exists: true, Length: > 0 })
                {
                    File.Move(partial, target, overwrite: true);
                    return target;
                }
                File.Delete(partial);
            }

            logger.LogDebug("No still for {Path}", file.FullName);
            return null;
        }
        catch (IOException ex)
        {
            logger.LogDebug(ex, "Still for {Path} not written", file.FullName);
            return null;
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose() => _gate.Dispose();

    /// <summary>Same file, same size, same write time → same still.</summary>
    private static string Key(FileInfo file) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(
            string.Create(CultureInfo.InvariantCulture, $"{file.FullName.ToUpperInvariant()}|{file.Length}|{file.LastWriteTimeUtc.Ticks}"))))[..24];
}
