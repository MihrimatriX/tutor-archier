using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Serilog;
using Serilog.Events;
using TutorArchier.Core;
using TutorArchier.Core.Hardware;
using TutorArchier.Core.Jobs;
using TutorArchier.Core.Media;
using TutorArchier.Core.Settings;
using TutorArchier.Data;
using TutorArchier.Engine.Ffmpeg;
using TutorArchier.Engine.Hardware;
using TutorArchier.Engine.Media;
using TutorArchier.Engine.Merge;
using TutorArchier.UI.Controls;
using TutorArchier.UI.Services;
using TutorArchier.UI.ViewModels;
using TutorArchier.UI.Views;
using TutorArchier.UI.Views.Pages;
using Wpf.Ui;
using Wpf.Ui.DependencyInjection;

namespace TutorArchier.UI;

public partial class App : Application
{
    private IHost? _host;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        ConfigureLogging();
        HookUnhandledExceptions();
        ChainMouseWheel();

        // The UI is Turkish-first; bindings format dates and numbers with tr-TR regardless of the OS language.
        FrameworkElement.LanguageProperty.OverrideMetadata(
            typeof(FrameworkElement),
            new FrameworkPropertyMetadata(XmlLanguage.GetLanguage(UiCulture.Current.IetfLanguageTag)));

        try
        {
            _host = BuildHost();
            _host.Services.GetRequiredService<AppDatabase>().Initialize();
            await _host.StartAsync();

            ConfigureRenderMode(_host.Services.GetRequiredService<ISettingsStore>(), e.Args);
            _host.Services.GetRequiredService<AppearanceService>().Initialize();

            var thumbnails = _host.Services.GetRequiredService<ThumbnailCache>();
            var hardware = _host.Services.GetRequiredService<HardwareService>();
            VideoThumb.Loader = video => hardware.Ffmpeg is { } ffmpeg ? thumbnails.GetAsync(ffmpeg, video) : Task.FromResult<string?>(null);

            var window = _host.Services.GetRequiredService<MainWindow>();
            MainWindow = window;
            window.Show();
            _host.Services.GetRequiredService<JobNotifier>().Start();

            // "TutorArchier.exe <folder>" (shortcuts, Open with) starts with that folder selected.
            if (e.Args.FirstOrDefault(Directory.Exists) is { } folder)
                _host.Services.GetRequiredService<WorkspaceState>().SelectedFolder = Path.GetFullPath(folder);
            Log.Information("TutorArchier started");
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "Startup failed");
            MessageBox.Show(
                $"TutorArchier başlatılamadı.\n\n{ex.Message}\n\nAyrıntılar: {AppPaths.LogsDirectory}",
                "TutorArchier",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (_host is not null)
        {
            // Host internals use ConfigureAwait(false), so blocking here cannot deadlock the dispatcher.
            _host.StopAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
            _host.Dispose();
        }
        Log.CloseAndFlush();
        base.OnExit(e);
    }

    private static IHost BuildHost()
    {
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
        {
            DisableDefaults = true,
            ContentRootPath = AppContext.BaseDirectory,
        });
        var services = builder.Services;

        services.AddSerilog();

        // Data
        services.AddSingleton(new AppDatabase(AppPaths.DatabasePath));
        services.AddSingleton<ISettingsStore, SqliteSettingsStore>();
        services.AddSingleton<IHardwareReportStore, SqliteHardwareReportStore>();
        services.AddSingleton<IProbeCache, SqliteProbeCache>();
        services.AddSingleton<IJobStore, SqliteJobStore>();

        // Engine
        services.AddSingleton<FfmpegRunner>();
        services.AddSingleton<FfmpegLocator>();
        services.AddSingleton<FfmpegInstaller>();
        services.AddSingleton<EncoderProber>();
        services.AddSingleton<EncoderBenchmark>();
        services.AddSingleton<HardwareService>();
        services.AddSingleton<MediaProber>();
        services.AddSingleton<FolderAnalyzer>();
        services.AddSingleton<ThumbnailCache>();
        services.AddSingleton<MergeRunner>();
        services.AddSingleton<QualitySearch>();
        services.AddSingleton<JobScheduler>();

        // UI services
        services.AddSingleton<AppearanceService>();
        services.AddSingleton<WorkspaceState>();
        services.AddSingleton<JobNotifier>();
        services.AddSingleton<INavigationService, NavigationService>();
        services.AddNavigationViewPageProvider();

