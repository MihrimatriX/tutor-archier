using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using TutorArchier.Core.Hardware;
using TutorArchier.Core.Settings;
using TutorArchier.Engine.Hardware;
using TutorArchier.UI.Controls;
using TutorArchier.UI.Services;

namespace TutorArchier.UI.ViewModels;

/// <summary>One row of the encoder table on Settings › ffmpeg ve donanım.</summary>
public sealed record EncoderRow(
    string Name,
    string Device,
    StatusKind Status,
    string StatusText,
    string? StatusDetail,
    StatusKind TenBit,
    string? TenBitDetail,
    StatusKind BFrames,
    string Speed,
    bool IsOutOfScope);

public sealed partial class SettingsViewModel : ObservableObject, IDisposable
{
    private readonly HardwareService _hardware;
    private readonly AppearanceService _theme;
    private readonly ISettingsStore _settings;
    private readonly ILogger<SettingsViewModel> _logger;
    private readonly bool _hardwareUiAtStartup;
    private CancellationTokenSource? _operation;

    public SettingsViewModel(HardwareService hardware, AppearanceService theme, ISettingsStore settings, ILogger<SettingsViewModel> logger)
    {
        _hardware = hardware;
        _theme = theme;
        _settings = settings;
        _logger = logger;
        _hardware.Changed += (_, _) => UiDispatcher.Post(Refresh);
        SelectedTheme = theme.Preference;
        _hardwareUiAtStartup = settings.Get(AppSettingKeys.HardwareAcceleratedUi);
        HardwareAcceleratedUi = _hardwareUiAtStartup;
        Refresh();
    }

    public ObservableCollection<EncoderRow> Encoders { get; } = [];

    public ObservableCollection<string> Gpus { get; } = [];

    public bool HasGpus => Gpus.Count > 0;

    public IReadOnlyList<KeyValuePair<AppTheme, string>> ThemeOptions { get; } =
    [
        new(AppTheme.System, "Sistem ayarını izle"),
        new(AppTheme.Dark, "Koyu"),
        new(AppTheme.Light, "Açık"),
    ];

    // ffmpeg
    [ObservableProperty]
    public partial StatusKind FfmpegStatus { get; set; }

    [ObservableProperty]
    public partial string FfmpegStatusText { get; set; } = "";

    [ObservableProperty]
    public partial string? FfmpegPath { get; set; }

    [ObservableProperty]
    public partial bool HasFfmpeg { get; set; }

    /// <summary>Offered when ffmpeg is missing or isn't the version the app was verified against.</summary>
    [ObservableProperty]
    public partial bool CanInstallFfmpeg { get; set; }

    [ObservableProperty]
    public partial StatusKind LibVmafStatus { get; set; }

    [ObservableProperty]
    public partial StatusKind SubtitlesStatus { get; set; }

    [ObservableProperty]
    public partial StatusKind ZscaleStatus { get; set; }

    [ObservableProperty]
    public partial string? VersionWarning { get; set; }

    [ObservableProperty]
    public partial string? FfmpegFolderError { get; set; }

    // Hardware
    [ObservableProperty]
    public partial string? CpuName { get; set; }

    [ObservableProperty]
    public partial string TestedAtText { get; set; } = "";

    [ObservableProperty]
    public partial string BenchmarkText { get; set; } = "";

