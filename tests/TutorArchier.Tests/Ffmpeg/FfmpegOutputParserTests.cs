using TutorArchier.Engine.Ffmpeg;

namespace TutorArchier.Tests.Ffmpeg;

public class FfmpegOutputParserTests
{
    [Theory]
    [InlineData("ffmpeg version 9.0.2-full_build-www.gyan.dev Copyright (c) 2000-2026 the FFmpeg developers", "9.0.2-full_build-www.gyan.dev", "9.0.2")]
    [InlineData("ffmpeg version n7.1 Copyright (c) 2000-2024", "n7.1", "7.1.0")]
    [InlineData("ffmpeg version 8.0 Copyright", "8.0", "8.0.0")]
    public void Parses_release_versions(string line, string expectedText, string expectedVersion)
    {
        var (text, version) = FfmpegOutputParser.ParseVersion(line + "\nbuilt with gcc 16.2.0\n");

        Assert.Equal(expectedText, text);
        Assert.Equal(Version.Parse(expectedVersion), version);
    }

    [Theory]
    [InlineData("ffmpeg version N-121000-g1234abcd-20260901 Copyright", "N-121000-g1234abcd-20260901")]
    [InlineData("ffmpeg version 2026-09-01-git-1234abcd-full_build-www.gyan.dev Copyright", "2026-09-01-git-1234abcd-full_build-www.gyan.dev")]
    public void Git_snapshots_have_text_but_no_numeric_version(string line, string expectedText)
    {
        var (text, version) = FfmpegOutputParser.ParseVersion(line);

        Assert.Equal(expectedText, text);
        Assert.Null(version);
    }

    [Fact]
    public void Unrecognized_output_yields_empty_version()
    {
        Assert.Equal(("", null), FfmpegOutputParser.ParseVersion("something else entirely"));
    }

    [Fact]
    public void Parses_filter_names_and_skips_the_legend()
    {
        const string output = """
            Filters:
              T.. = Timeline support
              .S. = Slice threading
              A = Audio input/output
              | = Source or sink filter
             .. ass               V->V       Render ASS subtitles onto input video using the libass library.
             TS bwdif             V->V       Deinterlace the input image.
             .. libvmaf           VV->V      Calculate the VMAF between two video streams.
             .. subtitles         V->V       Render text subtitles onto input video using the libass library.
             .S zscale            V->V       Apply resizing, colorspace and bit depth conversion.
             .. anullsrc          |->A       Null audio source, return empty audio frames.
            T.C overlay           VV->V      Overlay a video source on top of the input.
            """;

        var names = FfmpegOutputParser.ParseFilterNames(output);

        Assert.Equal(["anullsrc", "ass", "bwdif", "libvmaf", "overlay", "subtitles", "zscale"], names.Order());
    }

    [Fact]
    public void Parses_encoder_names_and_skips_the_legend()
    {
        const string output = """
            Encoders:
             V..... = Video
             A..... = Audio
             S..... = Subtitle
             .F.... = Frame-level multithreading
             ------
             V....D libx264              libx264 H.264 / AVC / MPEG-4 AVC / MPEG-4 part 10 (codec h264)
             V....D hevc_amf             AMD AMF HEVC encoder (codec hevc)
             V..... libsvtav1            SVT-AV1(Scalable Video Technology for AV1) encoder (codec av1)
             A....D aac                  AAC (Advanced Audio Coding)
             S..... srt                  SubRip subtitle
            """;

        var names = FfmpegOutputParser.ParseEncoderNames(output);

        Assert.Equal(["aac", "hevc_amf", "libsvtav1", "libx264", "srt"], names.Order());
    }
}
