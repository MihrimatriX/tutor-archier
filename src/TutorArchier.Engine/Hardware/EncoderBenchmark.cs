using System.Diagnostics;
using Microsoft.Extensions.Logging;
using TutorArchier.Core.Hardware;
using TutorArchier.Engine.Ffmpeg;

namespace TutorArchier.Engine.Hardware;

/// <summary>Short 1080p speed test per encoder; the fps feeds duration estimates.</summary>
public sealed class EncoderBenchmark(FfmpegRunner runner, ILogger<EncoderBenchmark> logger)
{
    /// <summary>Wall-clock time spent on each encoder. Fast encoders loop the clip until it runs out.</summary>
    public static TimeSpan BudgetPerEncoder { get; } = TimeSpan.FromSeconds(5);

    private const int BenchmarkQuality = 23;

    /// <summary>
    /// A 2 s 1080p30 clip with light temporal noise, so encoders can't coast on flat colour.
    /// MPEG-2 keeps decoding cost negligible and needs no optional libraries.
    /// </summary>
    public async Task<string> CreateSourceAsync(FfmpegInfo ffmpeg, string workDirectory, CancellationToken cancellationToken = default)
    {
        var path = Path.Combine(workDirectory, "bench-source.mkv");
        var result = await runner.RunAsync(
            ffmpeg.FfmpegPath,
            [
                "-hide_banner", "-v", "error", "-y",
                "-f", "lavfi", "-i", "testsrc2=s=1920x1080:r=30:d=2,noise=alls=6:allf=t",
                "-c:v", "mpeg2video", "-q:v", "2", "-pix_fmt", "yuv420p",
                path,
            ],
            cancellationToken: cancellationToken);

        if (!result.Success)
            throw new InvalidOperationException($"Benchmark source could not be created: {result.StdErr}");
        return path;
    }

    /// <returns>Encoded frames per second, or null if too few progress samples arrived to measure.</returns>
    public async Task<double?> MeasureAsync(
        FfmpegInfo ffmpeg,
        EncoderDefinition encoder,
        EncoderCapability capability,
        string sourcePath,
        CancellationToken cancellationToken = default)
    {
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(BudgetPerEncoder);

        var stopwatch = Stopwatch.StartNew();
        (double Seconds, long Frame)? first = null;
        (double Seconds, long Frame)? last = null;

        void OnProgress(FfmpegProgress progress)
        {
            if (progress.Frame <= 0)
                return;
            var sample = (stopwatch.Elapsed.TotalSeconds, progress.Frame);
            // The first sample includes encoder start-up, so the rate is measured between samples.
            if (first is null)
                first = sample;
            else
                last = sample;
        }

        try
        {
            await runner.RunAsync(
                ffmpeg.FfmpegPath,
                [
                    "-hide_banner", "-v", "error",
                    "-stream_loop", "-1", "-i", sourcePath,
                    "-an", "-c:v", encoder.Id, .. EncoderPresets.ConstantQuality(encoder, capability, BenchmarkQuality),
                    "-f", "null", "-",
                ],
                new FfmpegRunOptions { OnProgress = OnProgress },
                budget.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // Budget elapsed: the expected way for a looped benchmark to end.
        }

        if (first is not { } start || last is not { } end || end.Seconds - start.Seconds < 1)
        {
            logger.LogWarning("Benchmark for {Encoder} produced too few samples", encoder.Id);
            return null;
        }

        var fps = (end.Frame - start.Frame) / (end.Seconds - start.Seconds);
        logger.LogInformation("Benchmark {Encoder}: {Fps:F1} fps", encoder.Id, fps);
        return fps;
    }
}
