using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TutorArchier.Core.Jobs;
using TutorArchier.Engine.Merge;
using TutorArchier.UI.Controls;
using TutorArchier.UI.Services;

namespace TutorArchier.UI.ViewModels;

/// <summary>One merge job on the Queue page. Commands go straight to the scheduler.</summary>
public sealed partial class JobRow(JobRecord job, JobScheduler scheduler) : ObservableObject
{
    public long Id => job.Id;

    public string Name => job.Name;

    public string OutputPath => job.OutputPath;

    /// <summary>The first lecture, whose still stands for the job.</summary>
    public string? Cover { get; } = FirstLecture(job.PlanJson);

    public JobStatus Status => job.Status;

    public bool CanPause => Status is JobStatus.Queued or JobStatus.Running;

    public bool CanResume => Status is JobStatus.Paused or JobStatus.Failed;

    public bool CanCancel => Status is JobStatus.Queued or JobStatus.Running or JobStatus.Paused or JobStatus.Failed;

    public bool CanOpen => Status == JobStatus.Completed && File.Exists(OutputPath);

    public bool CanRemove => Status is not JobStatus.Running;

    public bool IsActive => Status is JobStatus.Running or JobStatus.Queued or JobStatus.Paused;

    public (StatusKind Kind, string Text) Badge => Status switch
    {
        JobStatus.Queued => (StatusKind.Pending, "Sırada"),
        JobStatus.Running => (StatusKind.Running, "Çalışıyor"),
        JobStatus.Paused => (StatusKind.Info, "Duraklatıldı"),
        JobStatus.Completed => (StatusKind.Success, "Tamamlandı"),
        JobStatus.Failed => (StatusKind.Danger, "Başarısız"),
        _ => (StatusKind.None, "İptal edildi"),
    };

    public StatusKind BadgeKind => Badge.Kind;

    public string BadgeText => Badge.Text;

    [ObservableProperty]
    public partial double Progress { get; set; } = job.TotalSteps == 0 ? 0 : 100.0 * job.CompletedSteps / job.TotalSteps;

    [ObservableProperty]
    public partial string Detail { get; set; } = job.Status switch
    {
        JobStatus.Failed => job.Error ?? "Bilinmeyen hata",
        JobStatus.Completed => $"Eklendi: {UiCulture.FormatDateTime(job.CreatedAt)}",
        JobStatus.Cancelled => "İş iptal edildi; ara dosyalar silindi.",
        _ => $"{job.CompletedSteps} / {job.TotalSteps} adım tamamlandı",
    };

    public void Update(MergeProgress progress)
    {
        Progress = progress.Fraction * 100;
        Detail = string.Create(UiCulture.Current, $"{progress.Text} · %{progress.Fraction * 100:0}");
    }

    [RelayCommand]
    private void Pause() => scheduler.Pause(Id);

    [RelayCommand]
    private void Resume() => scheduler.Resume(Id);

    [RelayCommand]
    private void Cancel() => scheduler.Cancel(Id);

    [RelayCommand]
    private void Remove() => scheduler.Remove(Id);

    [RelayCommand]
    private void Open() => Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{OutputPath}\"") { UseShellExecute = true });

    private static string? FirstLecture(string planJson)
    {
        try
        {
            return MergePlan.FromJson(planJson).Segments is [var first, ..] ? first.SourcePath : null;
        }
        catch (JsonException)
        {
            return null; // a plan from another build: the card just keeps its placeholder
        }
    }
}

/// <summary>Phase 2 queue: merge jobs in the order they run.</summary>
public sealed partial class QueueViewModel : ObservableObject
{
    private readonly JobScheduler _scheduler;

    public QueueViewModel(JobScheduler scheduler)
    {
        _scheduler = scheduler;
        _scheduler.Changed += (_, _) => UiDispatcher.Post(Refresh);
        _scheduler.ProgressChanged += (_, p) => UiDispatcher.Post(() => Jobs.FirstOrDefault(j => j.Id == p.JobId)?.Update(p.Progress));
        Refresh();
    }

    public ObservableCollection<JobRow> Jobs { get; } = [];

    [ObservableProperty]
    public partial bool IsEmpty { get; set; }

    [ObservableProperty]
    public partial bool HasFinished { get; set; }

    /// <summary>Clears completed and cancelled jobs from the list; their MKVs stay where they are.</summary>
    [RelayCommand]
    private void ClearFinished()
    {
        foreach (var job in Jobs.Where(j => j.Status is JobStatus.Completed or JobStatus.Cancelled).ToList())
            _scheduler.Remove(job.Id);
    }

    private void Refresh()
    {
        Jobs.Clear();
        // Active work first (in run order), then history newest first.
        var jobs = _scheduler.Jobs;
        foreach (var job in jobs.Where(IsActive).Concat(jobs.Where(j => !IsActive(j)).Reverse()))
            Jobs.Add(new JobRow(job, _scheduler));

        if (_scheduler.CurrentProgress is { } progress)
            Jobs.FirstOrDefault(j => j.Id == progress.JobId)?.Update(progress.Progress);

        IsEmpty = Jobs.Count == 0;
        HasFinished = Jobs.Any(j => j.Status is JobStatus.Completed or JobStatus.Cancelled);

        static bool IsActive(JobRecord job) => job.Status is JobStatus.Running or JobStatus.Queued or JobStatus.Paused;
    }
}
