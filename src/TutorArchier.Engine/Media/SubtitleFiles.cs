using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace TutorArchier.Engine.Media;

public sealed record SubtitleCue(TimeSpan Start, TimeSpan End, string Text);

/// <summary>Reads SRT/VTT cues and writes SRT. Only timing and text survive; styling beyond i/b/u is dropped.</summary>
public static partial class SubtitleFiles
{
    private static readonly Encoding StrictUtf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    private static readonly Encoding Turkish = LoadCodePage(1254);

    public static IReadOnlyList<SubtitleCue> Read(string path) => Parse(Decode(File.ReadAllBytes(path)));

    public static void WriteSrt(string path, IEnumerable<SubtitleCue> cues)
    {
        var builder = new StringBuilder();
        var number = 1;
        foreach (var cue in cues)
        {
            builder.Append(number++).Append('\n')
                .Append(Timestamp(cue.Start)).Append(" --> ").Append(Timestamp(cue.End)).Append('\n')
                .Append(cue.Text).Append("\n\n");
        }
        File.WriteAllText(path, builder.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    /// <summary>
    /// UTF-8 when the bytes are valid UTF-8, otherwise Windows-1254: Turkish subtitles made with older
    /// tools are almost always in that code page, and reading them as UTF-8 would mangle ş, ğ, ı.
    /// </summary>
    internal static string Decode(byte[] bytes)
    {
        try
        {
            return StrictUtf8.GetString(bytes).TrimStart('﻿');
        }
        catch (DecoderFallbackException)
        {
            return Turkish.GetString(bytes);
        }
    }

    private static Encoding LoadCodePage(int codePage)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        return Encoding.GetEncoding(codePage);
    }

    internal static IReadOnlyList<SubtitleCue> Parse(string text)
    {
        var cues = new List<SubtitleCue>();
        foreach (var block in BlankLines().Split(text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n')))
        {
            var lines = block.Split('\n');
            var timingLine = Array.FindIndex(lines, line => line.Contains("-->", StringComparison.Ordinal));
            if (timingLine < 0 || Timing().Match(lines[timingLine]) is not { Success: true } timing)
                continue; // WEBVTT header, NOTE/STYLE blocks, garbage

            var body = Tags().Replace(string.Join('\n', lines.Skip(timingLine + 1)).Trim(), "");
            if (body.Length == 0)
                continue;

            var start = Parse(timing, 1);
            var end = Parse(timing, 5);
            if (end > start)
                cues.Add(new SubtitleCue(start, end, body));
        }
        return cues;
    }

    private static TimeSpan Parse(Match match, int first)
    {
        int Group(int offset) => match.Groups[first + offset].Success
            ? int.Parse(match.Groups[first + offset].Value, CultureInfo.InvariantCulture)
            : 0;
        return new TimeSpan(0, Group(0), Group(1), Group(2), Group(3));
    }

    private static string Timestamp(TimeSpan time) =>
        string.Create(CultureInfo.InvariantCulture, $"{(int)time.TotalHours:00}:{time.Minutes:00}:{time.Seconds:00},{time.Milliseconds:000}");

    [GeneratedRegex(@"\n[ \t]*\n+")]
    private static partial Regex BlankLines();

    // Hours are optional in VTT ("01:02.500"); SRT uses a comma, VTT a dot.
    [GeneratedRegex(@"(?:(\d+):)?(\d{1,2}):(\d{2})[,.](\d{3})\s*-->\s*(?:(\d+):)?(\d{1,2}):(\d{2})[,.](\d{3})")]
    private static partial Regex Timing();

    // VTT voice/class/timestamp tags would show up literally in SRT players; keep only i, b, u.
    [GeneratedRegex(@"<(?!/?[ibu]>)[^>\n]*>")]
    private static partial Regex Tags();
}
