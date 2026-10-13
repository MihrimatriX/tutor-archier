using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using TutorArchier.Core;
using TutorArchier.Core.Hardware;
using TutorArchier.Core.Settings;
using TutorArchier.Engine.Ffmpeg;

namespace TutorArchier.Engine.Hardware;

/// <param name="Fraction">0..1 across the whole operation.</param>
/// <param name="Completed">Set when one encoder finished, so the UI can tick it off.</param>
public sealed record HardwareProgress(string Message, double Fraction, EncoderCapability? Completed = null);

/// <summary>
/// Owns "which ffmpeg, which encoders, how fast". Results are cached and only re-measured
/// when ffmpeg or the GPU drivers change (plan 4.7), or when the user asks.
/// Operations are serialized: two encoder tests never compete for the GPU.
/// </summary>
public sealed class HardwareService(
    FfmpegLocator locator,
    FfmpegInstaller installer,
    EncoderProber prober,
    EncoderBenchmark benchmark,
    ISettingsStore settings,
    IHardwareReportStore store,
    ILogger<HardwareService> logger) : IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    public FfmpegInfo? Ffmpeg { get; private set; }

    public HardwareReport? Report { get; private set; }

    /// <summary>True once ffmpeg has been searched for, whether or not it was found.</summary>
    public bool IsInitialized { get; private set; }

    /// <summary>True while a search, test or benchmark is running.</summary>
    public bool IsBusy { get; private set; }

    /// <summary>Raised (on a background thread) whenever any property above changes.</summary>
    public event EventHandler? Changed;

    /// <summary>Startup path: find ffmpeg, then reuse the cached report unless something changed.</summary>
    public async Task InitializeAsync(IProgress<HardwareProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        if (await LocateAsync(cancellationToken) is not null)
            await EnsureReportAsync(progress, cancellationToken);
    }

    /// <summary>Finds ffmpeg, honouring the user's folder choice.</summary>
    public Task<FfmpegInfo?> LocateAsync(CancellationToken cancellationToken = default) =>
        RunExclusiveAsync(async () =>
        {
            await installer.EnsureBundledAsync(cancellationToken);
            var found = await locator.LocateAsync(settings.Get(AppSettingKeys.FfmpegDirectory), cancellationToken);
            SetFfmpeg(found);
            IsInitialized = true;
            return found;
        }, cancellationToken);

    /// <summary>Uses the cached report when the fingerprint still matches, otherwise tests every encoder.</summary>
    public Task<HardwareReport?> EnsureReportAsync(IProgress<HardwareProgress>? progress = null, CancellationToken cancellationToken = default) =>
        RunExclusiveAsync(async () =>
        {
            if (Ffmpeg is null)
                return null;

            var gpus = SystemInfo.GetGpus();
            var cached = store.Load();
            if (cached is not null && cached.Fingerprint == ComputeFingerprint(Ffmpeg, gpus))
            {
                logger.LogInformation("Hardware report reused from cache ({TestedAt})", cached.TestedAt);
                Report = cached with { Ffmpeg = Ffmpeg };
                return Report;
            }

            logger.LogInformation("ffmpeg or GPU drivers changed; re-testing encoders");
            return await DetectCoreAsync(Ffmpeg, gpus, progress, cancellationToken);
        }, cancellationToken);

    /// <summary>Re-tests every encoder regardless of the cache.</summary>
    public Task<HardwareReport?> DetectAsync(IProgress<HardwareProgress>? progress = null, CancellationToken cancellationToken = default) =>
        RunExclusiveAsync(async () =>
            Ffmpeg is null ? null : await DetectCoreAsync(Ffmpeg, SystemInfo.GetGpus(), progress, cancellationToken),
            cancellationToken);

    /// <summary>Validates a user-chosen folder and remembers it. Returns null if it holds no usable ffmpeg.</summary>
    public Task<FfmpegInfo?> UseFfmpegDirectoryAsync(string directory, CancellationToken cancellationToken = default) =>
        RunExclusiveAsync(async () =>
        {
            var info = await locator.InspectAsync(directory, cancellationToken)
                ?? await locator.InspectAsync(Path.Combine(directory, "bin"), cancellationToken);
            if (info is null)
                return null;

            settings.Set(AppSettingKeys.FfmpegDirectory, Path.GetDirectoryName(info.FfmpegPath));
            SetFfmpeg(info);
            return info;
        }, cancellationToken);

    /// <summary>Downloads the pinned ffmpeg build and switches to it. Failures throw with a user-facing message.</summary>
    public Task<FfmpegInfo?> InstallFfmpegAsync(IProgress<HardwareProgress>? progress = null, CancellationToken cancellationToken = default) =>
        RunExclusiveAsync(async () =>
        {
            const double mb = 1024 * 1024;
            var directory = await installer.InstallAsync(
                bytes => progress?.Report(new HardwareProgress(
                    string.Create(CultureInfo.InvariantCulture, $"ffmpeg indiriliyor · {bytes / mb:F0} / {FfmpegInstaller.DownloadSize / mb:F0} MB"),
                    (double)bytes / FfmpegInstaller.DownloadSize)),
                cancellationToken);

            var info = await locator.InspectAsync(directory, cancellationToken);
            if (info is null)
                return null;

            settings.Set(AppSettingKeys.FfmpegDirectory, directory);
            SetFfmpeg(info);
            return info;
        }, cancellationToken);

    /// <summary>Forgets the user's folder and searches again.</summary>
    public Task<FfmpegInfo?> AutoLocateFfmpegAsync(CancellationToken cancellationToken = default) =>
        RunExclusiveAsync(async () =>
        {
            settings.Set(AppSettingKeys.FfmpegDirectory, null);
            var found = await locator.LocateAsync(null, cancellationToken);
            SetFfmpeg(found);
            return found;
        }, cancellationToken);

    /// <summary>Measures 1080p fps for every usable encoder. Nothing is saved if cancelled part-way.</summary>
    public Task<HardwareReport?> BenchmarkAsync(IProgress<HardwareProgress>? progress = null, CancellationToken cancellationToken = default) =>
        RunExclusiveAsync(async () =>
        {
            if (Ffmpeg is null || Report is null)
                return null;

            var workDirectory = AppPaths.CreateScratchDirectory("bench");
            try
            {
                var targets = Report.Encoders
                    .Where(c => c.Available)
                    .Select(c => (Capability: c, Definition: EncoderCatalog.Find(c.EncoderId)))
                    .Where(t => t.Definition is { UsedForEncoding: true })
                    .Select(t => (t.Capability, Definition: t.Definition!))
                    .ToList();

                progress?.Report(new HardwareProgress("Test videosu hazırlanıyor", 0));
                var source = await benchmark.CreateSourceAsync(Ffmpeg, workDirectory, cancellationToken);

                var measured = new Dictionary<string, double?>();
                for (var i = 0; i < targets.Count; i++)
                {
                    var (capability, definition) = targets[i];
                    progress?.Report(new HardwareProgress($"{definition.DisplayName} ölçülüyor", (double)i / targets.Count));
                    measured[capability.EncoderId] = await benchmark.MeasureAsync(Ffmpeg, definition, capability, source, cancellationToken);
                }

                Report = Report with
                {
                    Encoders = Report.Encoders
                        .Select(c => measured.TryGetValue(c.EncoderId, out var fps) ? c with { BenchmarkFps = fps } : c)
                        .ToList(),
                    BenchmarkedAt = DateTimeOffset.Now,
                };
                store.Save(Report);
                progress?.Report(new HardwareProgress("Hız ölçümü tamamlandı", 1));
                return Report;
            }
            finally
            {
                AppPaths.TryDeleteDirectory(workDirectory);
            }
        }, cancellationToken);

    private async Task<HardwareReport> DetectCoreAsync(
        FfmpegInfo ffmpeg,
        IReadOnlyList<GpuInfo> gpus,
        IProgress<HardwareProgress>? progress,
        CancellationToken cancellationToken)
    {
        var workDirectory = AppPaths.CreateScratchDirectory("probe");
        try
        {
            var compiled = await prober.ListCompiledEncodersAsync(ffmpeg, cancellationToken);
            var encoders = EncoderCatalog.All;
            var results = new List<EncoderCapability>(encoders.Count);

            for (var i = 0; i < encoders.Count; i++)
            {
                progress?.Report(new HardwareProgress($"{encoders[i].DisplayName} test ediliyor", (double)i / encoders.Count));
                var capability = await prober.ProbeAsync(ffmpeg, encoders[i], compiled, workDirectory, cancellationToken);
                results.Add(capability);
                progress?.Report(new HardwareProgress($"{encoders[i].DisplayName} test edildi", (double)(i + 1) / encoders.Count, capability));
            }

            var report = new HardwareReport(
                ComputeFingerprint(ffmpeg, gpus),
                DateTimeOffset.Now,
                ffmpeg,
                SystemInfo.GetCpuName(),
                gpus,
                results);

            store.Save(report);
            Report = report;
            return report;
        }
        finally
        {
            AppPaths.TryDeleteDirectory(workDirectory);
        }
    }

    /// <summary>A different binary invalidates the encoder results; the same one keeps them.</summary>
    private void SetFfmpeg(FfmpegInfo? info)
    {
        if (info?.FfmpegPath != Ffmpeg?.FfmpegPath)
            Report = null;
        Ffmpeg = info;
    }

    private async Task<T> RunExclusiveAsync<T>(Func<Task<T>> action, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        IsBusy = true;
        OnChanged();
        try
        {
            return await action();
        }
        finally
        {
            IsBusy = false;
            _gate.Release();
            OnChanged();
        }
    }

    /// <summary>Changes when the ffmpeg binary is replaced or a GPU driver is updated.</summary>
    internal static string ComputeFingerprint(FfmpegInfo ffmpeg, IReadOnlyList<GpuInfo> gpus)
    {
        var builder = new StringBuilder();
        var file = new FileInfo(ffmpeg.FfmpegPath);
        builder.Append(file.FullName).Append('|')
            .Append(file.Exists ? file.Length : -1).Append('|')
            .Append(file.Exists ? file.LastWriteTimeUtc.Ticks.ToString(CultureInfo.InvariantCulture) : "-");
        foreach (var gpu in gpus.OrderBy(g => g.Name, StringComparer.Ordinal))
            builder.Append('|').Append(gpu.Name).Append('@').Append(gpu.DriverVersion);

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString())))[..16];
    }

    private void OnChanged() => Changed?.Invoke(this, EventArgs.Empty);

    public void Dispose() => _gate.Dispose();
}
