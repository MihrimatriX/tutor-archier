using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using TutorArchier.Core.Hardware;
using TutorArchier.Core.Media;
using TutorArchier.Engine.Hardware;
using TutorArchier.Engine.Media;

namespace TutorArchier.Engine.Merge;

/// <summary>
/// The stream parameters that must agree for ffmpeg's concat demuxer to join files without re-encoding.
/// Time bases are included because the demuxer reads every file's timestamps in the first file's time base.
/// </summary>
public sealed record FormatSignature(
    string VideoCodec, int Width, int Height, string PixelFormat, string VideoTimeBase,
    string? AudioCodec, int SampleRate, int Channels, string? AudioTimeBase)
{
    public static FormatSignature Of(MediaInfo info) => new(
        info.Video!.Codec, info.Video.Width, info.Video.Height, info.Video.PixelFormat, info.Video.TimeBase,
        info.Audio?.Codec, info.Audio?.SampleRate ?? 0, info.Audio?.Channels ?? 0, info.Audio?.TimeBase);

    public bool VideoMatches(FormatSignature other) =>
        VideoCodec == other.VideoCodec && Width == other.Width && Height == other.Height && PixelFormat == other.PixelFormat;

    public bool AudioMatches(FormatSignature other) =>
        AudioCodec == other.AudioCodec && SampleRate == other.SampleRate && Channels == other.Channels;

    public bool TimeBaseMatches(FormatSignature other) =>
        VideoTimeBase == other.VideoTimeBase && AudioTimeBase == other.AudioTimeBase;
}

public enum SegmentAction
{
    /// <summary>Joined as is.</summary>
    Copy,

    /// <summary>Same codecs, re-wrapped so streams line up by index and share the target time base. No quality change.</summary>
    Remux,

    /// <summary>Video copied, audio converted (or silence added / audio dropped) to match.</summary>
    EncodeAudio,

    /// <summary>Video re-encoded to the target format: at transparent quality, or the searched one in archive mode.</summary>
    EncodeVideo,
}

/// <param name="EncodedPath">Where the prepared file lands in the work folder; null for <see cref="SegmentAction.Copy"/>.</param>
/// <param name="Arguments">Full ffmpeg arguments that write <c>EncodedPath + ".partial"</c>.</param>
/// <param name="EstimatedSeconds">Preparation time from the benchmark; null when no benchmark was run.</param>
public sealed record MergeSegment(
    string SourcePath,
    string Title,
    TimeSpan Duration,
    SegmentAction Action,
    string? EncodedPath,
    IReadOnlyList<string>? Arguments,
    double? EstimatedSeconds)
{
    [JsonIgnore]
    public string ConcatPath => EncodedPath ?? SourcePath;
}

/// <param name="Files">One entry per segment, null where that lecture has no subtitle in this language.</param>
public sealed record SubtitleTrack(string Language, string Title, IReadOnlyList<string?> Files);

/// <summary>Course files the MKVs don't carry (PDFs, code, unreadable videos…), zipped next to the output.</summary>
/// <param name="Root">Entry names are relative to this, so the zip keeps the course's folder layout.</param>
/// <param name="Bytes">Total size at planning time; only used for progress.</param>
public sealed record ResourceBundle(string ArchivePath, string Root, IReadOnlyList<string> Files, long Bytes);

/// <summary>Everything a merge job runs, decided up front so a resumed job does exactly what was queued.</summary>
/// <param name="ArchiveQuality">Set in archive mode: the CQ/CRF the VMAF search picked for re-encoding everything.</param>
/// <param name="Resources">Zipped as a step of its own, alongside the segment preparation.</param>
public sealed record MergePlan(
    string SourceFolder,
    string OutputPath,
    string Title,
    FormatSignature Target,
    string? EncoderId,
    IReadOnlyList<MergeSegment> Segments,
    IReadOnlyList<SubtitleTrack> SubtitleTracks,
    IReadOnlyList<string> Skipped,
    int? ArchiveQuality = null,
    ResourceBundle? Resources = null)
{
    private static readonly JsonSerializerOptions Json = new() { Converters = { new JsonStringEnumConverter() } };

    [JsonIgnore]
    public string WorkDirectory => OutputPath + FolderScanner.WorkDirectorySuffix;

    [JsonIgnore]
    public TimeSpan TotalDuration => Segments.Aggregate(TimeSpan.Zero, (sum, s) => sum + s.Duration);

    /// <summary>Segments that need a preparation step before the final join, in order.</summary>
    [JsonIgnore]
    public IReadOnlyList<MergeSegment> Prepared => Segments.Where(s => s.Action != SegmentAction.Copy).ToList();

    /// <summary>One step per prepared segment, one for the resource zip, plus the final join.</summary>
    [JsonIgnore]
    public int StepCount => Prepared.Count + (Resources is null ? 0 : 1) + 1;

    public string ToJson() => JsonSerializer.Serialize(this, Json);

    public static MergePlan FromJson(string json) =>
        JsonSerializer.Deserialize<MergePlan>(json, Json) ?? throw new JsonException("Empty merge plan");
}