        // Shell
        services.AddSingleton<MainWindow>();
        services.AddSingleton<MainWindowViewModel>();
        services.AddSingleton<OnboardingViewModel>();

        // Pages keep their state while the user moves between them.
        services.AddSingleton<HomePage>();
        services.AddSingleton<HomeViewModel>();
        services.AddSingleton<AnalysisPage>();
        services.AddSingleton<AnalysisViewModel>();
        services.AddSingleton<MergePage>();
        services.AddSingleton<MergeViewModel>();
        services.AddSingleton<QueuePage>();
        services.AddSingleton<QueueViewModel>();
        services.AddSingleton<SettingsPage>();
        services.AddSingleton<SettingsViewModel>();

        return builder.Build();
    }

    /// <summary>
    /// Software rendering unless the user opted in. On some GPU drivers (seen with a 2021 AMD driver)
    /// WPF's Direct3D path presents nothing, leaving every window blank or a flat colour. This only
    /// affects drawing the UI; ffmpeg's hardware encoders are unrelated. <c>--software-ui</c> is the
    /// escape hatch if hardware drawing was enabled on such a machine. Must run before any window exists.
    /// </summary>
    private static void ConfigureRenderMode(ISettingsStore settings, string[] args)
    {
        var forceSoftware = args.Contains("--software-ui", StringComparer.OrdinalIgnoreCase);
        var hardware = settings.Get(AppSettingKeys.HardwareAcceleratedUi) && !forceSoftware;
        RenderOptions.ProcessRenderMode = hardware ? RenderMode.Default : RenderMode.SoftwareOnly;
        Log.Information("UI rendering: {Mode}", hardware ? "hardware" : "software");
    }

    private static void ConfigureLogging()
    {
        Directory.CreateDirectory(AppPaths.LogsDirectory);
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
            .Enrich.FromLogContext()
            .WriteTo.File(
                Path.Combine(AppPaths.LogsDirectory, "tutorarchier-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 14,
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}",
                formatProvider: CultureInfo.InvariantCulture)
            .CreateLogger();
    }

    private void HookUnhandledExceptions()
    {
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            Log.Fatal(args.ExceptionObject as Exception, "Unhandled exception");
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Log.Error(args.Exception, "Unobserved task exception");
            args.SetObserved();
        };
    }

    /// <summary>
    /// A ScrollViewer swallows every wheel notch even when it can't move, so a short table inside a page would
    /// stop the page from scrolling under the mouse. Such notches go to the nearest outer viewer that can move.
    /// </summary>
    private static void ChainMouseWheel() =>
        EventManager.RegisterClassHandler(typeof(ScrollViewer), UIElement.PreviewMouseWheelEvent, new MouseWheelEventHandler((sender, e) =>
        {
            var viewer = (ScrollViewer)sender;
            // Preview tunnels from the window down; only the viewer closest to the mouse decides.
            if (e.Handled || NearestViewer(e.OriginalSource as DependencyObject) != viewer || CanScroll(viewer, e.Delta))
                return;
            for (var outer = NearestViewer(Parent(viewer)); outer is not null; outer = NearestViewer(Parent(outer)))
            {
                if (!CanScroll(outer, e.Delta))
                    continue;
                e.Handled = true;
                outer.RaiseEvent(new MouseWheelEventArgs(e.MouseDevice, e.Timestamp, e.Delta) { RoutedEvent = UIElement.MouseWheelEvent });
                return;
            }
        }));

    private static ScrollViewer? NearestViewer(DependencyObject? node)
    {
        while (node is not null and not ScrollViewer)
            node = Parent(node);
        return node as ScrollViewer;
    }

    // Text runs under the mouse are content elements, which only have a logical parent.
    private static DependencyObject? Parent(DependencyObject node) =>
        node is Visual or Visual3D ? VisualTreeHelper.GetParent(node) : LogicalTreeHelper.GetParent(node);

    private static bool CanScroll(ScrollViewer viewer, int delta) =>
        delta > 0 ? viewer.VerticalOffset > 0.5 : viewer.VerticalOffset < viewer.ScrollableHeight - 0.5;

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log.Error(e.Exception, "Unhandled UI exception");
        MessageBox.Show(
            $"Beklenmeyen bir hata oluştu:\n\n{e.Exception.Message}\n\nAyrıntılar günlük dosyasında: {AppPaths.LogsDirectory}",
            "TutorArchier",
            MessageBoxButton.OK,
            MessageBoxImage.Warning);
        e.Handled = true;
    }
}
