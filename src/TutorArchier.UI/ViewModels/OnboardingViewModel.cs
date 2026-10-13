using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using TutorArchier.Core.Hardware;
using TutorArchier.Core.Settings;
using TutorArchier.Engine.Hardware;
using TutorArchier.UI.Controls;
using TutorArchier.UI.Services;

namespace TutorArchier.UI.ViewModels;

/// <summary>First-run flow (plan §6.4.1): find ffmpeg → test encoders → optional speed test.</summary>
public sealed partial class OnboardingViewModel(
    HardwareService hardware,
    ISettingsStore settings,
    ILogger<OnboardingViewModel> logger) : ObservableObject, IDisposable
{
    // Share of the progress bar given to encoder tests; the benchmark fills the rest.
    private const double DetectionShare = 55;

    private CancellationTokenSource? _benchmarkCancellation;
    private CancellationTokenSource? _installCancellation;

    public event EventHandler? Completed;

    public ObservableCollection<CapabilityChip> Chips { get; } = [];

    [ObservableProperty]
    public partial StatusKind FfmpegStatus { get; set; } = StatusKind.Pending;

    [ObservableProperty]
    public partial string FfmpegText { get; set; } = "ffmpeg";

    [ObservableProperty]
    public partial StatusKind HardwareStatus { get; set; } = StatusKind.Pending;

    [ObservableProperty]
    public partial string HardwareText { get; set; } = "Donanım testi";

    [ObservableProperty]
    public partial StatusKind BenchmarkStatus { get; set; } = StatusKind.Pending;

    [ObservableProperty]
    public partial string BenchmarkText { get; set; } = "Hız ölçümü (yaklaşık 30 sn)";

    [ObservableProperty]
    public partial double Progress { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ChooseFfmpegFolderCommand), nameof(RetryCommand), nameof(InstallFfmpegCommand))]
    public partial bool IsBusy { get; set; }

    [ObservableProperty]
    public partial bool IsInstalling { get; set; }

    [ObservableProperty]
    public partial double InstallProgress { get; set; }

    [ObservableProperty]
    public partial string InstallText { get; set; } = "";

    [ObservableProperty]
    public partial bool IsFfmpegMissing { get; set; }

    [ObservableProperty]
    public partial bool IsBenchmarkRunning { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(FinishCommand))]
    public partial bool IsFinished { get; set; }

    [ObservableProperty]
    public partial string? FfmpegError { get; set; }

    public async Task StartAsync()
    {
        IsBusy = true;
        try
        {
            await RunAsync();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Onboarding checks failed");
            HardwareStatus = StatusKind.Danger;
            HardwareText = "Donanım testi tamamlanamadı. Ayarlar'dan tekrar deneyebilirsin.";
            Progress = 100;
            IsFinished = true;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task RunAsync()
    {
        Reset();

        FfmpegStatus = StatusKind.Running;
        FfmpegText = "ffmpeg aranıyor…";
        var ffmpeg = await hardware.LocateAsync();
        if (ffmpeg is null)
        {
            FfmpegStatus = StatusKind.Danger;
            FfmpegText = "ffmpeg bulunamadı";
            IsFfmpegMissing = true;
            return;
        }

        FfmpegStatus = StatusKind.Success;
        FfmpegText = DescribeFfmpeg(ffmpeg);

        HardwareStatus = StatusKind.Running;
        HardwareText = "Donanım test ediliyor…";
        var gpuVendors = (await Task.Run(SystemInfo.GetGpus)).Select(g => g.Vendor).ToHashSet();
        var report = await hardware.DetectAsync(new Progress<HardwareProgress>(p =>
        {
            Progress = p.Fraction * DetectionShare;
            if (p.Completed is { } capability && EncoderCatalog.Find(capability.EncoderId) is { } encoder)
            {
                foreach (var chip in CapabilityChips.For(encoder, capability, gpuVendors))
                    Chips.Add(chip);
            }
        }));

        var usable = report?.Encoders.Count(c => c.Available && EncoderCatalog.Find(c.EncoderId)?.UsedForEncoding == true) ?? 0;
        HardwareStatus = usable > 0 ? StatusKind.Success : StatusKind.Warning;
        HardwareText = usable > 0
            ? $"Donanım testi tamamlandı · {usable} encoder kullanılabilir"
            : "Kullanılabilir encoder bulunamadı";

        await RunBenchmarkAsync();
        Progress = 100;
        IsFinished = true;
    }

    private async Task RunBenchmarkAsync()
    {
        _benchmarkCancellation = new CancellationTokenSource();
        BenchmarkStatus = StatusKind.Running;
        IsBenchmarkRunning = true;
        try
        {
            await hardware.BenchmarkAsync(
                new Progress<HardwareProgress>(p =>
                {
                    Progress = DetectionShare + p.Fraction * (100 - DetectionShare);
                    BenchmarkText = $"Hız ölçümü · {p.Message}";
                }),
                _benchmarkCancellation.Token);
            BenchmarkStatus = StatusKind.Success;
            BenchmarkText = "Hız ölçümü tamamlandı";
        }
        catch (OperationCanceledException)
        {
            BenchmarkStatus = StatusKind.Warning;
            BenchmarkText = "Hız ölçümü atlandı · süre tahminleri yaklaşık olacak";
        }
        finally
        {
            IsBenchmarkRunning = false;
            _benchmarkCancellation.Dispose();
            _benchmarkCancellation = null;
        }
    }

    [RelayCommand]
    private void SkipBenchmark() => _benchmarkCancellation?.Cancel();

    [RelayCommand(CanExecute = nameof(CanChangeFfmpeg))]
    private async Task ChooseFfmpegFolderAsync()
    {
        var folder = FolderPicker.Pick("ffmpeg.exe ve ffprobe.exe'nin bulunduğu klasörü seç");
        if (folder is null)
            return;

        IsBusy = true;
        try
        {
            if (await hardware.UseFfmpegDirectoryAsync(folder) is null)
            {
                FfmpegError = "Bu klasörde çalışan bir ffmpeg.exe ve ffprobe.exe bulunamadı.";
                return;
            }
        }
        finally
        {
            IsBusy = false;
        }

        await StartAsync();
    }

    [RelayCommand(CanExecute = nameof(CanChangeFfmpeg))]
    private async Task InstallFfmpegAsync()
    {
        _installCancellation = new CancellationTokenSource();
        IsBusy = true;
        IsInstalling = true;
        FfmpegError = null;
        FfmpegStatus = StatusKind.Running;
        FfmpegText = "ffmpeg kuruluyor…";
        try
        {
            var installed = await hardware.InstallFfmpegAsync(
                new Progress<HardwareProgress>(p =>
                {
                    InstallProgress = p.Fraction * 100;
                    InstallText = p.Message;
                }),
                _installCancellation.Token);
            if (installed is null)
                FfmpegError = "ffmpeg indirildi ama çalıştırılamadı.";
        }
        catch (OperationCanceledException)
        {
            // Back to the "missing" card; nothing was written.
        }
        catch (InvalidOperationException ex)
        {
            FfmpegError = ex.Message;
        }
        finally
        {
            IsInstalling = false;
            IsBusy = false;
            _installCancellation.Dispose();
            _installCancellation = null;
        }

        if (hardware.Ffmpeg is null)
        {
            FfmpegStatus = StatusKind.Danger;
            FfmpegText = "ffmpeg bulunamadı";
            return;
        }
        await StartAsync();
    }

    [RelayCommand]
    private void CancelInstall() => _installCancellation?.Cancel();

    [RelayCommand(CanExecute = nameof(CanChangeFfmpeg))]
    private Task RetryAsync() => StartAsync();

    private bool CanChangeFfmpeg() => !IsBusy;

    [RelayCommand(CanExecute = nameof(IsFinished))]
    private void Finish() => Complete();

    /// <summary>Lets the user into the app without ffmpeg; Home and Settings keep showing what's missing.</summary>
    [RelayCommand]
    private void SkipOnboarding() => Complete();

    private void Complete()
    {
        _benchmarkCancellation?.Cancel();
        _installCancellation?.Cancel();
        settings.Set(AppSettingKeys.OnboardingCompleted, true);
        Completed?.Invoke(this, EventArgs.Empty);
    }

    private void Reset()
    {
        Chips.Clear();
        Progress = 0;
        IsFinished = false;
        IsFfmpegMissing = false;
        FfmpegError = null;
        HardwareStatus = StatusKind.Pending;
        HardwareText = "Donanım testi";
        BenchmarkStatus = StatusKind.Pending;
        BenchmarkText = "Hız ölçümü (yaklaşık 30 sn)";
    }

    /// <summary>Stops a running benchmark or download when the app shuts down mid-onboarding.</summary>
    public void Dispose()
    {
        _benchmarkCancellation?.Cancel();
        _installCancellation?.Cancel();
    }

    internal static string DescribeFfmpeg(FfmpegInfo ffmpeg)
    {
        var version = ffmpeg.Version is { } v ? $"v{v.ToString(v.Build > 0 ? 3 : 2)}" : ffmpeg.VersionText;
        return $"ffmpeg bulundu ({version}, libvmaf {(ffmpeg.HasLibVmaf ? "✓" : "✕")})";
    }
}