public static class MergePlanner
{
    private const string MatroskaTimeBase = "1/1000";
    private const string ArchiveCodec = "hevc";
    private const string ArchivePixelFormat = "yuv420p10le";

    private static readonly HashSet<string> Mp4Audio = new(StringComparer.Ordinal) { "aac", "mp3", "ac3", "eac3", "opus", "flac" };

    private static readonly HashSet<string> Mp4Video = new(StringComparer.Ordinal) { "h264", "hevc", "av1", "vp9", "mpeg4" };

    private static readonly Dictionary<string, Func<int, string[]>> AudioEncoders = new(StringComparer.Ordinal)
    {
        ["aac"] = ch => ["aac", "-b:a", ch <= 2 ? "192k" : "384k"],
        ["mp3"] = _ => ["libmp3lame", "-b:a", "192k"],
        ["opus"] = ch => ["libopus", "-b:a", ch <= 2 ? "160k" : "320k"],
        ["ac3"] = ch => ["ac3", "-b:a", ch <= 2 ? "192k" : "448k"],
        ["eac3"] = ch => ["eac3", "-b:a", ch <= 2 ? "192k" : "448k"],
        ["vorbis"] = _ => ["libvorbis", "-q:a", "6"],
        ["flac"] = _ => ["flac"],
        ["pcm_s16le"] = _ => ["pcm_s16le"],
    };

    /// <summary>Joined MP4s play almost anywhere (phones, TVs, browsers); MKV stays the default because it carries anything.</summary>
    public static bool IsMp4(string outputPath) => Path.GetExtension(outputPath).Equals(".mp4", StringComparison.OrdinalIgnoreCase);

    /// <summary>"…\Course" → "…\Course.mkv" (or .mp4) next to the folder, never overwriting an existing file.</summary>
    public static string DefaultOutputPath(string folder, bool mp4 = false)
    {
        var (parent, name) = ParentAndName(folder);
        return UniqueFile(parent, name, mp4 ? ".mp4" : ".mkv");
    }

    /// <summary>
    /// Everything in the course the MKVs leave out: non-media files, subtitles nothing matched or that can't be
    /// merged, and unreadable videos with their subtitles. Zipped into "resource\&lt;Course&gt;.zip" under
    /// <paramref name="outputDirectory"/>; null when there's nothing to keep.
    /// </summary>
    public static ResourceBundle? Bundle(FolderAnalysis analysis, string outputDirectory)
    {
        var files = analysis.OtherFiles
            .Concat(analysis.OrphanSubtitles)
            .Concat(analysis.UnsupportedSubtitles)
            .Concat(analysis.Videos.Where(v => !v.IsUsable).SelectMany(v => v.File.Subtitles.Select(s => s.FullPath).Prepend(v.File.FullPath)))
            .Order(FolderScanner.ExplorerOrder.Instance)
            .ToList();
        if (files.Count == 0)
            return null;

        var bytes = files.Sum(f => new FileInfo(f) is { Exists: true } info ? info.Length : 0);
        var archive = UniqueFile(Path.Combine(outputDirectory, "resource"), ParentAndName(analysis.Folder).Name, ".zip");
        return new ResourceBundle(archive, analysis.Folder, files, bytes);
    }

    private static string UniqueFile(string directory, string name, string extension)
    {
        var path = Path.Combine(directory, name + extension);
        for (var i = 2; File.Exists(path); i++)
            path = Path.Combine(directory, $"{name} ({i}){extension}");
        return path;
    }

