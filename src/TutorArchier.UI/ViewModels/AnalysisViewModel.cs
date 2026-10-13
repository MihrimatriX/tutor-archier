using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TutorArchier.Engine.Media;
using TutorArchier.UI.Services;
using TutorArchier.UI.Views.Pages;
using Wpf.Ui;

namespace TutorArchier.UI.ViewModels;

/// <param name="Share">0..100 of total running time, drawn as a bar.</param>
public sealed record DistributionRow(string Label, string Detail, double Share);

public sealed record AnalysisRow(
    int Number, string FullPath, string Path, string Duration, string Resolution, string Codec, string Size, bool HasEn, bool HasTr, bool HasProblem);

/// <summary>Phase 1: what's in the folder, and what will get in the way of merging it.</summary>
public sealed partial class AnalysisViewModel : ObservableObject
{
    private readonly INavigationService _navigation;

    public AnalysisViewModel(WorkspaceState workspace, INavigationService navigation)
    {
        Workspace = workspace;
        _navigation = navigation;
        Workspace.PropertyChanged += OnWorkspaceChanged;
        // The page is created on first visit; the analysis may already have run from Home or Merge.
        Refresh(Workspace.Analysis);
    }

    public WorkspaceState Workspace { get; }

    public ObservableCollection<DistributionRow> Codecs { get; } = [];

    public ObservableCollection<DistributionRow> Resolutions { get; } = [];

    public ObservableCollection<AnalysisProblem> Problems { get; } = [];

    public ObservableCollection<AnalysisRow> Files { get; } = [];

    [ObservableProperty]
    public partial bool HasResult { get; set; }

    [ObservableProperty]
    public partial string VideoCount { get; set; } = "";

    [ObservableProperty]
    public partial string SubtitleCount { get; set; } = "";

    [ObservableProperty]
    public partial string TotalDuration { get; set; } = "";

    [ObservableProperty]
    public partial string TotalSize { get; set; } = "";

    [ObservableProperty]
    public partial string EnCoverage { get; set; } = "";

    [ObservableProperty]
    public partial string TrCoverage { get; set; } = "";

    [ObservableProperty]
    public partial string ProblemsTitle { get; set; } = "";

    public Task OnShownAsync() => Workspace.AnalyzeAsync();

    [RelayCommand]
    private Task RescanAsync() => Workspace.AnalyzeAsync(force: true);

    [RelayCommand]
    private void Cancel() => Workspace.CancelAnalysis();

    [RelayCommand]
    private void PickFolder()
    {
        if (FolderPicker.Pick("Video klasörünü seç", Workspace.SelectedFolder) is { } folder)
        {
            Workspace.SelectedFolder = folder;
            _ = Workspace.AnalyzeAsync();
        }
    }

    [RelayCommand]
    private void GoToMerge() => _navigation.Navigate(typeof(MergePage));

    private void OnWorkspaceChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(WorkspaceState.Analysis))
            Refresh(Workspace.Analysis);
    }

    private void Refresh(FolderAnalysis? analysis)
    {
        Codecs.Clear();
        Resolutions.Clear();
        Problems.Clear();
        Files.Clear();
        HasResult = analysis is not null;
        if (analysis is null)
            return;

        var videos = analysis.Videos;
        var total = analysis.TotalDuration;
        VideoCount = videos.Count.ToString(UiCulture.Current);
        SubtitleCount = $"{videos.Sum(v => v.File.Subtitles.Count)} altyazı dosyası";
        TotalDuration = UiCulture.FormatDuration(total);
        TotalSize = UiCulture.FormatSize(analysis.TotalSizeBytes);
        EnCoverage = $"{analysis.CountWithSubtitle("en")} / {videos.Count}";
        TrCoverage = $"{analysis.CountWithSubtitle("tr")} / {videos.Count}";

        foreach (var row in Distribution(videos, v => UiCulture.FormatCodec(v.Info!.Video!.Codec), total))
            Codecs.Add(row);
        foreach (var row in Distribution(videos, v => $"{v.Info!.Video!.Width}×{v.Info.Video.Height}", total))
            Resolutions.Add(row);

        var problems = analysis.Problems;
        foreach (var problem in problems)
            Problems.Add(problem);
        ProblemsTitle = problems.Count == 0 ? "Sorun yok" : $"Sorunlar ({problems.Count})";

        var problemPaths = problems.Where(p => p.IsError).Select(p => p.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < videos.Count; i++)
        {
            var video = videos[i];
            var info = video.Info;
            Files.Add(new AnalysisRow(
                i + 1,
                video.File.FullPath,
                video.File.RelativePath,
                info is null ? "—" : UiCulture.FormatClock(info.Duration),
                info?.Video is { } v ? $"{v.Width}×{v.Height}" : "—",
                info?.Video is { } c ? UiCulture.FormatCodec(c.Codec) : "—",
                UiCulture.FormatSize(video.SizeBytes),
                video.File.Subtitles.Any(s => s.Language == "en"),
                video.File.Subtitles.Any(s => s.Language == "tr"),
                problemPaths.Contains(video.File.RelativePath)));
        }
    }

    private static IEnumerable<DistributionRow> Distribution(IReadOnlyList<AnalyzedVideo> videos, Func<AnalyzedVideo, string> key, TimeSpan total) =>
        videos
            .Where(v => v.Info?.Video is not null)
            .GroupBy(key)
            .Select(g => (Label: g.Key, Count: g.Count(), Duration: g.Aggregate(TimeSpan.Zero, (sum, v) => sum + v.Info!.Duration)))
            .OrderByDescending(g => g.Duration)
            .Select(g => new DistributionRow(
                g.Label,
                $"{g.Count} video · {UiCulture.FormatDuration(g.Duration)}",
                total > TimeSpan.Zero ? 100 * g.Duration / total : 0));
}
