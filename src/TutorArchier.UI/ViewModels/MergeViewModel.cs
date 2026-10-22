using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using TutorArchier.Core.Hardware;
using TutorArchier.Engine.Hardware;
using TutorArchier.Engine.Media;
using TutorArchier.Engine.Merge;
using TutorArchier.UI.Controls;
using TutorArchier.UI.Services;
using TutorArchier.UI.Views.Pages;
using Wpf.Ui;

namespace TutorArchier.UI.ViewModels;

/// <summary>A chapter of the one video, or one of the videos when each subfolder gets its own.</summary>
public sealed record ChapterRow(int Number, string? Video, string Title, string Start, string Action, StatusKind Kind);

public sealed record SubtitleTrackRow(string Title, string Coverage, StatusKind Kind);

/// <summary>
/// Phases 2 and 3: shows what the merge will do to the analysed folder (lossless or archive; one video, or one per
/// subfolder), then queues it. A single video is simply a list of one plan, so everything below sums over plans.
/// </summary>
public sealed partial class MergeViewModel : ObservableObject
{
    private readonly HardwareService _hardware;
    private readonly JobScheduler _scheduler;
    private readonly QualitySearch _qualitySearch;
    private readonly INavigationService _navigation;
    private readonly ILogger<MergeViewModel> _logger;

    /// <summary>Archive quality measured for the current analysis; reset when the folder changes.</summary>
    private QualitySearchResult? _archive;
    private CancellationTokenSource? _search;

    /// <summary>The chosen .mkv/.mp4 (one video) or folder (one per subfolder); null means the default next to the course.</summary>
    private string? _output;

    /// <summary>The analysis the current plans were made from.</summary>
    private FolderAnalysis? _planned;

    public MergeViewModel(
        WorkspaceState workspace, HardwareService hardware, JobScheduler scheduler, QualitySearch qualitySearch,
        INavigationService navigation, ILogger<MergeViewModel> logger)
    {
        Workspace = workspace;
        _hardware = hardware;
        _scheduler = scheduler;
        _qualitySearch = qualitySearch;
        _navigation = navigation;
        _logger = logger;
        Workspace.PropertyChanged += OnWorkspaceChanged;
    }

    public WorkspaceState Workspace { get; }

    public ObservableCollection<ChapterRow> Rows { get; } = [];

