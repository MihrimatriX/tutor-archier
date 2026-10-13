using TutorArchier.Engine.Media;

namespace TutorArchier.Tests.Media;

public sealed class FolderScannerTests
{
    private const string Root = @"C:\Course";

    [Fact]
    public void Subtitles_pair_with_their_video_and_carry_a_language()
    {
        var result = FolderScanner.Match(Root,
        [
            @"C:\Course\01 Intro.mp4",
            @"C:\Course\01 Intro.en.srt",
            @"C:\Course\01 Intro_tr.vtt",
            @"C:\Course\01 Intro [Turkish].srt",
            @"C:\Course\01 Introduction.mp4",
            @"C:\Course\01 Introduction.srt",
            @"C:\Course\notes.srt",
            @"C:\Course\01 Intro.ass",
            @"C:\Course\code\main.py",
        ]);

        var intro = result.Videos.Single(v => v.RelativePath == "01 Intro.mp4");
        // The longest matching video name wins, so "01 Introduction.srt" isn't stolen by "01 Intro".
        Assert.Equal(["en", "tr", "tr"], intro.Subtitles.Select(s => s.Language));
        Assert.Equal(".srt", Path.GetExtension(intro.Subtitles[1].FullPath)); // SRT preferred over VTT
        Assert.Equal([""], result.Videos.Single(v => v.RelativePath == "01 Introduction.mp4").Subtitles.Select(s => s.Language));
        Assert.Equal([@"C:\Course\notes.srt"], result.OrphanSubtitles);
        Assert.Equal([@"C:\Course\01 Intro.ass"], result.UnsupportedSubtitles);
        Assert.Equal([@"C:\Course\code\main.py"], result.OtherFiles);
    }

    [Fact]
    public void Videos_are_in_explorer_order_across_folders()
    {
        var result = FolderScanner.Match(Root,
        [
            @"C:\Course\Section 10\1 a.mp4",
            @"C:\Course\Section 2\10 b.mp4",
            @"C:\Course\Section 2\9 c.mp4",
        ]);

        Assert.Equal([@"Section 2\9 c.mp4", @"Section 2\10 b.mp4", @"Section 10\1 a.mp4"], result.Videos.Select(v => v.RelativePath));
    }

    [Theory]
    [InlineData(".en", "en")]
    [InlineData("_English", "en")]
    [InlineData(" - eng-US", "en")]
    [InlineData(".tr", "tr")]
    [InlineData("_Türkçe", "tr")]
    [InlineData(".de", "de")]
    [InlineData("", "")]
    [InlineData(" (2)", "")]
    public void Language_comes_from_the_name_suffix(string suffix, string expected) =>
        Assert.Equal(expected, FolderScanner.DetectLanguage(suffix));
}
