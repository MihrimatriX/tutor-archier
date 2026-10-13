using TutorArchier.Core.Hardware;
using TutorArchier.UI.Controls;

namespace TutorArchier.UI.ViewModels;

public sealed record CapabilityChip(StatusKind Kind, string Text, string? ToolTip);

public static class CapabilityChips
{
    /// <summary>
    /// Onboarding summary for one encoder (plan §6.4.1). Encoders for GPU vendors that aren't in the
    /// machine are left out rather than listed as failures; CPU AV1 is out of scope and not shown.
    /// </summary>
    public static IEnumerable<CapabilityChip> For(EncoderDefinition encoder, EncoderCapability capability, IReadOnlySet<GpuVendor> gpuVendors)
    {
        if (!encoder.UsedForEncoding && !encoder.IsHardware)
            yield break;

        if (!capability.Available)
        {
            if (encoder.IsHardware && !gpuVendors.Contains(ToGpuVendor(encoder.Vendor)))
                yield break;
            var label = encoder.UsedForEncoding ? encoder.DisplayName : $"{encoder.DisplayName} (bu donanımda yok)";
            yield return new CapabilityChip(StatusKind.Danger, label, capability.FailureReason);
            yield break;
        }

        yield return new CapabilityChip(StatusKind.Success, encoder.DisplayName, null);

        // 10-bit matters for the archive mode's HEVC output; surface it on its own like the plan's mock-up.
        if (encoder is { Codec: VideoCodecKind.Hevc, IsHardware: true })
        {
            var label = encoder.DisplayName.Replace("HEVC", "HEVC 10-bit", StringComparison.Ordinal);
            yield return capability.TenBit
                ? new CapabilityChip(StatusKind.Success, label, null)
                : new CapabilityChip(StatusKind.Danger, label, capability.TenBitFailureReason);
        }
    }

    public static GpuVendor ToGpuVendor(EncoderVendor vendor) => vendor switch
    {
        EncoderVendor.Nvidia => GpuVendor.Nvidia,
        EncoderVendor.Amd => GpuVendor.Amd,
        _ => GpuVendor.Other,
    };
}