    public ObservableCollection<SubtitleTrackRow> Tracks { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPlan))]
    [NotifyCanExecuteChangedFor(nameof(EnqueueCommand))]
    public partial IReadOnlyList<MergePlan> Plans { get; set; } = [];

    public bool HasPlan => Plans.Count > 0;

    /// <summary>The folder has at least two subfolders (or one plus loose videos) with something to merge.</summary>
    [ObservableProperty]
    public partial bool CanSplit { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RowsTitle), nameof(RowTitleHeader), nameof(RowTimeHeader))]
    public partial bool IsSplit { get; set; }

    public string RowsTitle => IsSplit ? "Videolar" : "Bölümler";

    public string RowTitleHeader => IsSplit ? "VİDEO" : "BÖLÜM";

    public string RowTimeHeader => IsSplit ? "SÜRE" : "BAŞLANGIÇ";

    [ObservableProperty]
    public partial string? PlanError { get; set; }

    [ObservableProperty]
    public partial string OutputName { get; set; } = "";

    [ObservableProperty]
    public partial string OutputFolder { get; set; } = "";

    [ObservableProperty]
    public partial string Summary { get; set; } = "";

    [ObservableProperty]
    public partial string TargetText { get; set; } = "";

    [ObservableProperty]
    public partial string TargetNote { get; set; } = "";

    [ObservableProperty]
    public partial string? CopyText { get; set; }

    [ObservableProperty]
    public partial string? PrepareText { get; set; }

    [ObservableProperty]
    public partial string? SkippedText { get; set; }

    [ObservableProperty]
    public partial string? SpaceWarning { get; set; }

    [ObservableProperty]
    public partial string? SubtitleNote { get; set; }

    [ObservableProperty]
    public partial string FooterTitle { get; set; } = "";

    [ObservableProperty]
    public partial string FooterDetail { get; set; } = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(EnqueueCommand))]
    public partial bool IsArchive { get; set; }

    [ObservableProperty]
    public partial bool IsSearching { get; set; }

    [ObservableProperty]
    public partial double SearchProgress { get; set; }

    [ObservableProperty]
    public partial string? SearchText { get; set; }

    [ObservableProperty]
    public partial string? ArchiveText { get; set; }

    [ObservableProperty]
    public partial StatusKind ArchiveKind { get; set; }

    [ObservableProperty]
    public partial string? ArchiveError { get; set; }

    /// <summary>The course has files the MKVs leave out (PDFs, code, unreadable videos…).</summary>
    [ObservableProperty]
    public partial bool CanBundle { get; set; }

    [ObservableProperty]
    public partial bool IsBundling { get; set; }

    /// <summary>Join into MP4 (plays on phones, TVs, browsers) instead of MKV; chapters and subtitles stay.</summary>
    [ObservableProperty]
    public partial bool IsMp4 { get; set; }

    [ObservableProperty]
    public partial string? BundleText { get; set; }

    /// <summary>In archive mode the plans are only final once the quality search has run.</summary>
    private bool CanEnqueue => HasPlan && (!IsArchive || Plans.All(p => p.ArchiveQuality is not null));

    public async Task OnShownAsync()
    {
        await Workspace.AnalyzeAsync();
        // The page is created on first visit; the analysis may have finished before anyone was listening.
        if (_planned != Workspace.Analysis)
            Reset();
    }

    [RelayCommand]
    private void PickFolder()
    {
        if (FolderPicker.Pick("Birleştirilecek klasörü seç", Workspace.SelectedFolder) is { } folder)
        {
            Workspace.SelectedFolder = folder;
            _ = OnShownAsync();
        }
    }

    [RelayCommand]
    private void ChangeOutput()
    {
        if (!HasPlan)
            return;
        var current = Plans[0].OutputPath;
        if (IsSplit)
        {
            // The videos go straight into the picked folder.
            if (FolderPicker.Pick("Videoların kaydedileceği klasörü seç", Path.GetDirectoryName(Path.GetDirectoryName(current))) is { } folder)
            {
                _output = folder;
                Rebuild();
            }
            return;
        }

        var dialog = new SaveFileDialog
        {
            Title = "Birleştirilmiş dosyayı kaydet",
            Filter = IsMp4 ? "MP4 video (*.mp4)|*.mp4" : "Matroska video (*.mkv)|*.mkv",
            DefaultExt = IsMp4 ? ".mp4" : ".mkv",
            AddExtension = true,
            InitialDirectory = Path.GetDirectoryName(current),
            FileName = Path.GetFileName(current),
        };
        if (dialog.ShowDialog(System.Windows.Application.Current?.MainWindow) == true)
        {
            _output = dialog.FileName;
            Rebuild();
        }
    }

    [RelayCommand]
    private void CancelSearch() => _search?.Cancel();

    partial void OnIsArchiveChanged(bool value)
    {
        if (value)
            ArchiveError = null;
        else
            _search?.Cancel();
        Rebuild();
    }

    partial void OnIsBundlingChanged(bool value) => Rebuild();

    partial void OnIsMp4Changed(bool value)
    {
        // The extension (or "(MP4)" folder) is part of the name; go back to the new format's default.
        _output = null;
        Rebuild();
    }

    partial void OnIsSplitChanged(bool value)
    {
        // A file name and a folder aren't interchangeable; go back to the default of the new mode.
        _output = null;
        Rebuild();
    }

    private async Task SearchQualityAsync(FolderAnalysis analysis)
    {
        var (encoder, capability) = MergePlanner.ArchiveEncoder(_hardware.Report);
        if (_hardware.Ffmpeg is not { HasLibVmaf: true } ffmpeg || encoder is null || capability is null)
        {
            IsArchive = false;
            ArchiveError = encoder is null
                ? "Bu bilgisayarda HEVC 10-bit kodlayabilen bir encoder bulunamadı."
                : "Kalite ölçümü için libvmaf içeren bir ffmpeg gerekli; Ayarlar'dan Otomatik kur'u kullan.";
            return;
        }

        using var search = new CancellationTokenSource();
        _search = search;
        IsSearching = true;
        SearchProgress = 0;
        SearchText = "Örnekler hazırlanıyor";
        try
        {
            var progress = new Progress<QualitySearchProgress>(p =>
            {
                SearchProgress = p.Fraction * 100;
                SearchText = p.Text;
            });
            // One quality for the whole course, even when each subfolder becomes its own video.
            var result = await _qualitySearch.RunAsync(ffmpeg, analysis.Videos, encoder, capability, progress, search.Token);
            // The folder may have changed just as the last measurement finished.
            search.Token.ThrowIfCancellationRequested();
            _archive = result;
            Rebuild();
        }
        catch (OperationCanceledException)
        {
            IsArchive = false;
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning(ex, "Archive quality search failed for {Folder}", analysis.Folder);
            IsArchive = false;
            ArchiveError = ex.Message;
        }
        finally
        {
            IsSearching = false;
            _search = null;
        }
    }

    [RelayCommand(CanExecute = nameof(CanEnqueue))]
    private void Enqueue()
    {
        foreach (var plan in Plans)
            _scheduler.Enqueue(plan);
        // The queued output names are taken now; suggest fresh ones if the same folder is merged again.
        _output = null;
        Rebuild();
        _navigation.Navigate(typeof(QueuePage));
    }

    private void OnWorkspaceChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(WorkspaceState.Analysis))
            Reset();
    }

    /// <summary>
    /// Per-folder defaults: archive off (each course gets its own measurement), output next to the course, and one
    /// video per subfolder whenever there are subfolders.
    /// </summary>
    private void Reset()
    {
        _search?.Cancel();
        _archive = null;
        _output = null;
        ArchiveError = null;
        IsArchive = false;
        CanSplit = Workspace.Analysis is { } analysis && MergePlanner.Sections(analysis).Count > 1;
        IsSplit = CanSplit;
        CanBundle = Workspace.Analysis is { } course && MergePlanner.Bundle(course, course.Folder) is not null;
        IsBundling = CanBundle;
        Rebuild();
    }

    private void Rebuild()
    {
        Rows.Clear();
        Tracks.Clear();
        PlanError = null;
        Plans = [];
        _planned = Workspace.Analysis;
        if (Workspace.Analysis is not { } analysis)
            return;

        var quality = IsArchive ? _archive?.Quality : null;
        try
        {
            var plans = IsSplit
                ? MergePlanner.CreatePerSubfolder(analysis, _output ??= MergePlanner.DefaultOutputFolder(analysis.Folder, IsMp4), _hardware.Report, quality, IsMp4)
                : [MergePlanner.Create(analysis, _output ??= MergePlanner.DefaultOutputPath(analysis.Folder, IsMp4), _hardware.Report, quality)];
            // One zip per course, carried by its first video's job.
            if (IsBundling && MergePlanner.Bundle(analysis, Path.GetDirectoryName(plans[0].OutputPath)!) is { } bundle)
                plans = [plans[0] with { Resources = bundle }, .. plans.Skip(1)];
            Plans = plans;
        }
        catch (InvalidOperationException ex)
        {
            PlanError = ex.Message;
            return;
        }

        Describe(analysis, Plans);
        if (IsArchive && _archive is null && !IsSearching)
            _ = SearchQualityAsync(analysis);
    }

    private void Describe(FolderAnalysis analysis, IReadOnlyList<MergePlan> plans)
    {
        var segments = plans.SelectMany(p => p.Segments).ToList();
        var prepared = plans.SelectMany(p => p.Prepared).ToList();
        var duration = plans.Aggregate(TimeSpan.Zero, (sum, p) => sum + p.TotalDuration);
        var sourceBytes = analysis.Videos.Where(v => v.IsUsable).Sum(v => v.SizeBytes);
        var archiveQuality = plans[0].ArchiveQuality;

        OutputName = IsSplit ? $"{plans.Count} ayrı video" : Path.GetFileName(plans[0].OutputPath);
        OutputFolder = IsSplit ? _output ?? "" : Path.GetDirectoryName(plans[0].OutputPath) ?? "";
        Summary = (IsSplit ? $"{plans.Count} video · {segments.Count} ders" : $"{segments.Count} bölüm")
            + $" · {UiCulture.FormatDuration(duration)} · yaklaşık {UiCulture.FormatSize(sourceBytes)}";

        // Each video is planned on its own, so per-subfolder videos can each keep a different majority format.
        TargetText = string.Join("\n", plans.Select(p => FormatTarget(p.Target)).Distinct());
        TargetNote = (archiveQuality is null
                ? "Videoların çoğunun (süreye göre) formatı; bu formattaki dosyalara dokunulmaz."
                : "Çözünürlük ve ses, videoların çoğundan (süreye göre); görüntü her derste yeniden kodlanır.")
            + (IsSplit ? " Her video için ayrı belirlenir." : "");

        var copied = segments.Count(s => s.Action == SegmentAction.Copy);
        CopyText = copied == 0 ? null : $"{copied} / {segments.Count} ders olduğu gibi kopyalanacak (kalite kaybı yok)";

        var encoded = segments.Count(s => s.Action == SegmentAction.EncodeVideo);
        var rewrapped = prepared.Count - encoded;
        var encoder = plans.Select(p => p.EncoderId).FirstOrDefault(id => id is not null) is { } id ? EncoderCatalog.Find(id) : null;
        var seconds = prepared.Sum(s => s.EstimatedSeconds ?? 0);
        var estimate = prepared.Any(s => s.EstimatedSeconds is null) ? "süre tahmini için Ayarlar'dan hız ölçümü yap"
            : seconds < 30 ? "birkaç saniye sürer"
            : $"tahmini {UiCulture.FormatDuration(TimeSpan.FromSeconds(seconds))}";
        var encodeText = archiveQuality is { } quality && encoder is not null
            ? $"{encoded} ders HEVC 10-bit'e sıkıştırılacak ({encoder.DisplayName}, {EncoderPresets.QualityLabel(encoder)} {quality})"
            : $"{encoded} ders şeffaf kalitede yeniden kodlanacak ({encoder?.DisplayName})";
        PrepareText = prepared.Count == 0
            ? null
            : string.Join(" · ", new[]
            {
                encoded > 0 ? encodeText : null,
                rewrapped > 0 ? $"{rewrapped} dersin sesi ya da kabı uyarlanacak" : null,
                estimate,
            }.Where(part => part is not null));

        long? archiveBytes = null;
        ArchiveText = null;
        if (archiveQuality is not null && _archive is { } archive)
        {
            // Encoded lectures at the samples' bytes per second; anything kept as is stays its own size.
            var sizes = analysis.Videos.ToDictionary(v => v.File.FullPath, v => v.SizeBytes, StringComparer.OrdinalIgnoreCase);
            archiveBytes = (long)segments.Sum(s => s.Action == SegmentAction.EncodeVideo ? archive.BytesPerSecond * s.Duration.TotalSeconds : sizes[s.SourcePath]);
            var smaller = archiveBytes < sourceBytes;
            var reached = archive.Vmaf >= QualitySearch.TargetVmaf;
            ArchiveKind = smaller && reached ? StatusKind.Success : StatusKind.Warning;
            ArchiveText = $"VMAF {archive.Vmaf:0.#}{(reached ? "" : $" (hedef {QualitySearch.TargetVmaf:0} tutturulamadı; kaynak zaten kayıplı olabilir)")} · tahmini çıktı {UiCulture.FormatSize(archiveBytes.Value)}"
                + (smaller
                    ? $" (kaynaktan %{100 - 100.0 * archiveBytes.Value / sourceBytes:0} küçük)"
                    : " (kaynaktan küçük değil; bu klasör için kayıpsız birleştirme daha iyi)");
        }

        FooterTitle = IsSplit ? $"{plans.Count} video oluşturulacak" : $"{OutputName} oluşturulacak";
        FooterDetail = IsArchive && archiveQuality is null
            ? "Kalite ölçümü bitince kuyruğa eklenebilir."
            : string.Join(" · ", new[]
            {
                $"{segments.Count} ders, {UiCulture.FormatDuration(duration)}",
                archiveBytes is { } bytes ? $"tahmini {UiCulture.FormatSize(bytes)}" : $"yaklaşık {UiCulture.FormatSize(sourceBytes)}",
                prepared.Count == 0 ? "yalnızca birleştirilir, birkaç saniye sürer" : estimate,
            });

        BundleText = plans[0].Resources is { } bundle
            ? $"{bundle.Files.Count} dosya, {UiCulture.FormatSize(bundle.Bytes)} → {Path.GetRelativePath(OutputFolder, bundle.ArchivePath)}"
            : null;

        var skipped = analysis.Videos.Where(v => !v.IsUsable).Select(v => v.File.RelativePath).ToList();
        SkippedText = skipped.Count == 0 ? null : $"{skipped.Count} dosya okunamadığı için dışarıda kalacak: {string.Join(", ", skipped.Take(3))}{(skipped.Count > 3 ? "…" : "")}";
        SpaceWarning = CheckFreeSpace(plans[0].OutputPath, prepared, sourceBytes, analysis, archiveBytes, plans[0].Resources?.Bytes ?? 0);

        // Same order as the tracks inside each video: English, Turkish, others, unlabelled.
        foreach (var language in plans.SelectMany(p => p.SubtitleTracks).GroupBy(t => t.Language)
                     .OrderBy(g => g.Key switch { "en" => 0, "tr" => 1, "" => 3, _ => 2 }))
        {
            var count = language.Sum(t => t.Files.Count(f => f is not null));
            Tracks.Add(new SubtitleTrackRow(
                language.First().Title,
                $"{count} / {segments.Count} derste",
                count == segments.Count ? StatusKind.Success : StatusKind.Warning));
        }
        SubtitleNote = Tracks.Count == 0
            ? "Bu klasörde birleştirilecek altyazı yok."
            : "Altyazısı olmayan derslerde o dilin track'i boş geçer; diğer derslerin altyazıları kaymaz.";

        if (IsSplit)
        {
            for (var i = 0; i < plans.Count; i++)
                Rows.Add(VideoRow(i + 1, plans[i]));
            return;
        }

        var start = TimeSpan.Zero;
        for (var i = 0; i < plans[0].Segments.Count; i++)
        {
            var segment = plans[0].Segments[i];
            var (action, kind) = segment.Action switch
            {
                SegmentAction.Copy => ("Kopyalanır", StatusKind.Success),
                SegmentAction.Remux => ("Yeniden paketlenir", StatusKind.Info),
                SegmentAction.EncodeAudio => ("Ses uyarlanır", StatusKind.Info),
                _ when archiveQuality is not null => ("Sıkıştırılır", StatusKind.Info),
                _ => ("Yeniden kodlanır", StatusKind.Warning),
            };
            Rows.Add(new ChapterRow(i + 1, segment.SourcePath, segment.Title, UiCulture.FormatClock(start), action, kind));
            start += segment.Duration;
        }
    }

    /// <summary>One output of the per-subfolder mode: its file, length and what happens to its lectures.</summary>
    private static ChapterRow VideoRow(int number, MergePlan plan)
    {
        var lectures = $"{plan.Segments.Count} ders";
        var encoded = plan.Segments.Count(s => s.Action == SegmentAction.EncodeVideo);
        var (action, kind) = plan.ArchiveQuality is not null ? ($"{lectures} · sıkıştırılır", StatusKind.Info)
            : encoded > 0 ? ($"{lectures} · {encoded} kodlanır", StatusKind.Warning)
            : plan.Prepared.Count > 0 ? ($"{lectures} · uyarlanır", StatusKind.Info)
            : ($"{lectures} · kopyalanır", StatusKind.Success);
        return new ChapterRow(number, plan.Segments[0].SourcePath, Path.GetFileName(plan.OutputPath), UiCulture.FormatClock(plan.TotalDuration), action, kind);
    }

    private static string FormatTarget(FormatSignature t) =>
        $"{UiCulture.FormatCodec(t.VideoCodec)} · {t.Width}×{t.Height}{(t.PixelFormat.Contains("10", StringComparison.Ordinal) ? " · 10-bit" : "")}"
        + (t.AudioCodec is null ? " · sessiz" : $" · {UiCulture.FormatCodec(t.AudioCodec)} {t.SampleRate / 1000.0:0.#} kHz {UiCulture.FormatChannels(t.Channels)}");

    /// <summary>
    /// The output is about the size of the sources (or the archive estimate); prepared segments need room next
    /// to it until the join ends. In archive mode nearly everything is prepared, so that's the estimate again.
    /// </summary>
    private string? CheckFreeSpace(string outputPath, IReadOnlyList<MergeSegment> prepared, long sourceBytes, FolderAnalysis analysis, long? archiveBytes, long resourceBytes)
    {
        try
        {
            var preparedSources = prepared.Select(s => s.SourcePath).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var needed = resourceBytes
                + (archiveBytes * 2 ?? sourceBytes + analysis.Videos.Where(v => preparedSources.Contains(v.File.FullPath)).Sum(v => v.SizeBytes));
            var free = new DriveInfo(Path.GetPathRoot(outputPath)!).AvailableFreeSpace;
            return free >= needed
                ? null
                : $"Hedef diskte yer yetmeyebilir: yaklaşık {UiCulture.FormatSize(needed)} gerekiyor, {UiCulture.FormatSize(free)} boş.";
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
        {
            _logger.LogDebug(ex, "Free space check skipped for {Output}", outputPath);
            return null;
        }
    }
}
