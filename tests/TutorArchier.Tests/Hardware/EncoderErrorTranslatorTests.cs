using TutorArchier.Engine.Hardware;

namespace TutorArchier.Tests.Hardware;

/// <summary>Error texts are real ffmpeg 9.0.2 output captured on the dev machine.</summary>
public class EncoderErrorTranslatorTests
{
    [Theory]
    [InlineData("h264_nvenc", "[h264_nvenc @ 000001effe6df4c0] Cannot load nvcuda.dll", "NVIDIA ekran kartı bulunamadı", "Cannot load nvcuda.dll")]
    [InlineData("hevc_amf", "[hevc_amf @ 0000021a09ab0840] 10-bit encoder is not supported by AMD GPU drivers versions lower than 23.30", "AMD sürücüsü 10-bit için eski (23.30 veya üstü gerekli)", "10-bit encoder is not supported by AMD GPU drivers versions lower than 23.30")]
    [InlineData("av1_amf", "[av1_amf @ 0000018df1b9da40] CreateComponent(AMFVideoEncoderHW_AV1) failed with error 10", "Ekran kartı bu codec'i desteklemiyor", "CreateComponent(AMFVideoEncoderHW_AV1) failed with error 10")]
    [InlineData("h264_qsv", "[h264_qsv @ 000002721c0de500] Error creating a MFX session: -9.", "Intel ekran kartı bulunamadı", "Error creating a MFX session: -9.")]
    [InlineData("h264_amf", "[h264_amf @ 000001] DLL amfrt64.dll failed to open", "AMD ekran kartı bulunamadı", "DLL amfrt64.dll failed to open")]
    public void Translates_known_failures(string encoder, string stderr, string reason, string detail)
    {
        var (actualReason, actualDetail) = EncoderErrorTranslator.Translate(encoder, stderr);

        Assert.Equal(reason, actualReason);
        Assert.Equal(detail, actualDetail);
    }

    [Fact]
    public void Hardware_init_failure_wins_over_ffmpegs_generic_encoder_not_found()
    {
        // Full av1_amf output on a Vega iGPU: the real cause is line 1, but "Encoder not found" follows.
        const string stderr = """
            [av1_amf @ 000002630c1a1380] CreateComponent(AMFVideoEncoderHW_AV1) failed with error 10
            [vost#0:0/av1_amf @ 000002630c1bb340] [enc:av1_amf @ 000002630c14b100] Error while opening encoder - maybe incorrect parameters such as bit_rate, rate, width or height.
            [vf#0:0 @ 000002630c1bc180] Error sending frames to consumers: Encoder not found
            [vf#0:0 @ 000002630c1bc180] Task finished with error code: -1129203192 (Encoder not found)
            [vost#0:0/av1_amf @ 000002630c1bb340] [enc:av1_amf @ 000002630c14b100] Could not open encoder before EOF
            """;

        var (reason, detail) = EncoderErrorTranslator.Translate("av1_amf", stderr);

        Assert.Equal("Ekran kartı bu codec'i desteklemiyor", reason);
        Assert.Equal("CreateComponent(AMFVideoEncoderHW_AV1) failed with error 10", detail);
    }

    [Fact]
    public void Missing_encoder_in_build()
    {
        const string stderr = """
            [vost#0:0 @ 0000021ded6be8c0] Unknown encoder 'foo_bar'
            [vost#0:0 @ 0000021ded6be8c0] Error selecting an encoder
            Error opening output file -.
            """;

        var (reason, detail) = EncoderErrorTranslator.Translate("foo_bar", stderr);

        Assert.Equal("Bu ffmpeg derlemesinde yok", reason);
        Assert.Equal("Unknown encoder 'foo_bar'", detail);
    }

    [Fact]
    public void Unknown_errors_fall_back_to_a_generic_reason()
    {
        var (reason, detail) = EncoderErrorTranslator.Translate("libx265", "something unexpected happened");

        Assert.Equal(EncoderErrorTranslator.GenericReason, reason);
        Assert.Equal("something unexpected happened", detail);
    }

    [Fact]
    public void Detail_prefers_the_encoders_own_line_and_skips_svt_banner()
    {
        const string stderr = """
            Svt[info]: -------------------------------------------
            [vost#0:0/hevc_amf @ 0001] Error while opening encoder
            [hevc_amf @ 0002] The real reason
            """;

        Assert.Equal("The real reason", EncoderErrorTranslator.PickDetailLine("hevc_amf", stderr));
        Assert.Equal("Error while opening encoder", EncoderErrorTranslator.PickDetailLine("other", stderr));
    }
}
