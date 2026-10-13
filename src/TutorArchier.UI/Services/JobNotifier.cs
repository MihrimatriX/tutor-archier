using System.Windows;
using System.Windows.Media.Imaging;
using Hardcodet.Wpf.TaskbarNotification;
using TutorArchier.Core.Jobs;
using TutorArchier.Engine.Merge;
using TutorArchier.UI.Views.Pages;
using Wpf.Ui;

namespace TutorArchier.UI.Services;

/// <summary>
/// A Windows notification when a merge finishes or fails while the user is looking elsewhere; with the window in
/// front, the Queue page already says so. The tray icon that carries it only appears with the first notice.
/// </summary>
public sealed class JobNotifier(JobScheduler scheduler, INavigationService navigation) : IDisposable
{
    private readonly Dictionary<long, JobStatus> _seen = [];
    private TaskbarIcon? _tray;

    public void Start()
    {
        foreach (var job in scheduler.Jobs)
            _seen[job.Id] = job.Status;
        scheduler.Changed += (_, _) => UiDispatcher.Post(Check);
    }

    public void Dispose() => _tray?.Dispose();

    private void Check()
    {
        var watching = Application.Current?.MainWindow is { IsActive: true, WindowState: not WindowState.Minimized };
        foreach (var job in scheduler.Jobs)
        {
            // A job seen for the first time starts as queued, so only real transitions notify.
            var before = _seen.GetValueOrDefault(job.Id, JobStatus.Queued);
            _seen[job.Id] = job.Status;
            if (before == job.Status || watching)
                continue;

            if (job.Status == JobStatus.Completed)
                Show("Birleştirme bitti", $"{job.Name} hazır.", BalloonIcon.Info);
            else if (job.Status == JobStatus.Failed)
                Show("Birleştirme başarısız", $"{job.Name}: {job.Error}", BalloonIcon.Error);
        }
    }

    private void Show(string title, string message, BalloonIcon icon)
    {
        if (_tray is null)
        {
            _tray = new TaskbarIcon
            {
                IconSource = new BitmapImage(new Uri("pack://application:,,,/Assets/TutorArchier.ico")),
                ToolTipText = "TutorArchier",
            };
            _tray.TrayBalloonTipClicked += (_, _) => ShowQueue();
            _tray.TrayLeftMouseUp += (_, _) => ShowQueue();
        }
        _tray.ShowBalloonTip(title, message, icon);
    }

    private void ShowQueue()
    {
        if (Application.Current?.MainWindow is { } window)
        {
            if (window.WindowState == WindowState.Minimized)
                window.WindowState = WindowState.Normal;
            window.Activate();
        }
        navigation.Navigate(typeof(QueuePage));
    }
}
