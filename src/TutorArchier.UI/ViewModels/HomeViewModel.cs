using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TutorArchier.Engine.Hardware;
using TutorArchier.UI.Services;
using TutorArchier.UI.Views.Pages;
using Wpf.Ui;

namespace TutorArchier.UI.ViewModels;

/// <summary>A recently opened course on Home.</summary>
public sealed record RecentRow(string Path, string Name, string? Cover, string Summary, string OpenedAt);

public sealed partial class HomeViewModel : ObservableObject
{
    private readonly INavigationService _navigation;
    private readonly HardwareService _hardware;

    public HomeViewModel(INavigationService navigation, HardwareService hardware, WorkspaceState workspace)
    {
        _navigation = navigation;
        _hardware = hardware;
        Workspace = workspace;
        Workspace.PropertyChanged += OnWorkspaceChanged;
        _hardware.Changed += (_, _) => UiDispatcher.Post(RefreshFfmpegState);
        RefreshFfmpegState();
        RefreshRecents();
    }

    public WorkspaceState Workspace { get; }

    public ObservableCollection<RecentRow> Recents { get; } = [];

    [ObservableProperty]
    public partial bool HasRecents { get; set; }

    [ObservableProperty]
    public partial bool IsFfmpegMissing { get; set; }

    /// <summary>The course card shows its cover and size, so the selected folder is scanned here too.</summary>
    public async Task OnShownAsync()
    {
        RefreshRecents();
        await Workspace.AnalyzeAsync();
    }

    [RelayCommand]
    private void PickFolder()
    {
        var folder = FolderPicker.Pick("Video klasörünü seç", Workspace.SelectedFolder);
        if (folder is not null)
        {
            Workspace.SelectedFolder = folder;
            _ = Workspace.AnalyzeAsync();
        }
    }

    [RelayCommand(CanExecute = nameof(HasFolder))]
    private void Analyze() => _navigation.Navigate(typeof(AnalysisPage));

    [RelayCommand(CanExecute = nameof(HasFolder))]
    private void Merge() => _navigation.Navigate(typeof(MergePage));

    [RelayCommand]
    private void OpenRecent(RecentRow recent)
    {
        Workspace.SelectedFolder = recent.Path;
        Merge();
    }

    [RelayCommand]
    private void OpenSettings() => _navigation.Navigate(typeof(SettingsPage));

    /// <summary>Drop target: the half the folder was released on decides the flow (plan §6.4.2).</summary>
    public void OpenDroppedPath(string path, bool merge)
    {
        var folder = Directory.Exists(path) ? path : Path.GetDirectoryName(path);
        if (string.IsNullOrEmpty(folder))
            return;

        Workspace.SelectedFolder = folder;
        if (merge)
            Merge();
        else
            Analyze();
    }

    private bool HasFolder() => Workspace.HasFolder;

    private void OnWorkspaceChanged(object? sender, PropertyChangedEventArgs e)
    {
        AnalyzeCommand.NotifyCanExecuteChanged();
        MergeCommand.NotifyCanExecuteChanged();
        if (e.PropertyName == nameof(WorkspaceState.Recents))
            RefreshRecents();
    }

    private void RefreshRecents()
    {
        Recents.Clear();
        foreach (var recent in Workspace.Recents)
        {
            Recents.Add(new RecentRow(
                recent.Path,
                recent.Name,
                recent.Cover,
                $"{recent.Lectures} ders · {UiCulture.FormatDuration(recent.Duration)} · {UiCulture.FormatSize(recent.SizeBytes)}",
                UiCulture.FormatAgo(recent.OpenedAt)));
        }
        HasRecents = Recents.Count > 0;
    }

    private void RefreshFfmpegState() => IsFfmpegMissing = _hardware.IsInitialized && _hardware.Ffmpeg is null;
}
