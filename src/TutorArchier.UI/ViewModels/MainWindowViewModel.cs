using System.Windows.Shell;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using TutorArchier.Core.Jobs;
using TutorArchier.Core.Settings;
using TutorArchier.Engine.Hardware;
using TutorArchier.Engine.Merge;
using TutorArchier.UI.Controls;
using TutorArchier.UI.Services;
using TutorArchier.UI.Views.Pages;
using Wpf.Ui;

namespace TutorArchier.UI.ViewModels;

public sealed partial class MainWindowViewModel(
    INavigationService navigation,
    ISettingsStore settings,
    HardwareService hardware,
    JobScheduler scheduler,
    HomeViewModel home,
    OnboardingViewModel onboarding,
    ILogger<MainWindowViewModel> logger) : ObservableObject
{
    public OnboardingViewModel Onboarding { get; } = onboarding;

    [ObservableProperty]
    public partial bool IsOnboardingVisible { get; set; }

    [ObservableProperty]
    public partial StatusKind FfmpegStatus { get; set; } = StatusKind.Running;

    [ObservableProperty]
    public partial string FfmpegStatusText { get; set; } = "ffmpeg aranıyor…";

    /// <summary>Running and waiting jobs, for the badge on the Queue menu item.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasActiveJobs))]
    public partial int ActiveJobs { get; set; }

    public bool HasActiveJobs => ActiveJobs > 0;

    /// <summary>The running job's progress on the app's taskbar button, so it shows while the window is minimised.</summary>
    [ObservableProperty]
    public partial TaskbarItemProgressState TaskbarState { get; set; }

    [ObservableProperty]
    public partial double TaskbarProgress { get; set; }

    public async Task InitializeAsync()
    {
        hardware.Changed += (_, _) => UiDispatcher.Post(RefreshFfmpegStatus);
        scheduler.Changed += (_, _) => UiDispatcher.Post(RefreshJobs);
        scheduler.ProgressChanged += (_, p) => UiDispatcher.Post(() => TaskbarProgress = p.Progress.Fraction);
        RefreshJobs();
        // Jobs cut off by the last shutdown resume as soon as ffmpeg is found.
        scheduler.Start();

        if (!settings.Get(AppSettingKeys.OnboardingCompleted))
        {
            IsOnboardingVisible = true;
            Onboarding.Completed += (_, _) => IsOnboardingVisible = false;
            await Onboarding.StartAsync();
            return;
        }

        try
        {
            // Cached report unless ffmpeg or the GPU driver changed since the last run.
            await hardware.InitializeAsync();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Startup hardware check failed");
        }
    }

    private void RefreshFfmpegStatus()
    {
        (FfmpegStatus, FfmpegStatusText) = hardware.Ffmpeg switch
        {
            { Version: { } v } => (StatusKind.Success, $"ffmpeg {v.ToString(v.Build > 0 ? 3 : 2)}"),
            { } ffmpeg => (StatusKind.Success, $"ffmpeg {ffmpeg.VersionText}"),
            null when hardware.IsBusy || !hardware.IsInitialized => (StatusKind.Running, "ffmpeg aranıyor…"),
            null => (StatusKind.Danger, "ffmpeg yok"),
        };
    }

    private void RefreshJobs()
    {
        var jobs = scheduler.Jobs;
        ActiveJobs = jobs.Count(j => j.Status is JobStatus.Running or JobStatus.Queued);
        TaskbarState = jobs.Any(j => j.Status == JobStatus.Running) ? TaskbarItemProgressState.Normal
            : jobs.Any(j => j.Status == JobStatus.Paused) ? TaskbarItemProgressState.Paused
            : TaskbarItemProgressState.None;
    }

    [RelayCommand]
    private void Navigate(Type pageType) => navigation.Navigate(pageType);

    [RelayCommand]
    private void OpenFolder()
    {
        navigation.Navigate(typeof(HomePage));
        home.PickFolderCommand.Execute(null);
    }
}
