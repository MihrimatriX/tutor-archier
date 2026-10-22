using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using TutorArchier.Core;
using TutorArchier.Core.Hardware;
using TutorArchier.Engine.Ffmpeg;
using TutorArchier.Engine.Hardware;
using TutorArchier.Engine.Media;

namespace TutorArchier.Engine.Merge;

/// <param name="Quality">The CQ / QP / CRF the archive encode uses.</param>
/// <param name="Vmaf">Combined score of the sample clips at that quality.</param>
/// <param name="BytesPerSecond">Size of the sample clips (video plus copied audio) per second of running time.</param>
public sealed record QualitySearchResult(int Quality, double Vmaf, double BytesPerSecond);

public sealed record QualitySearchProgress(double Fraction, string Text);

/// <summary>
/// Archive mode: finds the highest CQ/CRF whose encode still scores <see cref="TargetVmaf"/> against the source,
/// by encoding short clips from across the course and measuring them with libvmaf.
/// ponytail: one quality for the whole course from a few clips; search per lecture if courses that mix
/// screencasts and camera footage come out visibly soft in places.
/// </summary>
public sealed partial class QualitySearch(FfmpegRunner runner, ILogger<QualitySearch> logger)
{
    /// <summary>The usual "can't tell them apart on a normal screen" line for VMAF's default model.</summary>
    public const double TargetVmaf = 95;

    private const int SampleCount = 4;
    private static readonly TimeSpan SampleLength = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Two at a time: one sample's encode (decoder + GPU, mostly serial) overlaps the other's VMAF (all cores).
    /// More would only split the cores further, and old GeForce drivers cap NVENC at three sessions.
    /// </summary>
    private const int SampleSlots = 2;

    internal sealed record Sample(string Source, TimeSpan Start, TimeSpan Length);

    public async Task<QualitySearchResult> RunAsync(
        FfmpegInfo ffmpeg,
        IReadOnlyList<AnalyzedVideo> videos,
        EncoderDefinition encoder,
        EncoderCapability capability,
        IProgress<QualitySearchProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var samples = PickSamples(videos.Where(v => v.IsUsable).ToList());
        if (samples.Count == 0)
            throw new InvalidOperationException("Kalite ölçümü için okunabilir video yok.");

        var (low, high) = EncoderPresets.ArchiveRange(encoder);
        var label = EncoderPresets.QualityLabel(encoder);
        var total = (int)Math.Ceiling(Math.Log2(high - low + 1)) * samples.Count;
        var done = 0;
        var trials = new Dictionary<int, QualitySearchResult>();
        var work = AppPaths.CreateScratchDirectory("vmaf");
        try
        {
            async Task<QualitySearchResult> Measure(int quality)
            {
                if (trials.TryGetValue(quality, out var known))
                    return known;

                var scores = new double[samples.Count];
                var bytes = new long[samples.Count];
                var text = $"{label} {quality} ölçülüyor · {samples.Count} örnek";
                progress?.Report(new QualitySearchProgress(Math.Min(1, (double)done / total), text));
                await Parallel.ForEachAsync(
                    Enumerable.Range(0, samples.Count),
                    new ParallelOptions { MaxDegreeOfParallelism = SampleSlots, CancellationToken = cancellationToken },
                    async (i, token) =>
                    {
                        (scores[i], bytes[i]) = await MeasureAsync(ffmpeg, samples[i], encoder, capability, quality, Path.Combine(work, $"sample-{i}.mkv"), token);
                        progress?.Report(new QualitySearchProgress(Math.Min(1, (double)Interlocked.Increment(ref done) / total), text));
                    });

                var result = new QualitySearchResult(quality, Aggregate(scores), bytes.Sum() / samples.Sum(s => s.Length.TotalSeconds));
                logger.LogInformation("Archive search {Encoder} {Label} {Quality}: VMAF {Scores} -> {Vmaf:0.00}", encoder.Id, label, quality, scores, result.Vmaf);
                return trials[quality] = result;
            }

            var best = await HighestPassing(low, high, async quality => (await Measure(quality)).Vmaf >= TargetVmaf);
            return await Measure(best);
        }
        finally
        {
            AppPaths.TryDeleteDirectory(work);
        }
    }

