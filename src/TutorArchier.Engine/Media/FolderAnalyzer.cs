using Microsoft.Extensions.Logging;
using TutorArchier.Core.Media;

namespace TutorArchier.Engine.Media;

/// <param name="Info">Null when ffprobe couldn't read the file; <paramref name="Error"/> says why.</param>
public sealed record AnalyzedVideo(ScannedVideo File, long SizeBytes, MediaInfo? Info, string? Error)
{
    /// <summary>Has a readable video stream and a duration, so it can be part of a merge.</summary>
    public bool IsUsable => Info is { Video: not null } info && info.Duration > TimeSpan.Zero;
}

public sealed record AnalysisProblem(string Path, string Message, bool IsError);

public sealed record FolderAnalysis(
    string Folder,
    IReadOnlyList<AnalyzedVideo> Videos,
    IReadOnlyList<string> OrphanSubtitles,
    IReadOnlyList<string> UnsupportedSubtitles,
    IReadOnlyList<string> OtherFiles)
{
    public TimeSpan TotalDuration => Videos.Aggregate(TimeSpan.Zero, (sum, v) => sum + (v.Info?.Duration ?? TimeSpan.Zero));

    public long TotalSizeBytes => Videos.Sum(v => v.SizeBytes);

    /// <summary>Videos with at least one subtitle file in <paramref name="language"/>.</summary>
    public int CountWithSubtitle(string language) =>
        Videos.Count(v => v.File.Subtitles.Any(s => s.Language == language));

    public IReadOnlyList<AnalysisProblem> Problems =>
    [
        .. Videos.SelectMany(Describe),
        .. OrphanSubtitles.Select(path => new AnalysisProblem(Relative(path), "Eşleşen video yok; dosya adı bir videonun adıyla başlamalı", IsError: false)),
        .. UnsupportedSubtitles.Select(path => new AnalysisProblem(Relative(path), "Bu altyazı biçimi birleştirilmiyor (yalnızca SRT ve VTT)", IsError: false)),
    ];

    private IEnumerable<AnalysisProblem> Describe(AnalyzedVideo video)
    {
        var path = video.File.RelativePath;
        if (video.Info is null)
            yield return new AnalysisProblem(path, $"Okunamadı: {video.Error}", IsError: true);
        else if (video.Info.Video is null)
            yield return new AnalysisProblem(path, "Video akışı yok", IsError: true);
        else if (video.Info.Duration <= TimeSpan.Zero)
            yield return new AnalysisProblem(path, "Süresi okunamadı", IsError: true);
        else if (video.Info.Audio is null)
            yield return new AnalysisProblem(path, "Ses yok; birleştirirken sessiz ses eklenir", IsError: false);
    }

    private string Relative(string path) => System.IO.Path.GetRelativePath(Folder, path);
}

public sealed class FolderAnalyzer(MediaProber prober, ILogger<FolderAnalyzer> logger)
{
    // ffprobe is I/O-bound on spinning disks and CPU-light; a few at a time is the sweet spot.
    private static readonly int Parallelism = Math.Clamp(Environment.ProcessorCount / 2, 2, 6);

    /// <param name="progress">(videos read, total videos).</param>
    public async Task<FolderAnalysis> AnalyzeAsync(
        string folder,
        string ffprobePath,
        IProgress<(int Done, int Total)>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var scan = await Task.Run(() => FolderScanner.Scan(folder), cancellationToken);
        var results = new AnalyzedVideo[scan.Videos.Count];
        var done = 0;
        progress?.Report((0, results.Length));

        await Parallel.ForEachAsync(
            Enumerable.Range(0, results.Length),
            new ParallelOptions { MaxDegreeOfParallelism = Parallelism, CancellationToken = cancellationToken },
            async (i, token) =>
            {
                var video = scan.Videos[i];
                try
                {
                    var file = new FileInfo(video.FullPath);
                    var (info, error) = await prober.ProbeAsync(ffprobePath, file, token);
                    results[i] = new AnalyzedVideo(video, file.Length, info, error);
                }
                catch (IOException ex)
                {
                    // Deleted or locked between the scan and the probe.
                    results[i] = new AnalyzedVideo(video, 0, null, ex.Message);
                }
                progress?.Report((Interlocked.Increment(ref done), results.Length));
            });

        logger.LogInformation("Analyzed {Folder}: {Videos} videos, {Orphans} unmatched subtitles", folder, results.Length, scan.OrphanSubtitles.Count);
        return new FolderAnalysis(folder, results, scan.OrphanSubtitles, scan.UnsupportedSubtitles, scan.OtherFiles);
    }
}