    /// <summary>"…\Course" → "…\Course (MKV)" (or MP4) next to the folder, for one video per subfolder; never an existing folder.</summary>
    public static string DefaultOutputFolder(string folder, bool mp4 = false)
    {
        var (parent, name) = ParentAndName(folder);
        var format = mp4 ? "MP4" : "MKV";
        var path = Path.Combine(parent, $"{name} ({format})");
        for (var i = 2; Directory.Exists(path); i++)
            path = Path.Combine(parent, $"{name} ({format} {i})");
        return path;
    }

    /// <summary>
    /// One analysis per top-level subfolder (deeper folders stay inside and show up in chapter titles), plus one for
    /// videos lying directly in the course folder. Paths are rebased so chapters don't repeat the subfolder's name.
    /// Subfolders with nothing readable are left out.
    /// </summary>
    public static IReadOnlyList<FolderAnalysis> Sections(FolderAnalysis analysis) =>
        analysis.Videos
            .GroupBy(v => Path.GetDirectoryName(v.File.RelativePath) is { Length: > 0 } dir ? dir.Split(Path.DirectorySeparatorChar)[0] : "")
            .Where(group => group.Any(v => v.IsUsable))
            .Select(group =>
            {
                var folder = group.Key.Length == 0 ? analysis.Folder : Path.Combine(analysis.Folder, group.Key);
                var videos = group.Select(v => v with { File = v.File with { RelativePath = Path.GetRelativePath(folder, v.File.FullPath) } });
                return new FolderAnalysis(folder, videos.ToList(), [], [], []);
            })
            .ToList();

    /// <summary>
    /// "Each subfolder its own video": one plan per <see cref="Sections"/> entry, written as
    /// "&lt;subfolder&gt;.mkv" (or .mp4) into <paramref name="outputFolder"/>; loose videos take the course's name.
    /// </summary>
    public static IReadOnlyList<MergePlan> CreatePerSubfolder(FolderAnalysis analysis, string outputFolder, HardwareReport? hardware, int? archiveQuality = null, bool mp4 = false)
    {
        var course = ParentAndName(analysis.Folder).Name;
        return Sections(analysis)
            .Select(section =>
            {
                var name = section.Folder == analysis.Folder ? course : Path.GetFileName(section.Folder);
                var plan = Create(section, Path.Combine(outputFolder, name + (mp4 ? ".mp4" : ".mkv")), hardware, archiveQuality);
                // The title is the video's and the queue's name; keep the course in it so sections stay recognisable.
                return name == course ? plan : plan with { Title = $"{course} › {name}" };
            })
            .ToList();
    }

    private static (string Parent, string Name) ParentAndName(string folder)
    {
        var trimmed = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
        return (Path.GetDirectoryName(trimmed) ?? trimmed, Path.GetFileName(trimmed) is { Length: > 0 } name ? name : "TutorArchier");
    }

    /// <summary>The encoder archive mode re-encodes with; null when nothing here does HEVC 10-bit.</summary>
    public static (EncoderDefinition? Encoder, EncoderCapability? Capability) ArchiveEncoder(HardwareReport? hardware) =>
        PickEncoder(ArchiveCodec, ArchivePixelFormat, hardware);

