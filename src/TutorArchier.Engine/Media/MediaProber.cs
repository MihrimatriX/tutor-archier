using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using TutorArchier.Core.Media;
using TutorArchier.Engine.Ffmpeg;

namespace TutorArchier.Engine.Media;

/// <summary>Reads container and stream facts with ffprobe; results are cached until the file changes.</summary>
public sealed class MediaProber(FfmpegRunner runner, IProbeCache cache, ILogger<MediaProber> logger)
{
    /// <returns>The media info, or a short reason why the file couldn't be read.</returns>
    public async Task<(MediaInfo? Info, string? Error)> ProbeAsync(string ffprobePath, FileInfo file, CancellationToken cancellationToken = default)
    {
        var ticks = file.LastWriteTimeUtc.Ticks;
        if (cache.Get(file.FullName, file.Length, ticks) is { } cached)
            return (cached, null);

        var result = await runner.RunAsync(
            ffprobePath,
            ["-v", "error", "-print_format", "json", "-show_format", "-show_streams", file.FullName],
            cancellationToken: cancellationToken);

        var info = result.Success ? Parse(result.StdOut, file.Length) : null;
        if (info is null)
        {
            var reason = result.StdErr.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault();
            // ffprobe prefixes errors with the path, which the UI already shows next to the message.
            if (reason?.StartsWith(file.FullName + ":", StringComparison.OrdinalIgnoreCase) == true)
                reason = reason[(file.FullName.Length + 1)..].Trim();
            logger.LogInformation("ffprobe could not read {Path}: {Reason}", file.FullName, reason);
            return (null, reason ?? "ffprobe çıktısı okunamadı");
        }

        cache.Set(file.FullName, file.Length, ticks, info);
        return (info, null);
    }

    internal static MediaInfo? Parse(string json, long sizeBytes)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (!root.TryGetProperty("format", out var format))
                return null;

            VideoStreamInfo? video = null;
            AudioStreamInfo? audio = null;
            var subtitles = 0;
            var longestStream = 0d;

            var streams = root.TryGetProperty("streams", out var list) ? list.EnumerateArray().ToList() : [];
            foreach (var stream in streams)
            {
                longestStream = Math.Max(longestStream, Number(stream, "duration"));
                var index = (int)Number(stream, "index");
                switch (Text(stream, "codec_type"))
                {
                    // Cover art shows up as a one-frame "video" stream; it isn't the lecture.
                    case "video" when video is null && !IsAttachedPicture(stream):
                        video = new VideoStreamInfo(
                            index,
                            Text(stream, "codec_name") ?? "?",
                            (int)Number(stream, "width"),
                            (int)Number(stream, "height"),
                            Text(stream, "pix_fmt") ?? "?",
                            Rate(Text(stream, "avg_frame_rate")) is > 0 and var avg ? avg : Rate(Text(stream, "r_frame_rate")),
                            Text(stream, "time_base") ?? "?");
                        break;
                    case "audio" when audio is null:
                        audio = new AudioStreamInfo(
                            index,
                            Text(stream, "codec_name") ?? "?",
                            (int)Number(stream, "sample_rate"),
                            (int)Number(stream, "channels"),
                            Text(stream, "time_base") ?? "?");
                        break;
                    case "subtitle":
                        subtitles++;
                        break;
                }
            }

            var seconds = Number(format, "duration") is > 0 and var d ? d : longestStream;
            return new MediaInfo(
                TimeSpan.FromSeconds(seconds),
                sizeBytes,
                Text(format, "format_name") ?? "?",
                video,
                audio,
                subtitles);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool IsAttachedPicture(JsonElement stream) =>
        stream.TryGetProperty("disposition", out var disposition) && Number(disposition, "attached_pic") == 1;

    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    /// <summary>ffprobe writes most numbers as strings ("duration": "12.345"), a few as numbers.</summary>
    private static double Number(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value))
            return 0;
        return value.ValueKind switch
        {
            JsonValueKind.Number => value.GetDouble(),
            JsonValueKind.String when double.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) => parsed,
            _ => 0,
        };
    }

    /// <summary>"30000/1001" → 29.97; "0/0" → 0.</summary>
    private static double Rate(string? fraction)
    {
        var parts = fraction?.Split('/');
        if (parts is not [var num, var den]
            || !double.TryParse(num, NumberStyles.Float, CultureInfo.InvariantCulture, out var n)
            || !double.TryParse(den, NumberStyles.Float, CultureInfo.InvariantCulture, out var d)
            || d == 0)
            return 0;
        return n / d;
    }
}
