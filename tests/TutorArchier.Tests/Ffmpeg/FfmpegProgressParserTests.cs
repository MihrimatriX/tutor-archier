using System.Globalization;
using TutorArchier.Engine.Ffmpeg;

namespace TutorArchier.Tests.Ffmpeg;

public class FfmpegProgressParserTests
{
    [Fact]
    public void Emits_one_progress_per_block()
    {
        var parser = new FfmpegProgressParser();
        string[] block =
        [
            "frame=120", "fps=30.5", "stream_0_0_q=23.0", "bitrate=1500.2kbits/s",
            "total_size=1048576", "out_time_us=4000000", "out_time_ms=4000000",
            "out_time=00:00:04.000000", "dup_frames=0", "drop_frames=0", "speed=2.5x",
        ];

        foreach (var line in block)
            Assert.Null(parser.Feed(line));
        var progress = parser.Feed("progress=continue");

        Assert.NotNull(progress);
        Assert.Equal(120, progress.Frame);
        Assert.Equal(30.5, progress.Fps);
        Assert.Equal(TimeSpan.FromSeconds(4), progress.OutTime);
        Assert.Equal(1048576, progress.TotalSizeBytes);
        Assert.Equal(2.5, progress.Speed);
        Assert.False(progress.IsEnd);
    }

    [Fact]
    public void Handles_not_available_values_at_start()
    {
        var parser = new FfmpegProgressParser();
        parser.Feed("frame=0");
        parser.Feed("fps=0.00");
        parser.Feed("total_size=N/A");
        parser.Feed("out_time_us=-9223372036854775807");
        parser.Feed("speed=N/A");

        var progress = parser.Feed("progress=continue")!;

        Assert.Equal(0, progress.TotalSizeBytes);
        Assert.Equal(TimeSpan.Zero, progress.OutTime);
        Assert.Null(progress.Speed);
    }

    [Fact]
    public void Values_do_not_leak_into_the_next_block()
    {
        var parser = new FfmpegProgressParser();
        parser.Feed("frame=10");
        parser.Feed("speed=1.0x");
        parser.Feed("progress=continue");

        parser.Feed("frame=20");
        var second = parser.Feed("progress=end")!;

        Assert.Equal(20, second.Frame);
        Assert.Null(second.Speed);
        Assert.True(second.IsEnd);
    }

    [Fact]
    public void Parsing_ignores_the_current_culture()
    {
        // tr-TR uses ',' as the decimal separator; ffmpeg always writes '.'.
        var original = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("tr-TR");
        try
        {
            var parser = new FfmpegProgressParser();
            parser.Feed("fps=29.97");
            parser.Feed("speed=1.25x");
            var progress = parser.Feed("progress=continue")!;

            Assert.Equal(29.97, progress.Fps);
            Assert.Equal(1.25, progress.Speed);
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData("no separator here")]
    [InlineData("=value without key")]
    public void Ignores_malformed_lines(string line)
    {
        Assert.Null(new FfmpegProgressParser().Feed(line));
    }
}
