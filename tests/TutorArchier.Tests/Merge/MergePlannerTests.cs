using TutorArchier.Core.Hardware;
using TutorArchier.Core.Media;
using TutorArchier.Engine.Media;
using TutorArchier.Engine.Merge;

namespace TutorArchier.Tests.Merge;

public sealed class MergePlannerTests
{
    private static readonly VideoStreamInfo Hd = new(0, "h264", 1920, 1080, "yuv420p", 30, "1/15360");
    private static readonly AudioStreamInfo Aac = new(1, "aac", 48000, 2, "1/48000");

    [Fact]
    public void Majority_by_duration_is_copied_and_only_the_odd_ones_out_are_prepared()
    {
        var analysis = Analysis(
            Video("01 Intro.mp4", 600, Hd, Aac, ("en", @"C:\Course\01 Intro.en.srt")),
            Video("02 Setup.mp4", 900, Hd, Aac, ("en", @"C:\Course\02 Setup.en.srt"), ("tr", @"C:\Course\02 Setup.tr.srt")),
            Video("03 Old.mp4", 120, Hd with { Width = 1280, Height = 720 }, Aac),
            Video("04 Quiet.mp4", 60, Hd, Aac with { SampleRate = 44100 }),
            Video("05 Silent.mp4", 30, Hd, null),
            Video(@"Extras\06 Swapped.mov", 30, Hd with { Index = 1 }, Aac with { Index = 0 }),
            Video("07 Broken.mp4", 0, null, null));

        var plan = MergePlanner.Create(analysis, @"C:\Course.mkv", Hardware(nvencFps: 300));

        Assert.Equal(new FormatSignature("h264", 1920, 1080, "yuv420p", "1/15360", "aac", 48000, 2, "1/48000"), plan.Target);
        Assert.Equal("h264_nvenc", plan.EncoderId);
        Assert.Equal(
            [SegmentAction.Copy, SegmentAction.Copy, SegmentAction.EncodeVideo, SegmentAction.EncodeAudio, SegmentAction.EncodeAudio, SegmentAction.Remux],
            plan.Segments.Select(s => s.Action));
        Assert.Equal(["07 Broken.mp4"], plan.Skipped);
        Assert.Equal(5, plan.StepCount);
        Assert.Equal("Extras › 06 Swapped", plan.Segments[5].Title);
        // MP4 majority: prepared segments are MP4 in the same video timescale, so the demuxer reads them right.
        Assert.Equal(@"C:\Course.mkv.tutorarchier-parts\0003.mp4", plan.Segments[2].EncodedPath);

        var encode = plan.Segments[2].Arguments!;
        Assert.Contains("h264_nvenc", encode);
        Assert.Contains(encode, a => a.StartsWith("scale=1920:1080", StringComparison.Ordinal));
        Assert.Equal(["-video_track_timescale", "15360", "-f", "mp4", @"C:\Course.mkv.tutorarchier-parts\0003.mp4.partial"], encode.TakeLast(5));
        Assert.Contains(plan.Segments[4].Arguments!, a => a.StartsWith("anullsrc=r=48000:cl=stereo", StringComparison.Ordinal));
        Assert.Contains("copy", plan.Segments[4].Arguments!); // silent lecture: video untouched

        // One track per language; lectures without one get a gap.
        Assert.Equal(["en", "tr"], plan.SubtitleTracks.Select(t => t.Language));
        Assert.Equal([null, @"C:\Course\02 Setup.tr.srt", null, null, null, null], plan.SubtitleTracks[1].Files);
    }

    [Fact]
    public void A_silent_majority_does_not_drop_the_audio_of_the_others()
    {
        // Found with a demo course: 18 s silent + 15 s with sound → the merged video had no audio track.
        var plan = MergePlanner.Create(
            Analysis(Video("01 Talk.mp4", 15, Hd, Aac), Video("02 Screen.mp4", 18, Hd, null)), @"C:\Course.mkv", Hardware(nvencFps: 300));

        Assert.Equal("aac", plan.Target.AudioCodec);
        Assert.Equal([SegmentAction.Copy, SegmentAction.EncodeAudio], plan.Segments.Select(s => s.Action));
        Assert.Contains(plan.Segments[1].Arguments!, a => a.StartsWith("anullsrc=r=48000:cl=stereo", StringComparison.Ordinal));
    }

