using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace TutorArchier.Engine.Media;

/// <param name="Language">"en", "tr", another two-letter code, or "" when the file name doesn't say.</param>
public sealed record SubtitleFile(string FullPath, string Language);

public sealed record ScannedVideo(string FullPath, string RelativePath, IReadOnlyList<SubtitleFile> Subtitles);

/// <param name="UnsupportedSubtitles">ASS/SSA and image subtitles: found, but not merged.</param>
/// <param name="OtherFiles">Everything else (PDFs, code, images…): never merged, but bundled as resources.</param>
public sealed record ScanResult(
    IReadOnlyList<ScannedVideo> Videos,
    IReadOnlyList<string> OrphanSubtitles,
    IReadOnlyList<string> UnsupportedSubtitles,
    IReadOnlyList<string> OtherFiles);

/// <summary>Finds videos and their subtitle files in a folder tree. Reads names only; never opens a file.</summary>
public static partial class FolderScanner
{
    /// <summary>Merge work folders end with this; skipped so a merge in progress never scans itself.</summary>
    public const string WorkDirectorySuffix = ".tutorarchier-parts";

    private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".m4v", ".mkv", ".webm", ".mov", ".avi", ".ts", ".flv", ".wmv", ".mpg", ".mpeg",
    };

    private static readonly HashSet<string> SubtitleExtensions = new(StringComparer.OrdinalIgnoreCase) { ".srt", ".vtt" };

    private static readonly HashSet<string> UnsupportedSubtitleExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".ass", ".ssa", ".sub", ".idx", ".sup",
    };

    public static ScanResult Scan(string root)
    {
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.Hidden | FileAttributes.System,
        };
        var files = Directory.EnumerateFiles(root, "*", options)
            .Where(path => !path.Contains(WorkDirectorySuffix + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
        return Match(root, files);
    }

    /// <summary>Pairs each subtitle with the video whose name it starts with, e.g. "01 Intro.mp4" ← "01 Intro.en.srt".</summary>
    internal static ScanResult Match(string root, IEnumerable<string> files)
    {
        var videos = new List<string>();
        var subtitles = new List<string>();
        var unsupported = new List<string>();
        var others = new List<string>();
        foreach (var path in files)
        {
            var extension = Path.GetExtension(path);
            if (VideoExtensions.Contains(extension))
                videos.Add(path);
            else if (SubtitleExtensions.Contains(extension))
                subtitles.Add(path);
            else if (UnsupportedSubtitleExtensions.Contains(extension))
                unsupported.Add(path);
            else
                others.Add(path);
        }

        var byVideo = videos.ToDictionary(v => v, _ => new List<SubtitleFile>(), StringComparer.OrdinalIgnoreCase);
        var videosByDirectory = videos.ToLookup(v => Path.GetDirectoryName(v), StringComparer.OrdinalIgnoreCase);
        var orphans = new List<string>();

        foreach (var subtitle in subtitles)
        {
            var name = Path.GetFileNameWithoutExtension(subtitle);
            var owner = videosByDirectory[Path.GetDirectoryName(subtitle)]
                .Select(video => (Video: video, Base: Path.GetFileNameWithoutExtension(video)))
                .Where(c => name.StartsWith(c.Base, StringComparison.OrdinalIgnoreCase)
                    && (name.Length == c.Base.Length || IsSeparator(name[c.Base.Length])))
                .OrderByDescending(c => c.Base.Length)
                .FirstOrDefault(); // MaxBy throws on an empty sequence of value tuples

            if (owner.Video is null)
                orphans.Add(subtitle);
            else
                byVideo[owner.Video].Add(new SubtitleFile(subtitle, DetectLanguage(name[owner.Base.Length..])));
        }

        var scanned = videos
            .Select(v => new ScannedVideo(
                v,
                Path.GetRelativePath(root, v),
                // SRT before VTT, so the plan picks SRT when a lecture ships both for one language.
                byVideo[v].OrderBy(s => Path.GetExtension(s.FullPath).Equals(".srt", StringComparison.OrdinalIgnoreCase) ? 0 : 1).ToList()))
            .OrderBy(v => v.RelativePath, ExplorerOrder.Instance)
            .ToList();

        return new ScanResult(
            scanned,
            orphans.Order(ExplorerOrder.Instance).ToList(),
            unsupported.Order(ExplorerOrder.Instance).ToList(),
            others.Order(ExplorerOrder.Instance).ToList());
    }

    /// <summary>"_en", ".English", " [tr]", "-eng-US" → language code; anything unrecognised → "".</summary>
    internal static string DetectLanguage(string suffix)
    {
        foreach (var token in LanguageTokens().Matches(suffix).Select(m => m.Value.ToLowerInvariant()))
        {
            switch (token)
            {
                case "en" or "eng" or "english" or "ingilizce":
                    return "en";
                case "tr" or "tur" or "turkish" or "turkce" or "türkçe":
                    return "tr";
                case { Length: 2 }:
                    return token;
            }
        }
        return "";
    }

    private static bool IsSeparator(char c) => c is '.' or '_' or '-' or ' ' or '[' or '(';

    [GeneratedRegex(@"\p{L}+")]
    private static partial Regex LanguageTokens();

    /// <summary>Windows Explorer's ordering: "Lecture 2" before "Lecture 10", case-insensitive.</summary>
    public sealed class ExplorerOrder : IComparer<string>
    {
        public static ExplorerOrder Instance { get; } = new();

        public int Compare(string? x, string? y) => StrCmpLogicalW(x ?? "", y ?? "");

#pragma warning disable SYSLIB1054 // DllImport keeps this file free of unsafe code / source-generated marshalling.
        [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)]
        private static extern int StrCmpLogicalW(string x, string y);
#pragma warning restore SYSLIB1054
    }
}
