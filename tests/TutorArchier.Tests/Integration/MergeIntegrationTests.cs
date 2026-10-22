using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using TutorArchier.Core;
using TutorArchier.Core.Media;
using TutorArchier.Engine.Ffmpeg;
using TutorArchier.Engine.Media;
using TutorArchier.Engine.Merge;

namespace TutorArchier.Tests.Integration;

public sealed class MergeIntegrationTests : IDisposable
{
    private readonly FfmpegRunner _runner = new(NullLogger<FfmpegRunner>.Instance);
    private readonly string _work = AppPaths.CreateScratchDirectory("test-merge");

    public void Dispose() => AppPaths.TryDeleteDirectory(_work);

    [FfmpegFact]
    public async Task Folder_becomes_one_mkv_with_chapters_and_shifted_subtitles()
    {
        var ffmpeg = TestFfmpeg.Locate(new FfmpegLocator(_runner, NullLogger<FfmpegLocator>.Instance));
        var course = Directory.CreateDirectory(Path.Combine(_work, "Course")).FullName;
        await ClipAsync(ffmpeg.FfmpegPath, Path.Combine(course, "01 Intro.mp4"), "320x240", 2);
        await ClipAsync(ffmpeg.FfmpegPath, Path.Combine(course, "02 Next.mp4"), "320x240", 1.5);
        await ClipAsync(ffmpeg.FfmpegPath, Path.Combine(course, "03 Odd.mp4"), "160x120", 1);
        File.WriteAllText(Path.Combine(course, "01 Intro.en.srt"), "1\n00:00:00,500 --> 00:00:01,500\nHello\n");
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        File.WriteAllBytes(Path.Combine(course, "02 Next.tr.srt"), Encoding.GetEncoding(1254).GetBytes("1\n00:00:00,250 --> 00:00:09,000\nŞimdi ığdır\n"));

        var analyzer = new FolderAnalyzer(new MediaProber(_runner, new MemoryProbeCache(), NullLogger<MediaProber>.Instance), NullLogger<FolderAnalyzer>.Instance);
        var analysis = await analyzer.AnalyzeAsync(course, ffmpeg.FfprobePath);
        var plan = MergePlanner.Create(analysis, MergePlanner.DefaultOutputPath(course), hardware: null);
        Assert.Equal([SegmentAction.Copy, SegmentAction.Copy, SegmentAction.EncodeVideo], plan.Segments.Select(s => s.Action));

        // Interrupt right after the re-encode, as a pause or app shutdown would.
        var mergeRunner = new MergeRunner(_runner, NullLogger<MergeRunner>.Instance);
        using var interrupt = new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            mergeRunner.RunAsync(ffmpeg, plan, _ => { }, steps => interrupt.Cancel(), interrupt.Token));
        Assert.True(File.Exists(plan.Prepared[0].EncodedPath));
        Assert.False(File.Exists(plan.OutputPath));

        // Resume: the finished encode is reused, only the join runs.
        var completed = 1;
        var messages = new List<string>();
        await mergeRunner.RunAsync(ffmpeg, plan, p => messages.Add(p.Text), steps => completed = steps);

        Assert.DoesNotContain(messages, m => m.Contains("kodlanıyor", StringComparison.Ordinal));
        Assert.Equal(plan.StepCount, completed);
        Assert.Equal(Path.Combine(_work, "Course.mkv"), plan.OutputPath);
        Assert.False(Directory.Exists(plan.WorkDirectory));

        var probe = await _runner.RunAsync(ffmpeg.FfprobePath, ["-v", "error", "-print_format", "json", "-show_chapters", "-show_streams", "-show_format", plan.OutputPath]);
        using var json = JsonDocument.Parse(probe.StdOut);
        var chapters = json.RootElement.GetProperty("chapters").EnumerateArray().ToList();
        Assert.Equal(["01 Intro", "02 Next", "03 Odd"], chapters.Select(c => c.GetProperty("tags").GetProperty("title").GetString()));
        var subtitleLanguages = json.RootElement.GetProperty("streams").EnumerateArray()
            .Where(s => s.GetProperty("codec_type").GetString() == "subtitle")
            .Select(s => s.GetProperty("tags").GetProperty("language").GetString());
        Assert.Equal(["eng", "tur"], subtitleLanguages);
        var duration = double.Parse(json.RootElement.GetProperty("format").GetProperty("duration").GetString()!, System.Globalization.CultureInfo.InvariantCulture);
        Assert.InRange(duration, 4.3, 4.8);

