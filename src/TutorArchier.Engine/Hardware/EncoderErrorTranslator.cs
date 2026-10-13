using System.Text.RegularExpressions;

namespace TutorArchier.Engine.Hardware;

/// <summary>Turns ffmpeg's encoder-init errors into a short reason a non-technical user can act on.</summary>
public static partial class EncoderErrorTranslator
{
    public const string GenericReason = "Deneme kodlaması başarısız";

    // First match wins, so more specific needles come first. Hardware rules precede "Unknown encoder":
    // AMF/NVENC init failures end with ffmpeg's generic "Encoder not found" summary, which must not win.
    private static readonly (string[] Needles, string Reason)[] Rules =
    [
        (["10-bit encoder is not supported by AMD GPU drivers"], "AMD sürücüsü 10-bit için eski (23.30 veya üstü gerekli)"),
        (["Driver does not support the required nvenc API version", "minimum required Nvidia driver"], "NVIDIA sürücüsü bu ffmpeg sürümü için eski"),
        (["nvcuda.dll", "libcuda.so", "CUDA_ERROR_NO_DEVICE", "No NVENC capable devices found"], "NVIDIA ekran kartı bulunamadı"),
        (["amfrt64.dll", "amfrt32.dll", "libamfrt64", "Failed to open AMF"], "AMD ekran kartı bulunamadı"),
        (["MFX session"], "Intel ekran kartı bulunamadı"),
        (["CreateComponent(", "No capable devices found", "Codec not supported", "OpenEncodeSessionEx failed"], "Ekran kartı bu codec'i desteklemiyor"),
        (["Unknown encoder"], "Bu ffmpeg derlemesinde yok"),
    ];

    public static (string Reason, string? Detail) Translate(string encoderId, string stderr)
    {
        var detail = PickDetailLine(encoderId, stderr);
        foreach (var (needles, reason) in Rules)
        {
            if (needles.Any(needle => stderr.Contains(needle, StringComparison.OrdinalIgnoreCase)))
                return (reason, detail);
        }
        return (GenericReason, detail);
    }

    /// <summary>Prefers the encoder's own first log line ("[hevc_amf @ 0x…] …") with the context prefix removed.</summary>
    internal static string? PickDetailLine(string encoderId, string stderr)
    {
        var lines = stderr.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        var ownPrefix = $"[{encoderId} @";
        var line = lines.FirstOrDefault(l => l.StartsWith(ownPrefix, StringComparison.Ordinal))
            ?? lines.FirstOrDefault(l => !l.StartsWith("Svt[", StringComparison.Ordinal));
        return line is null ? null : ContextPrefix().Replace(line, "").Trim();
    }

    [GeneratedRegex(@"^(\[[^\]]+\]\s*)+")]
    private static partial Regex ContextPrefix();
}