    /// <param name="archiveQuality">
    /// Archive mode: every lecture that isn't HEVC 10-bit already is re-encoded to it at this CQ/CRF
    /// (from <see cref="QualitySearch"/>). Null for the lossless merge.
    /// </param>
    public static MergePlan Create(FolderAnalysis analysis, string outputPath, HardwareReport? hardware, int? archiveQuality = null)
    {
        var usable = analysis.Videos.Where(v => v.IsUsable).ToList();
        if (usable.Count == 0)
            throw new InvalidOperationException("Birleştirilecek okunabilir video yok.");

        // The format covering most of the running time wins, so the fewest minutes get re-encoded.
        var target = usable
            .GroupBy(v => FormatSignature.Of(v.Info!))
            .MaxBy(g => g.Sum(v => v.Info!.Duration.Ticks))!.Key;

        // A silent majority would drop every other lecture's sound. Audio comes from the audible lectures' own
        // majority instead and the silent ones get generated silence. Its time base follows the majority's
        // container (MKV 1/1000, MP4 1/rate), so that container is kept and only the odd ones are re-wrapped.
        if (target.AudioCodec is null && usable.Any(v => v.Info!.Audio is not null))
        {
            var audio = usable
                .Where(v => v.Info!.Audio is not null)
                .GroupBy(v => (v.Info!.Audio!.Codec, v.Info.Audio.SampleRate, v.Info.Audio.Channels))
                .MaxBy(g => g.Sum(v => v.Info!.Duration.Ticks))!.Key;
            target = target with
            {
                AudioCodec = audio.Codec,
                SampleRate = audio.SampleRate,
                Channels = audio.Channels,
                AudioTimeBase = target.VideoTimeBase == MatroskaTimeBase ? MatroskaTimeBase : string.Create(CultureInfo.InvariantCulture, $"1/{audio.SampleRate}"),
            };
        }

        if (archiveQuality is not null)
        {
            // Resolution and audio stay the majority's. Every part is a fresh encode, so any time base works:
            // MP4 at 90 kHz, like the lossless path's parts. It keeps AAC's encoder delay in an edit list,
            // where MKV would shift the video by it and leave the priming samples to the player.
            target = target with
            {
                VideoCodec = ArchiveCodec,
                PixelFormat = ArchivePixelFormat,
                VideoTimeBase = "1/90000",
                AudioTimeBase = target.AudioCodec is null ? null : string.Create(CultureInfo.InvariantCulture, $"1/{target.SampleRate}"),
            };
        }

        // An MP4 can't carry e.g. WMV or Vorbis; those lectures are converted, the rest is still copied.
        var mp4 = IsMp4(outputPath);
        if (mp4 && !Mp4Video.Contains(target.VideoCodec))
            target = target with { VideoCodec = "h264", PixelFormat = "yuv420p" };
        if (mp4 && target.AudioCodec is { } mp4Audio && !Mp4Audio.Contains(mp4Audio))
            target = target with { AudioCodec = "aac" };

        var (encoder, capability) = PickEncoder(target.VideoCodec, target.PixelFormat, hardware);
        if (encoder is null && archiveQuality is not null)
            throw new InvalidOperationException("Arşiv modu için HEVC 10-bit kodlayabilen bir encoder bulunamadı.");
        if (encoder is null && usable.Any(v => !FormatSignature.Of(v.Info!).VideoMatches(target)))
        {
            // e.g. a VP9 or AV1 majority: nothing here encodes those, so the odd ones out can't be matched.
            // Everything goes to H.264 instead (libx264 is in every supported build).
            target = target with { VideoCodec = "h264", PixelFormat = "yuv420p" };
            (encoder, capability) = PickEncoder(target.VideoCodec, target.PixelFormat, hardware);
        }
        if (target.AudioCodec is { } audioCodec && !AudioEncoders.ContainsKey(audioCodec))
            target = target with { AudioCodec = "aac" };

        // Prepared segments must come out in the target's time base: MKV is always 1/1000, MP4 takes the
        // video timescale as an option and uses 1/sample-rate for audio. Anything else (AVI, odd MP4s)
        // can't be reproduced, so then every segment is re-wrapped as MKV instead.
        var container = Container(target);
        if (container is null)
        {
            target = target with { VideoTimeBase = MatroskaTimeBase, AudioTimeBase = target.AudioCodec is null ? null : MatroskaTimeBase };
            container = Container(target)!;
        }

        var workDirectory = outputPath + FolderScanner.WorkDirectorySuffix;
        var extension = container.Value.Format == "mp4" ? ".mp4" : ".mkv";
        var quality = archiveQuality ?? (encoder is null ? 0 : EncoderPresets.TransparentQuality(encoder));
        var segments = usable
            .Select((video, i) => Segment(video, Path.Combine(workDirectory, $"{i + 1:0000}{extension}"), target, container.Value, encoder, capability, quality, archiveQuality is not null))
            .ToList();

        return new MergePlan(
            analysis.Folder,
            outputPath,
            Path.GetFileNameWithoutExtension(outputPath),
            target,
            encoder?.Id,
            segments,
            SubtitleTracksFor(usable),
            analysis.Videos.Where(v => !v.IsUsable).Select(v => v.File.RelativePath).ToList(),
            archiveQuality);
    }