    // Running operation
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RetestCommand), nameof(BenchmarkCommand), nameof(ChooseFfmpegFolderCommand), nameof(AutoLocateFfmpegCommand), nameof(InstallFfmpegCommand))]
    public partial bool IsBusy { get; set; }

    [ObservableProperty]
    public partial string? BusyText { get; set; }

    [ObservableProperty]
    public partial double Progress { get; set; }

    [ObservableProperty]
    public partial bool CanCancel { get; set; }

    /// <summary>Kept apart from <see cref="BusyText"/> so the service's "done" refresh can't wipe it.</summary>
    [ObservableProperty]
    public partial string? OperationError { get; set; }

    // Appearance
    [ObservableProperty]
    public partial AppTheme SelectedTheme { get; set; }

    partial void OnSelectedThemeChanged(AppTheme value) => _theme.SetPreference(value);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RestartRequired))]
    public partial bool HardwareAcceleratedUi { get; set; }

    /// <summary>The render mode is fixed per process, so a change only shows after a restart.</summary>
    public bool RestartRequired => HardwareAcceleratedUi != _hardwareUiAtStartup;

    partial void OnHardwareAcceleratedUiChanged(bool value) => _settings.Set(AppSettingKeys.HardwareAcceleratedUi, value);

    [RelayCommand(CanExecute = nameof(CanTest))]
    private Task RetestAsync() => RunOperationAsync("Encoder'lar test ediliyor", cancellable: false,
        (progress, token) => _hardware.DetectAsync(progress, token));

    [RelayCommand(CanExecute = nameof(CanBenchmark))]
    private Task BenchmarkAsync() => RunOperationAsync("Hız ölçülüyor", cancellable: true,
        (progress, token) => _hardware.BenchmarkAsync(progress, token));

    [RelayCommand]
    private void Cancel() => _operation?.Cancel();

    [RelayCommand(CanExecute = nameof(IsIdle))]
    private async Task ChooseFfmpegFolderAsync()
    {
        var folder = FolderPicker.Pick("ffmpeg.exe ve ffprobe.exe'nin bulunduğu klasörü seç",
            FfmpegPath is null ? null : Path.GetDirectoryName(FfmpegPath));
        if (folder is null)
            return;

        FfmpegFolderError = null;
        if (await _hardware.UseFfmpegDirectoryAsync(folder) is null)
        {
            FfmpegFolderError = "Bu klasörde çalışan bir ffmpeg.exe ve ffprobe.exe bulunamadı.";
            return;
        }
        await RetestAsync();
    }

    /// <summary>Download, then test encoders in the same run so the table fills in without another click.</summary>
    [RelayCommand(CanExecute = nameof(IsIdle))]
    private Task InstallFfmpegAsync()
    {
        FfmpegFolderError = null;
        return RunOperationAsync("ffmpeg indiriliyor", cancellable: true, async (progress, token) =>
        {
            if (await _hardware.InstallFfmpegAsync(progress, token) is not null)
                await _hardware.EnsureReportAsync(progress, token);
        });
    }

    [RelayCommand(CanExecute = nameof(IsIdle))]
    private async Task AutoLocateFfmpegAsync()
    {
        FfmpegFolderError = null;
        if (await _hardware.AutoLocateFfmpegAsync() is not null)
            await _hardware.EnsureReportAsync();
    }

    private bool IsIdle() => !IsBusy;

    private bool CanTest() => !IsBusy && HasFfmpeg;

    private bool CanBenchmark() => !IsBusy && _hardware.Report is not null;

    private async Task RunOperationAsync(
        string busyText,
        bool cancellable,
        Func<IProgress<HardwareProgress>, CancellationToken, Task> operation)
    {
        _operation = new CancellationTokenSource();
        BusyText = busyText;
        OperationError = null;
        Progress = 0;
        CanCancel = cancellable;
        try
        {
            await operation(
                new Progress<HardwareProgress>(p =>
                {
                    Progress = p.Fraction * 100;
                    BusyText = p.Message;
                }),
                _operation.Token);
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("{Operation} cancelled by the user", busyText);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "{Operation} failed", busyText);
            OperationError = $"İşlem tamamlanamadı: {ex.Message}";
        }
        finally
        {
            CanCancel = false;
            _operation.Dispose();
            _operation = null;
        }
    }

    /// <summary>Stops a running benchmark when the app shuts down.</summary>
    public void Dispose() => _operation?.Cancel();

    /// <summary>Mirrors <see cref="HardwareService"/> state; called on the UI thread.</summary>
    public void Refresh()
    {
        IsBusy = _hardware.IsBusy || !_hardware.IsInitialized;
        if (!_hardware.IsBusy)
            BusyText = null;
        RetestCommand.NotifyCanExecuteChanged();
        BenchmarkCommand.NotifyCanExecuteChanged();

        RefreshFfmpeg(_hardware.Ffmpeg);
        RefreshHardware(_hardware.Report);
    }

    private void RefreshFfmpeg(FfmpegInfo? ffmpeg)
    {
        HasFfmpeg = ffmpeg is not null;
        CanInstallFfmpeg = ffmpeg is not { IsTestedVersion: true };
        FfmpegPath = ffmpeg?.FfmpegPath;

        if (ffmpeg is null)
        {
            FfmpegStatus = _hardware.IsInitialized ? StatusKind.Danger : StatusKind.Running;
            FfmpegStatusText = _hardware.IsInitialized ? "ffmpeg bulunamadı" : "ffmpeg aranıyor…";
            LibVmafStatus = SubtitlesStatus = ZscaleStatus = StatusKind.Pending;
            VersionWarning = null;
            return;
        }

        FfmpegStatus = StatusKind.Success;
        FfmpegStatusText = $"ffmpeg {ffmpeg.VersionText}";
        LibVmafStatus = ffmpeg.HasLibVmaf ? StatusKind.Success : StatusKind.Danger;
        SubtitlesStatus = ffmpeg.HasSubtitlesFilter ? StatusKind.Success : StatusKind.Danger;
        ZscaleStatus = ffmpeg.HasZscale ? StatusKind.Success : StatusKind.Danger;
        VersionWarning = ffmpeg.IsTestedVersion
            ? null
            : $"Bu sürüm test edilmedi. Komut şablonları ffmpeg {FfmpegInfo.TestedVersion.Major}.{FfmpegInfo.TestedVersion.Minor} ile doğrulandı.";
    }

    private void RefreshHardware(HardwareReport? report)
    {
        CpuName = report?.CpuName;
        Gpus.Clear();
        foreach (var gpu in report?.Gpus ?? [])
            Gpus.Add(gpu.DriverVersion is null ? gpu.Name : $"{gpu.Name} · sürücü {gpu.DriverVersion}");
        OnPropertyChanged(nameof(HasGpus));

        TestedAtText = report is null
            ? (_hardware.IsBusy ? "Test ediliyor…" : "Henüz test edilmedi")
            : $"Son test: {UiCulture.FormatDateTime(report.TestedAt)}";
        BenchmarkText = report?.BenchmarkedAt is { } benchmarked
            ? $"Son hız ölçümü: {UiCulture.FormatDateTime(benchmarked)}"
            : "Hız ölçümü yapılmadı · süre tahminleri yaklaşık";

        Encoders.Clear();
        foreach (var encoder in EncoderCatalog.All)
            Encoders.Add(BuildRow(encoder, report?.Find(encoder.Id)));
    }

    private static EncoderRow BuildRow(EncoderDefinition encoder, EncoderCapability? capability)
    {
        var device = EncoderCatalog.VendorLabel(encoder.Vendor);
        if (capability is null)
            return new EncoderRow(encoder.DisplayName, device, StatusKind.Pending, "Test edilmedi", null,
                StatusKind.None, null, StatusKind.None, "—", !encoder.UsedForEncoding);

        if (!capability.Available)
            return new EncoderRow(encoder.DisplayName, device, StatusKind.Danger, capability.FailureReason ?? "Çalışmıyor",
                capability.FailureDetail, StatusKind.None, null, StatusKind.None, "—", !encoder.UsedForEncoding);

        var speed = capability.BenchmarkFps is { } fps ? string.Create(UiCulture.Current, $"{fps:F0} fps") : "—";
        return new EncoderRow(
            encoder.DisplayName,
            device,
            StatusKind.Success,
            "Çalışıyor",
            null,
            capability.TenBit ? StatusKind.Success : StatusKind.Danger,
            capability.TenBitFailureReason,
            capability.BFrames ? StatusKind.Success : StatusKind.Danger,
            speed,
            !encoder.UsedForEncoding);
    }
}
