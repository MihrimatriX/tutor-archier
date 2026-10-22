using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.Logging;
using TutorArchier.Core.Settings;
using TutorArchier.Engine.Hardware;
using TutorArchier.Engine.Media;

namespace TutorArchier.UI.Services;

/// <summary>
/// The folder the user is working on and its analysis, shared by Home, Analysis and Merge so the
/// folder is scanned once; also remembers recently opened courses. Lives on the UI thread.
/// </summary>
public sealed partial class WorkspaceState(FolderAnalyzer analyzer, HardwareService hardware, ISettingsStore settings, ILogger<WorkspaceState> logger) : ObservableObject
{
    private const int RecentLimit = 8;

    private CancellationTokenSource? _analysis;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasFolder), nameof(CourseName))]
    public partial string? SelectedFolder { get; set; }

    public bool HasFolder => !string.IsNullOrEmpty(SelectedFolder);

    public string? CourseName => SelectedFolder is { } folder ? Path.GetFileName(Path.TrimEndingDirectorySeparator(folder)) : null;

    /// <summary>Analysis of <see cref="SelectedFolder"/>; null until it has been scanned.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CourseSummary), nameof(Cover), nameof(CoverColumns))]
    public partial FolderAnalysis? Analysis { get; set; }

    /// <summary>"12 ders · 3 sa 20 dk · 4,2 GB · 3 bölüm klasörü"; the scan's progress until then.</summary>
    public string? CourseSummary => Analysis is { } analysis ? Summarize(analysis) : IsAnalyzing ? AnalysisText : null;

    /// <summary>Lectures for the cover: four spread over the course, or the first one when there are fewer.</summary>
    public IReadOnlyList<string> Cover
    {
        get
        {
            var usable = Analysis?.Videos.Where(v => v.IsUsable).Select(v => v.File.FullPath).ToList() ?? [];
            return usable.Count >= 4 ? [.. Enumerable.Range(0, 4).Select(i => usable[i * (usable.Count - 1) / 3])] : [.. usable.Take(1)];
        }
    }

    public int CoverColumns => Cover.Count >= 4 ? 2 : 1;

    /// <summary>Newest first; courses whose folder is gone are left out.</summary>
    public IReadOnlyList<RecentFolder> Recents => [.. settings.Get(AppSettingKeys.RecentFolders).Where(r => Directory.Exists(r.Path))];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CourseSummary))]
    public partial bool IsAnalyzing { get; set; }

    [ObservableProperty]
    public partial double AnalysisProgress { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CourseSummary))]
    public partial string? AnalysisText { get; set; }

    [ObservableProperty]
    public partial string? AnalysisError { get; set; }

    partial void OnSelectedFolderChanged(string? value)
    {
        _analysis?.Cancel();
        Analysis = null;
        AnalysisError = null;
    }

    /// <summary>Scans the folder unless it already has a current analysis (or <paramref name="force"/>).</summary>
    public async Task AnalyzeAsync(bool force = false)
    {
        if (SelectedFolder is not { } folder || IsAnalyzing || (!force && Analysis?.Folder == folder))
            return;
        if (hardware.Ffmpeg is not { } ffmpeg)
        {
            AnalysisError = "Videoları okumak için ffmpeg gerekiyor. Ayarlar'dan tek tıkla kurabilirsin.";
            // A folder passed at startup is opened before ffmpeg has been found; scan as soon as it turns up.
            hardware.Changed -= RetryOnceFfmpegFound;
            hardware.Changed += RetryOnceFfmpegFound;
            return;
        }

        using var cancellation = new CancellationTokenSource();
        _analysis = cancellation;
        IsAnalyzing = true;
        AnalysisError = null;
        AnalysisProgress = 0;
        AnalysisText = "Klasör taranıyor…";
        try
        {
            var result = await analyzer.AnalyzeAsync(
                folder,
                ffmpeg.FfprobePath,
                new Progress<(int Done, int Total)>(p =>
                {
                    AnalysisProgress = p.Total == 0 ? 0 : 100.0 * p.Done / p.Total;
                    AnalysisText = $"Videolar okunuyor · {p.Done} / {p.Total}";
                }),
                cancellation.Token);
            if (folder == SelectedFolder)
            {
                Analysis = result;
                Remember(result);
            }
        }
        catch (OperationCanceledException)
        {
            // Folder changed or the user stopped it.
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Could not scan {Folder}", folder);
            AnalysisError = $"Klasör okunamadı: {ex.Message}";
        }
        finally
        {
            _analysis = null;
            IsAnalyzing = false;
        }
    }

    public void CancelAnalysis() => _analysis?.Cancel();

    private void RetryOnceFfmpegFound(object? sender, EventArgs e)
    {
        if (hardware.Ffmpeg is null)
            return;
        hardware.Changed -= RetryOnceFfmpegFound;
        UiDispatcher.Post(() => _ = AnalyzeAsync());
    }

    private void Remember(FolderAnalysis analysis)
    {
        var usable = analysis.Videos.Where(v => v.IsUsable).ToList();
        var entry = new RecentFolder(
            analysis.Folder,
            CourseName ?? analysis.Folder,
            usable.Count,
            analysis.TotalDuration,
            analysis.TotalSizeBytes,
            Cover is [var cover, ..] ? cover : null,
            DateTimeOffset.Now);
        var recents = settings.Get(AppSettingKeys.RecentFolders)
            .Where(r => !string.Equals(r.Path, analysis.Folder, StringComparison.OrdinalIgnoreCase))
            .Prepend(entry)
            .Take(RecentLimit)
            .ToList();
        settings.Set<IReadOnlyList<RecentFolder>>(AppSettingKeys.RecentFolders, recents);
        OnPropertyChanged(nameof(Recents));
    }

    private static string Summarize(FolderAnalysis analysis)
    {
        var lectures = analysis.Videos.Count(v => v.IsUsable);
        var sections = analysis.Videos.Select(v => Path.GetDirectoryName(v.File.RelativePath)).Where(d => !string.IsNullOrEmpty(d)).Select(d => d!.Split(Path.DirectorySeparatorChar)[0]).Distinct().Count();
        return $"{lectures} ders · {UiCulture.FormatDuration(analysis.TotalDuration)} · {UiCulture.FormatSize(analysis.TotalSizeBytes)}"
            + (sections > 1 ? $" · {sections} bölüm klasörü" : "");
    }
}
