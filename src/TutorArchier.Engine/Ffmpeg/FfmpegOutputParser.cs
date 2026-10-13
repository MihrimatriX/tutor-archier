using System.Globalization;
using System.Text.RegularExpressions;

namespace TutorArchier.Engine.Ffmpeg;

/// <summary>Parsers for ffmpeg's informational listings (<c>-version</c>, <c>-filters</c>, <c>-encoders</c>).</summary>
public static partial class FfmpegOutputParser
{
    /// <summary>
    /// "ffmpeg version 9.0.2-full_build-www.gyan.dev Copyright ..." → ("9.0.2-full_build-www.gyan.dev", 9.0.2).
    /// Git snapshots ("N-121000-g…", "2026-09-01-git-…") yield a null <see cref="Version"/>.
    /// </summary>
    public static (string Text, Version? Version) ParseVersion(string output)
    {
        var match = VersionLine().Match(output);
        if (!match.Success)
            return ("", null);

        var text = match.Groups[1].Value;
        var numeric = NumericVersion().Match(text);
        if (!numeric.Success)
            return (text, null);

        var major = int.Parse(numeric.Groups[1].Value, CultureInfo.InvariantCulture);
        var minor = int.Parse(numeric.Groups[2].Value, CultureInfo.InvariantCulture);
        var patch = numeric.Groups[3].Success ? int.Parse(numeric.Groups[3].Value, CultureInfo.InvariantCulture) : 0;
        return (text, new Version(major, minor, patch));
    }

    /// <summary>Names from <c>ffmpeg -filters</c>, e.g. " .. libvmaf  VV->V  Calculate the VMAF…".</summary>
    public static IReadOnlySet<string> ParseFilterNames(string output) =>
        CollectNames(output, FilterLine());

    /// <summary>Names from <c>ffmpeg -encoders</c>, e.g. " V....D libx264  libx264 H.264…".</summary>
    public static IReadOnlySet<string> ParseEncoderNames(string output) =>
        CollectNames(output, EncoderLine());

    private static HashSet<string> CollectNames(string output, Regex pattern)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var line in output.Split('\n'))
        {
            var match = pattern.Match(line);
            if (match.Success)
                names.Add(match.Groups[1].Value);
        }
        return names;
    }

    [GeneratedRegex(@"ffmpeg version (\S+)")]
    private static partial Regex VersionLine();

    [GeneratedRegex(@"^n?(\d+)\.(\d+)(?:\.(\d+))?")]
    private static partial Regex NumericVersion();

    // Flag column is 2 chars in current builds (".S"), 3 in older ones ("T.C").
    [GeneratedRegex(@"^\s*[.A-Z]{2,3}\s+([A-Za-z0-9_]+)\s+\S*->\S*")]
    private static partial Regex FilterLine();

    // The legend lines (" V..... = Video") don't match because "=" isn't a valid name.
    [GeneratedRegex(@"^\s*[VAS][.A-Z]{5}\s+([A-Za-z0-9_\-]+)\s")]
    private static partial Regex EncoderLine();
}
