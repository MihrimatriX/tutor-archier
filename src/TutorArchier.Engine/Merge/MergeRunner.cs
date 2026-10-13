using System.Globalization;
using System.IO.Compression;
using System.Text;
using Microsoft.Extensions.Logging;
using TutorArchier.Core;
using TutorArchier.Core.Hardware;
using TutorArchier.Engine.Ffmpeg;
using TutorArchier.Engine.Media;

namespace TutorArchier.Engine.Merge;

/// <param name="Fraction">0..1 of the whole job; every step weighs the same.</param>
/// <param name="Text">What's running now; several steps at once are joined with " · ".</param>
public sealed record MergeProgress(double Fraction, string Text);

/// <summary>
/// Runs a <see cref="MergePlan"/>: prepares the segments that need it and zips the resources, several at a time,
/// then joins everything into one MKV (or MP4) with chapters and one subtitle track per language. Each finished step stays
/// on disk, so a resumed job skips it. Source files are only ever read.
/// </summary>
public sealed class MergeRunner(FfmpegRunner runner, ILogger<MergeRunner> logger)
{
    // ponytail: fixed slot counts. Consumer NVENC/AMF chips have one or two engines (more sessions just share them),
    // and x264/x265 already use every core, so a second software encode would only fight the first. Remux and audio
    // steps are disk-bound; two keep a disk busy without making a spinning one seek itself to death.
    private const int HardwareEncodeSlots = 2;
    private const int SoftwareEncodeSlots = 1;
    private const int LightSlots = 2;

    /// <summary>Already compressed: deflating them again costs time and saves nothing.</summary>
    private static readonly HashSet<string> Stored = new(StringComparer.OrdinalIgnoreCase)
    {
        ".zip", ".rar", ".7z", ".gz", ".xz", ".bz2", ".jpg", ".jpeg", ".png", ".gif", ".webp", ".mp3", ".m4a", ".aac", ".ogg", ".opus", ".flac",
        ".mp4", ".m4v", ".mkv", ".webm", ".mov", ".avi", ".ts", ".flv", ".wmv", ".mpg", ".mpeg",
    };

    /// <param name="onStepCompleted">Called with the new completed-step count after each step, never concurrently.</param>
    public async Task RunAsync(
        FfmpegInfo ffmpeg,
        MergePlan plan,
        Action<MergeProgress> onProgress,
        Action<int> onStepCompleted,
        CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(plan.WorkDirectory);
        var prepared = plan.Prepared;
        var steps = plan.StepCount;
        var fractions = new double[steps];
        var running = new List<string>();
        var gate = new Lock();
        var completed = 0;

        void Report()
        {
            lock (gate)
                onProgress(new MergeProgress(Math.Clamp(fractions.Sum() / steps, 0, 1), string.Join(" · ", running)));
        }

        // Steps run in any order, so the files on disk say what's done: each one only leaves ".partial" on success.
        async Task Step(int step, string text, string donePath, Func<Action<double>, CancellationToken, Task> work, CancellationToken token)
        {
            if (File.Exists(donePath))
            {
                lock (gate)
                    fractions[step] = 1;
                return;
            }

            lock (gate)
                running.Add(text);
            Report();
            try
            {
                await work(f => { lock (gate) fractions[step] = Math.Clamp(f, 0, 1); Report(); }, token);
            }
            finally
            {
                lock (gate)
                    running.Remove(text);
            }

            lock (gate)
            {
                fractions[step] = 1;
                onStepCompleted(++completed);
            }
            Report();
        }

        // One failure stops the siblings too, instead of encoding for an hour toward a join that can't happen.
        using var failFast = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        async Task Lane(IEnumerable<int> items, int slots)
        {
            try
            {
                await Parallel.ForEachAsync(
                    items,
                    new ParallelOptions { MaxDegreeOfParallelism = slots, CancellationToken = failFast.Token },
                    async (i, token) =>
                    {
                        var segment = prepared[i];
                        var verb = segment.Action == SegmentAction.EncodeVideo ? "kodlanıyor" : "hazırlanıyor";
                        await Step(i, $"{segment.Title} {verb}", segment.EncodedPath!, (report, t) => PrepareAsync(ffmpeg, segment, report, t), token);
                    });
            }
            catch
            {
                await failFast.CancelAsync();
                throw;
            }
        }

        var hardware = plan.EncoderId is { } id && EncoderCatalog.Find(id) is { IsHardware: true };
        var indices = Enumerable.Range(0, prepared.Count).ToList();
        var lanes = new List<Task>
        {
            Lane(indices.Where(i => prepared[i].Action == SegmentAction.EncodeVideo), hardware ? HardwareEncodeSlots : SoftwareEncodeSlots),
            Lane(indices.Where(i => prepared[i].Action != SegmentAction.EncodeVideo), LightSlots),
        };
        if (plan.Resources is { } resources)
        {
            lanes.Add(Task.Run(async () =>
            {
                try
                {
                    await Step(prepared.Count, "Ek dosyalar zipleniyor", resources.ArchivePath, (report, t) => ZipAsync(plan, resources, report, t), failFast.Token);
                }
                catch
                {
                    await failFast.CancelAsync();
                    throw;
                }
            }, failFast.Token));
        }
        // A faulted lane wins over the ones it cancelled, so the real error is the one reported.
        await Task.WhenAll(lanes);

        const string joining = "Bölümler tek dosyada birleştiriliyor";
        lock (gate)
            running.Add(joining);
        Report();
        await JoinAsync(ffmpeg, plan, f => { lock (gate) fractions[steps - 1] = Math.Clamp(f, 0, 1); Report(); }, cancellationToken);
        onStepCompleted(steps);

        AppPaths.TryDeleteDirectory(plan.WorkDirectory);
        logger.LogInformation("Merged {Count} videos into {Output}", plan.Segments.Count, plan.OutputPath);
    }

