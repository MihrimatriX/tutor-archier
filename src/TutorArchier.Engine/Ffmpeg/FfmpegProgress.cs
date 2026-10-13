using System.Globalization;

namespace TutorArchier.Engine.Ffmpeg;

/// <summary>One block of ffmpeg's <c>-progress</c> output.</summary>
/// <param name="Speed">Realtime multiplier (<c>speed=2.5x</c>); null while ffmpeg reports N/A.</param>
public sealed record FfmpegProgress(
    long Frame,
    double Fps,
    TimeSpan OutTime,
    long TotalSizeBytes,
    double? Speed,
    bool IsEnd);

/// <summary>
/// Accumulates <c>key=value</c> lines until the <c>progress=continue|end</c> line that closes a block.
/// Values are machine-formatted, so parsing is culture-invariant (the UI runs under tr-TR).
/// </summary>
public sealed class FfmpegProgressParser
{
    private readonly Dictionary<string, string> _values = new(StringComparer.Ordinal);

    public FfmpegProgress? Feed(string line)
    {
        var separator = line.IndexOf('=', StringComparison.Ordinal);
        if (separator <= 0)
            return null;

        var key = line[..separator].Trim();
        var value = line[(separator + 1)..].Trim();

        if (key != "progress")
        {
            _values[key] = value;
            return null;
        }

        var progress = new FfmpegProgress(
            Frame: ReadLong("frame"),
            Fps: ReadDouble("fps") ?? 0,
            OutTime: TimeSpan.FromMicroseconds(Math.Max(0, ReadLong("out_time_us"))),
            TotalSizeBytes: ReadLong("total_size"),
            Speed: ReadDouble("speed", trimSuffix: 'x'),
            IsEnd: value == "end");

        _values.Clear();
        return progress;
    }

    private long ReadLong(string key) =>
        _values.TryGetValue(key, out var raw)
        && long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? value
            : 0;

    private double? ReadDouble(string key, char? trimSuffix = null)
    {
        if (!_values.TryGetValue(key, out var raw))
            return null;
        if (trimSuffix is { } suffix)
            raw = raw.TrimEnd(suffix);
        return double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            ? value
            : null;
    }
}
