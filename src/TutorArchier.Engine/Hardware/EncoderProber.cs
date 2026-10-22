using Microsoft.Extensions.Logging;
using TutorArchier.Core.Hardware;
using TutorArchier.Engine.Ffmpeg;

namespace TutorArchier.Engine.Hardware;

/// <summary>Measures what each encoder can do on this machine with tiny trial encodes (plan 4.7).</summary>
public sealed class EncoderProber(FfmpegRunner runner, ILogger<EncoderProber> logger)
{
    public async Task<IReadOnlySet<string>> ListCompiledEncodersAsync(FfmpegInfo ffmpeg, CancellationToken cancellationToken = default)
    {
        var result = await runner.RunAsync(ffmpeg.FfmpegPath, ["-hide_banner", "-encoders"], cancellationToken: cancellationToken);
        return FfmpegOutputParser.ParseEncoderNames(result.StdOut);
    }

    public async Task<EncoderCapability> ProbeAsync(
        FfmpegInfo ffmpeg,
        EncoderDefinition encoder,
        IReadOnlySet<string> compiledEncoders,
        string workDirectory,
        CancellationToken cancellationToken = default)
    {
        if (!compiledEncoders.Contains(encoder.Id))
            return new EncoderCapability(encoder.Id, Available: false, TenBit: false, BFrames: false, "Bu ffmpeg derlemesinde yok");

        var basic = await TrialEncodeAsync(ffmpeg, encoder, [], cancellationToken);
        if (!basic.Success)
        {
            var (reason, detail) = EncoderErrorTranslator.Translate(encoder.Id, basic.StdErr);
            logger.LogInformation("Encoder {Encoder} unavailable: {Reason} ({Detail})", encoder.Id, reason, detail);
            return new EncoderCapability(encoder.Id, Available: false, TenBit: false, BFrames: false, reason, detail);
        }

        var tenBit = await TrialEncodeAsync(ffmpeg, encoder, ["-pix_fmt", encoder.TenBitPixelFormat], cancellationToken);
        var tenBitReason = tenBit.Success ? null : EncoderErrorTranslator.Translate(encoder.Id, tenBit.StdErr).Reason;
        var bFrames = await ProbeBFramesAsync(ffmpeg, encoder, workDirectory, cancellationToken);

        logger.LogInformation("Encoder {Encoder} available (10-bit: {TenBit}, B-frames: {BFrames})", encoder.Id, tenBit.Success, bFrames);
        return new EncoderCapability(encoder.Id, Available: true, tenBit.Success, bFrames, TenBitFailureReason: tenBitReason);
    }

    private Task<FfmpegResult> TrialEncodeAsync(FfmpegInfo ffmpeg, EncoderDefinition encoder, string[] extra, CancellationToken cancellationToken) =>
        runner.RunAsync(
            ffmpeg.FfmpegPath,
            [
                "-hide_banner", "-v", "error",
                "-f", "lavfi", "-i", "color=black:s=256x256:d=0.1",
                "-frames:v", "1", "-c:v", encoder.Id, .. extra,
                "-f", "null", "-",
            ],
            cancellationToken: cancellationToken);

    /// <summary>
    /// Some encoders silently ignore <c>-bf</c> (hevc_amf on pre-RDNA3), so a successful run proves nothing.
    /// Encode a short clip and look for actual B pictures in the bitstream.
    /// </summary>
    private async Task<bool> ProbeBFramesAsync(FfmpegInfo ffmpeg, EncoderDefinition encoder, string workDirectory, CancellationToken cancellationToken)
    {
        if (encoder.Codec == VideoCodecKind.Av1)
            return false;
        if (!encoder.IsHardware)
            return true;

        var muxer = encoder.Codec == VideoCodecKind.H264 ? "h264" : "hevc";
        var output = Path.Combine(workDirectory, $"bframes-{encoder.Id}.{muxer}");

        var encode = await runner.RunAsync(
            ffmpeg.FfmpegPath,
            [
                "-hide_banner", "-v", "error", "-y",
                "-f", "lavfi", "-i", "testsrc2=s=320x240:r=30",
                "-frames:v", "24", "-c:v", encoder.Id, "-bf", "2",
                "-f", muxer, output,
            ],
            cancellationToken: cancellationToken);
        if (!encode.Success || !File.Exists(output))
            return false;

        var probe = await runner.RunAsync(
            ffmpeg.FfprobePath,
            ["-v", "error", "-select_streams", "v:0", "-show_entries", "frame=pict_type", "-of", "csv=p=0", output],
            cancellationToken: cancellationToken);

        return probe.Success
            && probe.StdOut.Split('\n', StringSplitOptions.TrimEntries).Any(pictType => pictType == "B");
    }
}