    [Fact]
    public void Time_bases_that_differ_are_rewrapped_and_unreproducible_ones_fall_back_to_mkv()
    {
        var mkv = Video("02 b.mkv", 5, Hd with { TimeBase = "1/1000" }, Aac with { TimeBase = "1/1000" });
        var mixed = MergePlanner.Create(Analysis(Video("01 a.mp4", 10, Hd, Aac), mkv), @"C:\out.mkv", null);
        Assert.Equal([SegmentAction.Copy, SegmentAction.Remux], mixed.Segments.Select(s => s.Action));
        Assert.Contains("15360", mixed.Segments[1].Arguments!);

        var avi = Hd with { TimeBase = "1001/30000" };
        var odd = MergePlanner.Create(Analysis(Video("01 a.avi", 10, avi, Aac), Video("02 b.avi", 5, avi, Aac)), @"C:\out.mkv", null);
        Assert.Equal("1/1000", odd.Target.VideoTimeBase);
        Assert.All(odd.Segments, s => Assert.Equal(SegmentAction.Remux, s.Action));
        Assert.EndsWith(".mkv", odd.Segments[0].EncodedPath);
    }

    [Fact]
    public void Archive_mode_encodes_every_lecture_to_hevc_10_bit_at_the_searched_quality()
    {
        var hevc10 = Hd with { Codec = "hevc", PixelFormat = "yuv420p10le", TimeBase = "1/1000" };
        var analysis = Analysis(
            Video("01 a.mp4", 600, Hd, Aac),
            Video("02 b.mp4", 300, Hd with { Width = 1280, Height = 720 }, Aac),
            Video("03 c.mkv", 60, hevc10, Aac with { TimeBase = "1/1000" }));

        var plan = MergePlanner.Create(analysis, @"C:\Course.mkv", Hardware(nvencFps: 300), archiveQuality: 27);

        Assert.Equal(new FormatSignature("hevc", 1920, 1080, "yuv420p10le", "1/90000", "aac", 48000, 2, "1/48000"), plan.Target);
        Assert.Equal("hevc_nvenc", plan.EncoderId);
        // Even the lecture that is HEVC 10-bit already: another encoder's parameter sets can't share the join.
        Assert.All(plan.Segments, s => Assert.Equal(SegmentAction.EncodeVideo, s.Action));
        var encode = plan.Segments[1].Arguments!;
        Assert.Equal(["-cq", "27"], encode.SkipWhile(a => a != "-cq").Take(2));
        Assert.Contains("p010le", encode);
        Assert.DoesNotContain("-bf", encode); // Pascal HEVC: no B-frames
        Assert.Contains(encode, a => a.StartsWith("scale=1920:1080", StringComparison.Ordinal));
        // MP4 parts, like the lossless path: the edit list keeps AAC's encoder delay out of the timeline.
        Assert.Equal(["-video_track_timescale", "90000", "-f", "mp4"], encode.TakeLast(5).Take(4));
        Assert.Equal(27, MergePlan.FromJson(plan.ToJson()).ArchiveQuality);
    }

    [Fact]
    public void Each_subfolder_becomes_its_own_video_with_chapters_relative_to_it()
    {
        var analysis = Analysis(
            Video("00 Tanıtım.mp4", 30, Hd, Aac),
            Video(@"01 Giriş\01 Hoş geldin.mp4", 60, Hd, Aac),
            Video(@"01 Giriş\Ekler\02 Kurulum.mp4", 90, Hd, Aac),
            Video(@"02 Temeller\03 İlk adım.mp4", 120, Hd, Aac),
            Video(@"03 Bozuk\04 Okunmuyor.mp4", 0, null, null));

        var plans = MergePlanner.CreatePerSubfolder(analysis, @"C:\Course (MKV)", null);

        // Loose videos take the course's name; a subfolder with nothing readable makes no video.
        Assert.Equal([@"C:\Course (MKV)\Course.mkv", @"C:\Course (MKV)\01 Giriş.mkv", @"C:\Course (MKV)\02 Temeller.mkv"], plans.Select(p => p.OutputPath));
        Assert.Equal(["Course", "Course › 01 Giriş", "Course › 02 Temeller"], plans.Select(p => p.Title));
        Assert.Equal(["01 Hoş geldin", "Ekler › 02 Kurulum"], plans[1].Segments.Select(s => s.Title));
        Assert.Equal(@"C:\Course\01 Giriş", plans[1].SourceFolder);
    }

    [Fact]
    public void Plan_survives_a_round_trip_through_the_job_store()
    {
        var plan = MergePlanner.Create(Analysis(Video("a.mp4", 10, Hd, Aac), Video("b.mp4", 5, Hd with { Width = 1280 }, Aac)), @"C:\out.mkv", null);

        var restored = MergePlan.FromJson(plan.ToJson());

        Assert.Equal(plan.Segments.Select(s => (s.Action, s.Duration, s.ConcatPath)), restored.Segments.Select(s => (s.Action, s.Duration, s.ConcatPath)));
        Assert.Equal(plan.Segments[1].Arguments, restored.Segments[1].Arguments);
        Assert.Equal("libx264", restored.EncoderId); // no hardware report: software only
    }