    private static MergeSegment Segment(
        AnalyzedVideo video, string encodedPath, FormatSignature target, (string Format, string? Timescale) container,
        EncoderDefinition? encoder, EncoderCapability? capability, int quality, bool encodeAll)
    {
        var info = video.Info!;
        var own = FormatSignature.Of(info);
        // The concat demuxer pairs streams by index, so the joined layout must be video 0, audio 1.
        var lined = info.Video!.Index == 0 && (info.Audio is null ? target.AudioCodec is null : info.Audio.Index == 1);
        // Archive mode encodes even lectures that are HEVC 10-bit already: only the first part's parameter sets
        // reach the MKV header, so another encoder's HEVC between ours could decode as garbage.
        var action = encodeAll || !own.VideoMatches(target) ? SegmentAction.EncodeVideo
            : !own.AudioMatches(target) ? SegmentAction.EncodeAudio
            : !lined || !own.TimeBaseMatches(target) ? SegmentAction.Remux
            : SegmentAction.Copy;

        var title = string.Join(" › ", Path.ChangeExtension(video.File.RelativePath, null).Split(Path.DirectorySeparatorChar));
        if (action == SegmentAction.Copy)
            return new MergeSegment(video.File.FullPath, title, info.Duration, action, null, null, 0);
        if (action == SegmentAction.EncodeVideo && (encoder is null || capability is null))
            throw new InvalidOperationException("Farklı formattaki videoları dönüştürecek bir encoder bulunamadı; ffmpeg derlemesini kontrol et.");

        return new MergeSegment(
            video.File.FullPath,
            title,
            info.Duration,
            action,
            encodedPath,
            Arguments(video, action, target, container, encoder, capability, quality, encodedPath + ".partial"),
            Estimate(info, action, target, capability));
    }

    private static List<string> Arguments(
        AnalyzedVideo video, SegmentAction action, FormatSignature target, (string Format, string? Timescale) container,
        EncoderDefinition? encoder, EncoderCapability? capability, int quality, string output)
    {
        var info = video.Info!;
        var silence = target.AudioCodec is not null && info.Audio is null;
        var args = new List<string> { "-hide_banner", "-v", "error", "-y", "-i", video.File.FullPath };
        if (silence)
            args.AddRange(["-f", "lavfi", "-i", string.Create(CultureInfo.InvariantCulture, $"anullsrc=r={target.SampleRate}:cl={Layout(target.Channels)}")]);

        args.AddRange(["-map", "0:V:0"]);
        if (target.AudioCodec is not null)
            args.AddRange(["-map", silence ? "1:a:0" : "0:a:0"]);

        if (action == SegmentAction.EncodeVideo && encoder is not null && capability is not null)
        {
            args.AddRange(["-c:v", encoder.Id, .. EncoderPresets.ConstantQuality(encoder, capability, quality)]);
            if (info.Video!.Width != target.Width || info.Video.Height != target.Height)
            {
                // Letterbox instead of stretching when the aspect ratio differs.
                args.AddRange(["-vf", string.Create(CultureInfo.InvariantCulture,
                    $"scale={target.Width}:{target.Height}:force_original_aspect_ratio=decrease,pad={target.Width}:{target.Height}:(ow-iw)/2:(oh-ih)/2,setsar=1")]);
            }
            args.AddRange(["-pix_fmt", encoder.IsHardware && target.PixelFormat == "yuv420p10le" ? "p010le" : target.PixelFormat]);
        }
        else
        {
            args.AddRange(["-c:v", "copy"]);
        }

        if (target.AudioCodec is null)
            args.Add("-an");
        else if (!silence && FormatSignature.Of(info).AudioMatches(target))
            args.AddRange(["-c:a", "copy"]);
        else
            args.AddRange([
                "-c:a", .. AudioEncoders[target.AudioCodec](target.Channels),
                "-ar", target.SampleRate.ToString(CultureInfo.InvariantCulture),
                "-ac", target.Channels.ToString(CultureInfo.InvariantCulture),
            ]);

        if (silence)
            args.Add("-shortest");
        args.AddRange(["-sn", "-dn", "-map_metadata", "-1", "-map_chapters", "-1"]);
        if (container.Timescale is { } timescale)
            args.AddRange(["-video_track_timescale", timescale]);
        args.AddRange(["-f", container.Format, output]);
        return args;
    }