    /// <summary>Largest value in [low, high] that passes, given that quality only drops as the value rises; low if none does.</summary>
    internal static async Task<int> HighestPassing(int low, int high, Func<int, Task<bool>> passes)
    {
        while (low < high)
        {
            var mid = (low + high + 1) / 2;
            if (await passes(mid))
                low = mid;
            else
                high = mid - 1;
        }
        return low;
    }

    /// <summary>One score for the course from the per-clip VMAF scores; the search keeps it at or above <see cref="TargetVmaf"/>.</summary>
    /// <remarks>
    /// The average alone lets three static slides outvote one camera clip that came out soft; the minimum alone lets
    /// one hard scene inflate the whole course. So: transparent on average, and no clip more than 2 points below that.
    /// </remarks>
    internal static double Aggregate(IReadOnlyList<double> scores) => Math.Min(scores.Average(), scores.Min() + 2);

    /// <summary>Spread over the course, a third of the way into each lecture: past the intro card, before the outro.</summary>
    internal static List<Sample> PickSamples(IReadOnlyList<AnalyzedVideo> usable)
    {
        var count = Math.Min(SampleCount, usable.Count);
        return Enumerable.Range(0, count)
            .Select(i => usable[count == 1 ? 0 : i * (usable.Count - 1) / (count - 1)])
            .Select(video =>
            {
                var duration = video.Info!.Duration;
                var length = duration < SampleLength ? duration : SampleLength;
                return new Sample(video.File.FullPath, (duration - length) / 3, length);
            })
            .ToList();
    }

    private async Task<(double Vmaf, long Bytes)> MeasureAsync(
        FfmpegInfo ffmpeg, Sample sample, EncoderDefinition encoder, EncoderCapability capability, int quality, string clip, CancellationToken cancellationToken)
    {
        var start = Seconds(sample.Start);
        var length = Seconds(sample.Length);

        // Audio is copied along so the clip's size stands for the whole output; VMAF only reads the video.
        var encode = await runner.RunAsync(
            ffmpeg.FfmpegPath,
            [
                "-hide_banner", "-v", "error", "-y", "-ss", start, "-t", length, "-i", sample.Source,
                "-map", "0:V:0", "-map", "0:a:0?", "-c:a", "copy", "-sn", "-dn",
                "-c:v", encoder.Id, .. EncoderPresets.ConstantQuality(encoder, capability, quality), "-pix_fmt", encoder.TenBitPixelFormat,
                "-f", "matroska", clip,
            ],
            new FfmpegRunOptions { LowPriority = true },
            cancellationToken);
        if (!encode.Success)
            throw new InvalidOperationException($"Kalite örneği kodlanamadı: {MergeRunner.LastLine(encode.StdErr)}");

        // Frames are paired by index, not timestamp: the clip's 1/1000 time base rounds 33.37 ms down to 33 ms,
        // and libvmaf's timestamp sync would then score every frame against the one before it.
        var filter = string.Create(CultureInfo.InvariantCulture,
            $"[0:v]format=yuv420p,settb=AVTB,setpts=N[d];[1:v]format=yuv420p,settb=AVTB,setpts=N[r];[d][r]libvmaf=n_threads={Math.Max(1, Environment.ProcessorCount / SampleSlots)}:n_subsample=2");
        var measure = await runner.RunAsync(
            ffmpeg.FfmpegPath,
            ["-hide_banner", "-nostats", "-i", clip, "-ss", start, "-t", length, "-i", sample.Source, "-lavfi", filter, "-f", "null", "-"],
            new FfmpegRunOptions { LowPriority = true },
            cancellationToken);
        var match = VmafScore().Match(measure.StdErr);
        if (!measure.Success || !match.Success)
            throw new InvalidOperationException($"VMAF ölçülemedi: {MergeRunner.LastLine(measure.StdErr)}");

        return (double.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture), new FileInfo(clip).Length);
    }

    private static string Seconds(TimeSpan time) => time.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture);

    [GeneratedRegex(@"VMAF score: ([\d.]+)")]
    private static partial Regex VmafScore();
}