    private async Task PrepareAsync(FfmpegInfo ffmpeg, MergeSegment segment, Action<double> report, CancellationToken cancellationToken)
    {
        var result = await runner.RunAsync(
            ffmpeg.FfmpegPath,
            segment.Arguments!,
            new FfmpegRunOptions { LowPriority = true, OnProgress = p => report(p.OutTime / segment.Duration) },
            cancellationToken);
        if (!result.Success)
            throw new InvalidOperationException($"{segment.Title} dönüştürülemedi: {LastLine(result.StdErr)}");

        File.Move(segment.EncodedPath + ".partial", segment.EncodedPath!, overwrite: true);
    }

    /// <summary>
    /// Written inside the work folder first, so cancelling the job cleans it up with the rest. Fastest deflate, and
    /// none at all for files that are compressed already: this is about keeping the files, not about size.
    /// </summary>
    private async Task ZipAsync(MergePlan plan, ResourceBundle resources, Action<double> report, CancellationToken cancellationToken)
    {
        var partial = Path.Combine(plan.WorkDirectory, "resources.zip.partial");
        File.Delete(partial); // a run cut off mid-zip; Create mode won't open an existing file
        long done = 0;
        await using (var zip = await ZipFile.OpenAsync(partial, ZipArchiveMode.Create, cancellationToken))
        {
            foreach (var file in resources.Files)
            {
                var name = Path.GetRelativePath(resources.Root, file).Replace(Path.DirectorySeparatorChar, '/');
                try
                {
                    var level = Stored.Contains(Path.GetExtension(file)) ? CompressionLevel.NoCompression : CompressionLevel.Fastest;
                    await zip.CreateEntryFromFileAsync(file, name, level, cancellationToken);
                    done += new FileInfo(file).Length;
                }
                catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
                {
                    // Deleted since the analysis; the rest is still worth keeping.
                    logger.LogWarning("Resource {File} disappeared before it was zipped", file);
                }
                report(resources.Bytes == 0 ? 1 : (double)done / resources.Bytes);
            }
        }

        Directory.CreateDirectory(Path.GetDirectoryName(resources.ArchivePath)!);
        // Never overwrite, like the video: the name was free when the job was planned.
        File.Move(partial, resources.ArchivePath, overwrite: false);
    }