    /// <summary>The container that reproduces the target's time bases, or null when none can.</summary>
    private static (string Format, string? Timescale)? Container(FormatSignature target)
    {
        if (target.VideoTimeBase == MatroskaTimeBase && (target.AudioCodec is null || target.AudioTimeBase == MatroskaTimeBase))
            return ("matroska", null);

        var parts = target.VideoTimeBase.Split('/');
        var mp4Audio = target.AudioCodec is null
            || (Mp4Audio.Contains(target.AudioCodec) && target.AudioTimeBase == string.Create(CultureInfo.InvariantCulture, $"1/{target.SampleRate}"));
        if (parts is ["1", var den] && int.TryParse(den, NumberStyles.None, CultureInfo.InvariantCulture, out var timescale) && timescale > 0 && mp4Audio)
            return ("mp4", den);

        return null;
    }

    /// <summary>Benchmark fps is measured at 1080p; scale by pixel count. Copy-only steps run at disk speed.</summary>
    private static double? Estimate(MediaInfo info, SegmentAction action, FormatSignature target, EncoderCapability? capability)
    {
        if (action != SegmentAction.EncodeVideo)
            return info.Duration.TotalSeconds / 100;
        if (capability?.BenchmarkFps is not > 0)
            return null;
        var frames = info.Duration.TotalSeconds * (info.Video!.FrameRate > 0 ? info.Video.FrameRate : 30);
        return frames * (target.Width * (double)target.Height / (1920 * 1080)) / capability.BenchmarkFps.Value;
    }

    /// <summary>Fastest encoder for the target codec: measured hardware first, software as the fallback.</summary>
    private static (EncoderDefinition? Encoder, EncoderCapability? Capability) PickEncoder(string videoCodec, string pixelFormat, HardwareReport? hardware)
    {
        VideoCodecKind? codec = videoCodec switch
        {
            "h264" => VideoCodecKind.H264,
            "hevc" => VideoCodecKind.Hevc,
            _ => null,
        };
        if (codec is null)
            return (null, null);

        var tenBit = pixelFormat == ArchivePixelFormat;
        var hardwareFriendly = tenBit || pixelFormat == "yuv420p";
        return EncoderCatalog.All
            .Where(e => e.UsedForEncoding && e.Codec == codec && (!e.IsHardware || hardwareFriendly))
            .Select(e => (Encoder: e, Capability: hardware?.Find(e.Id)
                // No report yet: assume software works (it ships in the build), hardware doesn't.
                ?? new EncoderCapability(e.Id, Available: !e.IsHardware, TenBit: true, BFrames: true)))
            .Where(c => c.Capability.Available && (!tenBit || c.Capability.TenBit))
            .OrderByDescending(c => c.Encoder.IsHardware)
            .ThenByDescending(c => c.Capability.BenchmarkFps ?? 0)
            .Select(c => ((EncoderDefinition?)c.Encoder, (EncoderCapability?)c.Capability))
            .FirstOrDefault();
    }

    /// <summary>One track per language found anywhere in the folder: English, Turkish, others, then unlabelled.</summary>
    private static List<SubtitleTrack> SubtitleTracksFor(List<AnalyzedVideo> videos) =>
        videos
            .SelectMany(v => v.File.Subtitles.Select(s => s.Language))
            .Distinct()
            .OrderBy(language => language switch { "en" => 0, "tr" => 1, "" => 3, _ => 2 })
            .ThenBy(language => language, StringComparer.Ordinal)
            .Select(language => new SubtitleTrack(
                language,
                language switch { "en" => "English", "tr" => "Türkçe", "" => "Altyazı", _ => language.ToUpperInvariant() },
                videos.Select(v => v.File.Subtitles.FirstOrDefault(s => s.Language == language)?.FullPath).ToList()))
            .ToList();

    private static string Layout(int channels) => channels switch
    {
        1 => "mono",
        6 => "5.1",
        8 => "7.1",
        _ => "stereo",
    };
}