    [Fact]
    public void Concat_list_and_chapters_escape_their_syntax()
    {
        var plan = MergePlanner.Create(
            Analysis(Video("It's #1; a=b.mp4", 61.5, Hd, Aac), Video("two.mp4", 30, Hd, Aac)),
            @"C:\out.mkv",
            null);

        Assert.Contains(@"file 'C:\Course\It'\''s #1; a=b.mp4'" + "\nduration 61.5\n", MergeRunner.ConcatList(plan));
        var chapters = MergeRunner.ChapterMetadata(plan);
        Assert.Contains("START=0\nEND=61500\ntitle=It's \\#1\\; a\\=b\n", chapters);
        Assert.Contains("START=61500\nEND=91500\ntitle=two\n", chapters);
    }

    [Fact]
    public void Mp4_output_converts_what_mp4_cannot_carry()
    {
        var vorbis = new AudioStreamInfo(1, "vorbis", 48000, 2, "1/1000");
        var analysis = Analysis(Video("01 a.mkv", 10, Hd with { TimeBase = "1/1000" }, vorbis), Video("02 b.mkv", 5, Hd with { TimeBase = "1/1000" }, vorbis));

        Assert.Equal("vorbis", MergePlanner.Create(analysis, @"C:\out.mkv", null).Target.AudioCodec);
        var mp4 = MergePlanner.Create(analysis, @"C:\out.mp4", null);
        Assert.Equal("aac", mp4.Target.AudioCodec);
        Assert.All(mp4.Segments, s => Assert.Equal(SegmentAction.EncodeAudio, s.Action)); // video still copied
    }

    [Fact]
    public void Bundle_keeps_everything_the_mkvs_leave_out()
    {
        var analysis = new FolderAnalysis(
            @"C:\Course",
            [Video("01 a.mp4", 10, Hd, Aac, ("en", @"C:\Course\01 a.en.srt")), Video("02 broken.mp4", 0, null, null, ("en", @"C:\Course\02 broken.en.srt"))],
            [@"C:\Course\notes.srt"],
            [@"C:\Course\01 a.ass"],
            [@"C:\Course\code\main.py", @"C:\Course\slides.pdf"]);

        var bundle = MergePlanner.Bundle(analysis, @"C:\Out");

        Assert.NotNull(bundle);
        Assert.Equal(@"C:\Out\resource\Course.zip", bundle.ArchivePath);
        Assert.Equal(@"C:\Course", bundle.Root);
        // Merged lectures and their subtitles stay out; the unreadable video comes along with its subtitle.
        Assert.Equal(
            [@"C:\Course\01 a.ass", @"C:\Course\02 broken.en.srt", @"C:\Course\02 broken.mp4", @"C:\Course\code\main.py", @"C:\Course\notes.srt", @"C:\Course\slides.pdf"],
            bundle.Files);
        Assert.Null(MergePlanner.Bundle(Analysis(Video("01 a.mp4", 10, Hd, Aac)), @"C:\Out"));

        var plan = MergePlanner.Create(analysis, @"C:\Out\Course.mkv", null) with { Resources = bundle };
        Assert.Equal(2, plan.StepCount); // nothing to prepare, the zip, the join
        Assert.Equal(bundle.Files, MergePlan.FromJson(plan.ToJson()).Resources!.Files);
    }

    private static FolderAnalysis Analysis(params AnalyzedVideo[] videos) => new(@"C:\Course", videos, [], [], []);

    private static AnalyzedVideo Video(string relative, double seconds, VideoStreamInfo? video, AudioStreamInfo? audio, params (string Language, string Path)[] subtitles)
    {
        var info = video is null ? null : new MediaInfo(TimeSpan.FromSeconds(seconds), 1000, "mov,mp4", video, audio, 0);
        return new AnalyzedVideo(
            new ScannedVideo(Path.Combine(@"C:\Course", relative), relative, subtitles.Select(s => new SubtitleFile(s.Path, s.Language)).ToList()),
            1000,
            info,
            info is null ? "Invalid data found when processing input" : null);
    }

    private static HardwareReport Hardware(double nvencFps) => new(
        "fp", DateTimeOffset.Now, new FfmpegInfo("ffmpeg.exe", "ffprobe.exe", "8.1", new Version(8, 1), true, true, true), "CPU", [],
        [
            new EncoderCapability("h264_nvenc", Available: true, TenBit: false, BFrames: true, BenchmarkFps: nvencFps),
            new EncoderCapability("hevc_nvenc", Available: true, TenBit: true, BFrames: false, BenchmarkFps: nvencFps),
            new EncoderCapability("libx264", Available: true, TenBit: true, BFrames: true, BenchmarkFps: 30),
        ]);
}
