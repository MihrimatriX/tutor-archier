using Microsoft.Extensions.Logging.Abstractions;
using TutorArchier.Core;
using TutorArchier.Core.Hardware;
using TutorArchier.Engine.Ffmpeg;
using TutorArchier.Engine.Hardware;

namespace TutorArchier.Tests.Integration;

public sealed class FfmpegIntegrationTests
{
    private readonly FfmpegRunner _runner = new(NullLogger<FfmpegRunner>.Instance);
    private readonly FfmpegLocator _locator;

    public FfmpegIntegrationTests()
    {
        _locator = new FfmpegLocator(_runner, NullLogger<FfmpegLocator>.Instance);
    }

    [FfmpegFact]
    public void Locator_reads_version_and_filters()
    {
        var info = TestFfmpeg.Locate(_locator);

        Assert.False(string.IsNullOrEmpty(info.VersionText));
        Assert.True(File.Exists(info.FfmpegPath));
        Assert.True(File.Exists(info.FfprobePath));
    }

    [Fact]
    public async Task Locator_rejects_a_folder_without_ffmpeg()
    {
        var empty = AppPaths.CreateScratchDirectory("test-empty");
        try
        {
            Assert.Null(await _locator.InspectAsync(empty));
        }
        finally
        {
            AppPaths.TryDeleteDirectory(empty);
        }
    }

    [Fact]
    public void Preferred_directory_is_searched_first_then_its_bin()
    {
        var candidates = FfmpegLocator.CandidateDirectories(@"D:\Tools\ffmpeg").Take(2).ToList();

        Assert.Equal([@"D:\Tools\ffmpeg", @"D:\Tools\ffmpeg\bin"], candidates);
    }

    [Fact]
    public void Candidates_are_unique()
    {
        var candidates = FfmpegLocator.CandidateDirectories(@"C:\Windows").ToList();

        Assert.Equal(candidates.Count, candidates.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [FfmpegFact]
    public async Task Runner_reports_progress_until_the_end()
    {
        var info = TestFfmpeg.Locate(_locator);
        var updates = new List<FfmpegProgress>();

        var result = await _runner.RunAsync(
            info.FfmpegPath,
            ["-hide_banner", "-v", "error", "-f", "lavfi", "-i", "testsrc2=s=320x240:r=30:d=2", "-f", "null", "-"],
            new FfmpegRunOptions { OnProgress = updates.Add });

        Assert.True(result.Success, result.StdErr);
        var last = Assert.Single(updates, u => u.IsEnd);
        Assert.Equal(60, last.Frame);
        Assert.InRange(last.OutTime.TotalSeconds, 1.9, 2.05);
    }

    [FfmpegFact]
    public async Task Cancellation_stops_ffmpeg()
    {
        var info = TestFfmpeg.Locate(_locator);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _runner.RunAsync(
            info.FfmpegPath,
            ["-hide_banner", "-v", "error", "-re", "-f", "lavfi", "-i", "testsrc2=s=320x240:r=30:d=60", "-f", "null", "-"],
            cancellationToken: cancellation.Token));
    }

    [FfmpegFact]
    public void Child_processes_are_tied_to_the_app_lifetime()
    {
        var info = TestFfmpeg.Locate(_locator);
        using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
            info.FfmpegPath,
            ["-hide_banner", "-v", "error", "-re", "-f", "lavfi", "-i", "testsrc2=s=64x64:r=5:d=30", "-f", "null", "-"])
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        })!;
        try
        {
            Assert.True(ChildProcessJob.TryTrack(process, out var error), error);
            Assert.True(ChildProcessJob.IsTracked(process));
        }
        finally
        {
            process.Kill();
        }
    }

    [Fact]
    public async Task Runner_reports_a_missing_executable_as_failure()
    {
        var result = await _runner.RunAsync(@"C:\definitely\not\here\ffmpeg.exe", ["-version"]);

        Assert.False(result.Success);
    }

    [FfmpegFact]
    public async Task Prober_measures_software_x264()
    {
        var info = TestFfmpeg.Locate(_locator);
        var prober = new EncoderProber(_runner, NullLogger<EncoderProber>.Instance);
        var compiled = await prober.ListCompiledEncodersAsync(info);
        var work = AppPaths.CreateScratchDirectory("test-probe");
        try
        {
            var x264 = await prober.ProbeAsync(info, EncoderCatalog.Find("libx264")!, compiled, work);

            Assert.True(x264.Available, x264.FailureDetail);
            Assert.True(x264.BFrames);
        }
        finally
        {
            AppPaths.TryDeleteDirectory(work);
        }
    }

    [FfmpegFact]
    public async Task Prober_reports_encoders_missing_from_the_build()
    {
        var info = TestFfmpeg.Locate(_locator);
        var prober = new EncoderProber(_runner, NullLogger<EncoderProber>.Instance);
        var fake = new EncoderDefinition("not_a_real_encoder", VideoCodecKind.H264, EncoderVendor.Software, "Fake", true);

        var result = await prober.ProbeAsync(info, fake, new HashSet<string>(), AppPaths.TempRoot);

        Assert.False(result.Available);
        Assert.Equal("Bu ffmpeg derlemesinde yok", result.FailureReason);
    }

    [Fact]
    public void Fingerprint_changes_with_the_gpu_driver_only()
    {
        var ffmpeg = new FfmpegInfo(@"C:\nope\ffmpeg.exe", @"C:\nope\ffprobe.exe", "9.0.2", new Version(9, 0, 2), true, true, true);
        GpuInfo[] before = [new("AMD Radeon(TM) Graphics", "27.20.11022.14001", GpuVendor.Amd)];
        GpuInfo[] after = [new("AMD Radeon(TM) Graphics", "31.0.24000.1000", GpuVendor.Amd)];

        Assert.Equal(HardwareService.ComputeFingerprint(ffmpeg, before), HardwareService.ComputeFingerprint(ffmpeg, before));
        Assert.NotEqual(HardwareService.ComputeFingerprint(ffmpeg, before), HardwareService.ComputeFingerprint(ffmpeg, after));
    }
}