        // Turkish cue: decoded from 1254, moved to where lecture 2 starts (2 s), cut at its end (3.5 s).
        var turkish = await _runner.RunAsync(ffmpeg.FfmpegPath, ["-v", "error", "-i", plan.OutputPath, "-map", "0:s:1", "-f", "srt", "-"]);
        Assert.Contains("00:00:02,250 --> 00:00:03,500", turkish.StdOut);
        Assert.Contains("Şimdi ığdır", turkish.StdOut);
    }

    [FfmpegFact]
    public async Task Archive_mode_measures_a_quality_and_writes_hevc_10_bit()
    {
        var ffmpeg = TestFfmpeg.Locate(new FfmpegLocator(_runner, NullLogger<FfmpegLocator>.Instance));
        Assert.True(ffmpeg.HasLibVmaf, "archive mode needs an ffmpeg build with libvmaf");
        var course = Directory.CreateDirectory(Path.Combine(_work, "Archive")).FullName;
        await ClipAsync(ffmpeg.FfmpegPath, Path.Combine(course, "01 Intro.mp4"), "320x240", 2);
        await ClipAsync(ffmpeg.FfmpegPath, Path.Combine(course, "02 Next.mp4"), "320x240", 1.5);
        File.WriteAllText(Path.Combine(course, "slides.pdf"), "pdf");
        Directory.CreateDirectory(Path.Combine(course, "code"));
        File.WriteAllText(Path.Combine(course, "code", "main.py"), "print('hi')");
        var analyzer = new FolderAnalyzer(new MediaProber(_runner, new MemoryProbeCache(), NullLogger<MediaProber>.Instance), NullLogger<FolderAnalyzer>.Instance);
        var analysis = await analyzer.AnalyzeAsync(course, ffmpeg.FfprobePath);

        var (encoder, capability) = MergePlanner.ArchiveEncoder(hardware: null); // no report: libx265
        var search = await new QualitySearch(_runner, NullLogger<QualitySearch>.Instance).RunAsync(ffmpeg, analysis.Videos, encoder!, capability!);

        Assert.InRange(search.Quality, 18, 34);
        Assert.True(search.Vmaf >= QualitySearch.TargetVmaf || search.Quality == 18, $"VMAF {search.Vmaf} at CRF {search.Quality}");
        Assert.True(search.BytesPerSecond > 0);

        var output = MergePlanner.DefaultOutputPath(course);
        var plan = MergePlanner.Create(analysis, output, hardware: null, search.Quality) with { Resources = MergePlanner.Bundle(analysis, _work) };
        var completed = new List<int>();
        await new MergeRunner(_runner, NullLogger<MergeRunner>.Instance).RunAsync(ffmpeg, plan, _ => { }, completed.Add);

        // Two encodes, the zip and the join, reported one at a time even though the first three run together.
        Assert.Equal([1, 2, 3, 4], completed);
        using (var zip = System.IO.Compression.ZipFile.OpenRead(Path.Combine(_work, "resource", "Archive.zip")))
            Assert.Equal(["code/main.py", "slides.pdf"], zip.Entries.Select(e => e.FullName).Order());

        var probe = await _runner.RunAsync(ffmpeg.FfprobePath, ["-v", "error", "-print_format", "json", "-show_chapters", "-show_streams", "-show_format", plan.OutputPath]);
        using var json = JsonDocument.Parse(probe.StdOut);
        var video = json.RootElement.GetProperty("streams").EnumerateArray().First(s => s.GetProperty("codec_type").GetString() == "video");
        Assert.Equal("hevc", video.GetProperty("codec_name").GetString());
        Assert.Equal("yuv420p10le", video.GetProperty("pix_fmt").GetString());
        Assert.Equal(2, json.RootElement.GetProperty("chapters").GetArrayLength());
        var duration = double.Parse(json.RootElement.GetProperty("format").GetProperty("duration").GetString()!, System.Globalization.CultureInfo.InvariantCulture);
        Assert.InRange(duration, 3.3, 3.8);
    }

    [FfmpegFact]
    public async Task Subfolders_become_one_chaptered_mp4()
    {
        var ffmpeg = TestFfmpeg.Locate(new FfmpegLocator(_runner, NullLogger<FfmpegLocator>.Instance));
        var course = Directory.CreateDirectory(Path.Combine(_work, "Mp4")).FullName;
        Directory.CreateDirectory(Path.Combine(course, "01 Giriş"));
        Directory.CreateDirectory(Path.Combine(course, "02 Temeller"));
        await ClipAsync(ffmpeg.FfmpegPath, Path.Combine(course, "01 Giriş", "01 a.mp4"), "320x240", 1.5);
        await ClipAsync(ffmpeg.FfmpegPath, Path.Combine(course, "02 Temeller", "01 b.mp4"), "320x240", 1);
        File.WriteAllText(Path.Combine(course, "02 Temeller", "01 b.tr.srt"), "1\n00:00:00,100 --> 00:00:00,900\nMerhaba\n");
        var analyzer = new FolderAnalyzer(new MediaProber(_runner, new MemoryProbeCache(), NullLogger<MediaProber>.Instance), NullLogger<FolderAnalyzer>.Instance);
        var analysis = await analyzer.AnalyzeAsync(course, ffmpeg.FfprobePath);

        var plan = MergePlanner.Create(analysis, MergePlanner.DefaultOutputPath(course, mp4: true), hardware: null);
        Assert.Equal(".mp4", Path.GetExtension(plan.OutputPath));
        await new MergeRunner(_runner, NullLogger<MergeRunner>.Instance).RunAsync(ffmpeg, plan, _ => { }, _ => { });

        var probe = await _runner.RunAsync(ffmpeg.FfprobePath, ["-v", "error", "-print_format", "json", "-show_chapters", "-show_streams", "-show_format", plan.OutputPath]);
        using var json = JsonDocument.Parse(probe.StdOut);
        Assert.Contains("mp4", json.RootElement.GetProperty("format").GetProperty("format_name").GetString(), StringComparison.Ordinal);
        var chapters = json.RootElement.GetProperty("chapters").EnumerateArray().Select(c => c.GetProperty("tags").GetProperty("title").GetString());
        Assert.Equal(["01 Giriş › 01 a", "02 Temeller › 01 b"], chapters);
        var subtitle = json.RootElement.GetProperty("streams").EnumerateArray().Single(s => s.GetProperty("codec_type").GetString() == "subtitle");
        Assert.Equal("mov_text", subtitle.GetProperty("codec_name").GetString());
        Assert.Equal("tur", subtitle.GetProperty("tags").GetProperty("language").GetString());

        // The cue sits in lecture 2, which starts at 1.5 s.
        var cues = await _runner.RunAsync(ffmpeg.FfmpegPath, ["-v", "error", "-i", plan.OutputPath, "-map", "0:s:0", "-f", "srt", "-"]);
        Assert.Contains("00:00:01,600 --> 00:00:02,400", cues.StdOut);
    }

    [FfmpegFact]
    public async Task Still_is_a_320_px_jpeg_made_once_per_video()
    {
        var ffmpeg = TestFfmpeg.Locate(new FfmpegLocator(_runner, NullLogger<FfmpegLocator>.Instance));
        var clip = Path.Combine(_work, "clip.mp4");
        await ClipAsync(ffmpeg.FfmpegPath, clip, "640x360", 2);
        var prober = new MediaProber(_runner, new MemoryProbeCache(), NullLogger<MediaProber>.Instance);
        using var thumbnails = new ThumbnailCache(prober, _runner, NullLogger<ThumbnailCache>.Instance) { Directory = Path.Combine(_work, "thumbs") };

        var still = await thumbnails.GetAsync(ffmpeg, clip);

        Assert.NotNull(still);
        var probe = await _runner.RunAsync(ffmpeg.FfprobePath, ["-v", "error", "-show_entries", "stream=codec_name,width", "-of", "csv=p=0", still]);
        Assert.Equal("mjpeg,320", probe.StdOut.Trim());
        Assert.Equal(still, await thumbnails.GetAsync(ffmpeg, clip)); // cached: same file, no second ffmpeg run
    }

    private async Task ClipAsync(string ffmpeg, string path, string size, double seconds)
    {
        var d = seconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var result = await _runner.RunAsync(ffmpeg,
        [
            "-v", "error", "-y",
            "-f", "lavfi", "-i", $"testsrc2=s={size}:r=30:d={d}",
            "-f", "lavfi", "-i", $"sine=f=440:r=48000:d={d}",
            "-c:v", "libx264", "-preset", "ultrafast", "-pix_fmt", "yuv420p",
            "-c:a", "aac", "-ac", "2", "-shortest", path,
        ]);
        Assert.True(result.Success, result.StdErr);
    }

    private sealed class MemoryProbeCache : IProbeCache
    {
        public MediaInfo? Get(string path, long sizeBytes, long lastWriteTicks) => null;

        public void Set(string path, long sizeBytes, long lastWriteTicks, MediaInfo info)
        {
        }
    }
}
