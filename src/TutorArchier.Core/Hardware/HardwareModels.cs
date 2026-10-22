namespace TutorArchier.Core.Hardware;

public enum EncoderVendor
{
    Software,
    Nvidia,
    Amd,
}

public enum GpuVendor
{
    Other,
    Nvidia,
    Amd,
    Intel,
}

public enum VideoCodecKind
{
    H264,
    Hevc,
    Av1,
}

/// <summary>A video encoder the app knows how to drive.</summary>
/// <param name="Id">ffmpeg encoder name, e.g. <c>hevc_amf</c>.</param>
/// <param name="UsedForEncoding">
/// False for encoders that are detected but not offered (AV1 is out of scope for now;
/// detection still runs so it can light up later without code changes).
/// </param>
public sealed record EncoderDefinition(
    string Id,
    VideoCodecKind Codec,
    EncoderVendor Vendor,
    string DisplayName,
    bool UsedForEncoding)
{
    public bool IsHardware => Vendor != EncoderVendor.Software;

    /// <summary>Pixel format used to probe 10-bit support.</summary>
    public string TenBitPixelFormat => IsHardware ? "p010le" : "yuv420p10le";
}

public sealed record GpuInfo(string Name, string? DriverVersion, GpuVendor Vendor)
{
    public static GpuVendor VendorFromName(string name)
    {
        if (name.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase))
            return GpuVendor.Nvidia;
        if (name.Contains("AMD", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Radeon", StringComparison.OrdinalIgnoreCase))
            return GpuVendor.Amd;
        if (name.Contains("Intel", StringComparison.OrdinalIgnoreCase))
            return GpuVendor.Intel;
        return GpuVendor.Other;
    }
}

public sealed record FfmpegInfo(
    string FfmpegPath,
    string FfprobePath,
    string VersionText,
    Version? Version,
    bool HasLibVmaf,
    bool HasSubtitlesFilter,
    bool HasZscale)
{
    /// <summary>The ffmpeg release the command templates were verified against (plan: pin the version).</summary>
    public static Version TestedVersion { get; } = new(8, 1);

    public bool IsTestedVersion =>
        Version is { } v && v.Major == TestedVersion.Major && v.Minor == TestedVersion.Minor;
}

/// <summary>Result of a 1-frame trial encode plus feature probes for one encoder.</summary>
/// <param name="FailureReason">Short user-facing reason (Turkish) when <paramref name="Available"/> is false.</param>
/// <param name="FailureDetail">The ffmpeg error line behind the reason, for tooltips.</param>
/// <param name="TenBitFailureReason">Why 10-bit failed while the encoder itself works, if it did.</param>
/// <param name="BenchmarkFps">1080p encode speed; null until a benchmark has run.</param>
public sealed record EncoderCapability(
    string EncoderId,
    bool Available,
    bool TenBit,
    bool BFrames,
    string? FailureReason = null,
    string? FailureDetail = null,
    string? TenBitFailureReason = null,
    double? BenchmarkFps = null);

public sealed record HardwareReport(
    string Fingerprint,
    DateTimeOffset TestedAt,
    FfmpegInfo Ffmpeg,
    string? CpuName,
    IReadOnlyList<GpuInfo> Gpus,
    IReadOnlyList<EncoderCapability> Encoders,
    DateTimeOffset? BenchmarkedAt = null)
{
    public EncoderCapability? Find(string encoderId) =>
        Encoders.FirstOrDefault(e => e.EncoderId == encoderId);
}