    private async Task JoinAsync(FfmpegInfo ffmpeg, MergePlan plan, Action<double> onFraction, CancellationToken cancellationToken)
    {
        var work = plan.WorkDirectory;
        var list = Path.Combine(work, "segments.ffconcat");
        var chapters = Path.Combine(work, "chapters.ffmeta");
        await File.WriteAllTextAsync(list, ConcatList(plan), new UTF8Encoding(false), cancellationToken);
        await File.WriteAllTextAsync(chapters, ChapterMetadata(plan), new UTF8Encoding(false), cancellationToken);

        var subtitles = new List<(SubtitleTrack Track, string Path)>();
        for (var i = 0; i < plan.SubtitleTracks.Count; i++)
        {
            var cues = ShiftedCues(plan, plan.SubtitleTracks[i]).ToList();
            if (cues.Count == 0)
                continue; // only unreadable files for this language; an empty SRT would fail the whole join
            var path = Path.Combine(work, $"subtitles-{i}.srt");
            SubtitleFiles.WriteSrt(path, cues);
            subtitles.Add((plan.SubtitleTracks[i], path));
        }

        var partial = plan.OutputPath + ".partial";
        var args = new List<string>
        {
            "-hide_banner", "-v", "error", "-y",
            "-f", "concat", "-safe", "0", "-i", list,
            "-f", "ffmetadata", "-i", chapters,
        };
        foreach (var (_, path) in subtitles)
            args.AddRange(["-i", path]);

        args.AddRange(["-map", "0:V:0"]);
        if (plan.Target.AudioCodec is not null)
            args.AddRange(["-map", "0:a:0"]);
        for (var i = 0; i < subtitles.Count; i++)
            args.AddRange(["-map", $"{i + 2}:0"]);
        args.AddRange(["-map_metadata", "1", "-map_chapters", "1", "-c", "copy"]);
        var mp4 = MergePlanner.IsMp4(plan.OutputPath);
        if (mp4)
        {
            // MP4 text subtitles are mov_text; hvc1 is the HEVC tag Apple players and most TVs insist on.
            args.AddRange(["-c:s", "mov_text"]);
            if (plan.Target.VideoCodec == "hevc")
                args.AddRange(["-tag:v", "hvc1"]);
        }
        for (var i = 0; i < subtitles.Count; i++)
        {
            args.AddRange([
                $"-metadata:s:s:{i}", $"language={Iso6392(subtitles[i].Track.Language)}",
                $"-metadata:s:s:{i}", $"{(mp4 ? "handler_name" : "title")}={subtitles[i].Track.Title}",
            ]);
        }
        args.AddRange(["-f", mp4 ? "mp4" : "matroska", partial]);

        var total = plan.TotalDuration;
        var result = await runner.RunAsync(
            ffmpeg.FfmpegPath,
            args,
            new FfmpegRunOptions { OnProgress = p => onFraction(p.OutTime / total) },
            cancellationToken);
        if (!result.Success)
            throw new InvalidOperationException($"Birleştirme başarısız: {LastLine(result.StdErr)}");

        // Never overwrite: the user may have created a file with this name while the job waited.
        File.Move(partial, plan.OutputPath, overwrite: false);
    }

    /// <summary>
    /// Durations are given explicitly so the join offsets are exactly the ones used for chapters and subtitles.
    /// H.264 parameter sets are made in-band by the demuxer's auto_convert, so segments from different
    /// encoders still decode. ponytail: HEVC gets no such conversion; mixed-encoder HEVC folders rely on
    /// identical parameter sets, add hevc_mp4toannexb per segment if that shows up.
    /// </summary>
    internal static string ConcatList(MergePlan plan)
    {
        var builder = new StringBuilder("ffconcat version 1.0\n");
        foreach (var segment in plan.Segments)
        {
            builder.Append("file '").Append(segment.ConcatPath.Replace("'", @"'\''", StringComparison.Ordinal)).Append("'\n");
            builder.Append(CultureInfo.InvariantCulture, $"duration {segment.Duration.TotalSeconds:0.######}\n");
        }
        return builder.ToString();
    }

    internal static string ChapterMetadata(MergePlan plan)
    {
        var builder = new StringBuilder(";FFMETADATA1\n").Append("title=").Append(Escape(plan.Title)).Append('\n');
        var start = TimeSpan.Zero;
        foreach (var segment in plan.Segments)
        {
            var end = start + segment.Duration;
            builder.Append("[CHAPTER]\nTIMEBASE=1/1000\n")
                .Append(CultureInfo.InvariantCulture, $"START={(long)start.TotalMilliseconds}\nEND={(long)end.TotalMilliseconds}\n")
                .Append("title=").Append(Escape(segment.Title)).Append('\n');
            start = end;
        }
        return builder.ToString();

        // ffmetadata treats these as syntax.
        static string Escape(string value) => new StringBuilder(value)
            .Replace("\\", "\\\\").Replace("=", "\\=").Replace(";", "\\;").Replace("#", "\\#").Replace("\n", "\\\n")
            .ToString();
    }

    /// <summary>Each lecture's cues moved to where the lecture starts; cues running past its end are cut there.</summary>
    internal static IEnumerable<SubtitleCue> ShiftedCues(MergePlan plan, SubtitleTrack track)
    {
        var offset = TimeSpan.Zero;
        for (var i = 0; i < plan.Segments.Count; i++)
        {
            var duration = plan.Segments[i].Duration;
            if (track.Files[i] is { } file)
            {
                foreach (var cue in SubtitleFiles.Read(file).Where(c => c.Start < duration))
                    yield return new SubtitleCue(cue.Start + offset, (cue.End < duration ? cue.End : duration) + offset, cue.Text);
            }
            offset += duration;
        }
    }

    /// <summary>Matroska wants ISO 639-2 codes.</summary>
    private static string Iso6392(string language) => language switch
    {
        "en" => "eng",
        "tr" => "tur",
        "de" => "ger",
        "fr" => "fre",
        "es" => "spa",
        "it" => "ita",
        "ru" => "rus",
        "pt" => "por",
        "ar" => "ara",
        _ => "und",
    };

    internal static string LastLine(string text) =>
        text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault() ?? "bilinmeyen hata";
}
