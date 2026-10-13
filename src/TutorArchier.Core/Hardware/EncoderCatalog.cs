namespace TutorArchier.Core.Hardware;

public static class EncoderCatalog
{
    public static IReadOnlyList<EncoderDefinition> All { get; } =
    [
        new("h264_nvenc", VideoCodecKind.H264, EncoderVendor.Nvidia, "H.264 · NVENC", UsedForEncoding: true),
        new("hevc_nvenc", VideoCodecKind.Hevc, EncoderVendor.Nvidia, "HEVC · NVENC", UsedForEncoding: true),
        new("av1_nvenc", VideoCodecKind.Av1, EncoderVendor.Nvidia, "AV1 · NVENC", UsedForEncoding: false),
        new("h264_amf", VideoCodecKind.H264, EncoderVendor.Amd, "H.264 · AMF", UsedForEncoding: true),
        new("hevc_amf", VideoCodecKind.Hevc, EncoderVendor.Amd, "HEVC · AMF", UsedForEncoding: true),
        new("av1_amf", VideoCodecKind.Av1, EncoderVendor.Amd, "AV1 · AMF", UsedForEncoding: false),
        new("libx264", VideoCodecKind.H264, EncoderVendor.Software, "H.264 · x264", UsedForEncoding: true),
        new("libx265", VideoCodecKind.Hevc, EncoderVendor.Software, "HEVC · x265", UsedForEncoding: true),
        new("libsvtav1", VideoCodecKind.Av1, EncoderVendor.Software, "AV1 · SVT-AV1", UsedForEncoding: false),
    ];

    public static EncoderDefinition? Find(string id) => All.FirstOrDefault(e => e.Id == id);

    public static string VendorLabel(EncoderVendor vendor) => vendor switch
    {
        EncoderVendor.Nvidia => "NVIDIA GPU",
        EncoderVendor.Amd => "AMD GPU",
        _ => "CPU",
    };
}
