using System.Text;
using TutorArchier.Engine.Media;

namespace TutorArchier.Tests.Media;

public sealed class SubtitleFilesTests
{
    [Fact]
    public void Srt_and_vtt_parse_to_the_same_cues()
    {
        const string srt = "1\r\n00:00:01,500 --> 00:00:03,000\r\n<i>Merhaba</i>\r\ndünya\r\n\r\n2\r\n00:01:00,000 --> 00:01:02,250\r\nİkinci\r\n";
        const string vtt = "WEBVTT\n\nNOTE generated\n\nintro\n00:01.500 --> 00:03.000 align:start\n<i>Merhaba</i>\ndünya\n\n01:00.000 --> 01:02.250\n<v Narrator>İkinci</v>\n";

        var expected = new[]
        {
            new SubtitleCue(TimeSpan.FromSeconds(1.5), TimeSpan.FromSeconds(3), "<i>Merhaba</i>\ndünya"),
            new SubtitleCue(TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(62.25), "İkinci"),
        };
        Assert.Equal(expected, SubtitleFiles.Parse(srt));
        Assert.Equal(expected, SubtitleFiles.Parse(vtt));
    }

    [Fact]
    public void Non_utf8_bytes_are_read_as_turkish_windows_1254()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var bytes = Encoding.GetEncoding(1254).GetBytes("Şimdi ığdır");

        Assert.Equal("Şimdi ığdır", SubtitleFiles.Decode(bytes));
        Assert.Equal("Şimdi ığdır", SubtitleFiles.Decode(Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes("Şimdi ığdır")).ToArray()));
    }
}
