using System.Globalization;
using TutorArchier.Core.Hardware;

namespace TutorArchier.Engine.Hardware;

/// <summary>
/// Quality-first encoder arguments (plan, Ek A). The single <c>quality</c> knob maps to
/// CQ for NVENC, QP for AMF and CRF for software encoders. Archive mode tunes it with a VMAF search.
/// </summary>
public static class EncoderPresets
{
    /// <summary>
    /// Quality at which a re-encode of an already-compressed lecture is visually indistinguishable.
    /// ponytail: fixed per encoder family for the lossless merge's odd ones out; archive mode measures instead.
    /// </summary>
    public static int TransparentQuality(EncoderDefinition encoder) => encoder.Vendor switch
    {
        EncoderVendor.Nvidia => 19,
        EncoderVendor.Amd => 18,
        _ => encoder.Codec == VideoCodecKind.H264 ? 17 : 18,
    };

    /// <summary>What the archive search may try: from transparent up to clearly visible loss.</summary>
    public static (int Low, int High) ArchiveRange(EncoderDefinition encoder) =>
        (TransparentQuality(encoder), TransparentQuality(encoder) + 16);

    public static string QualityLabel(EncoderDefinition encoder) => encoder.Vendor switch
    {
        EncoderVendor.Nvidia => "CQ",
        EncoderVendor.Amd => "QP",
        _ => "CRF",
    };

    public static IReadOnlyList<string> ConstantQuality(EncoderDefinition encoder, EncoderCapability capability, int quality)
    {
        var q = quality.ToString(CultureInfo.InvariantCulture);
        var args = new List<string>();

        switch (encoder.Vendor)
        {
            case EncoderVendor.Nvidia:
                args.AddRange(["-preset", "p7", "-tune", "hq", "-rc", "vbr", "-cq", q, "-b:v", "0", "-spatial-aq", "1", "-rc-lookahead", "32"]);
                // Pascal HEVC has no B-frames and refuses to open when asked for them.
                if (capability.BFrames)
                    args.AddRange(["-bf", "3"]);
                break;

            case EncoderVendor.Amd:
                args.AddRange(["-quality", "quality", "-rc", "cqp", "-qp_i", q, "-qp_p", q]);
                if (encoder.Codec == VideoCodecKind.H264 && capability.BFrames)
                    args.AddRange(["-bf", "2", "-qp_b", q]);
                break;

            default:
                args.AddRange(encoder.Id switch
                {
                    "libx265" => ["-preset", "medium", "-crf", q, "-x265-params", "log-level=error:aq-mode=3"],
                    "libsvtav1" => ["-preset", "8", "-crf", q],
                    _ => ["-preset", "medium", "-crf", q],
                });
                break;
        }

        return args;
    }
}
