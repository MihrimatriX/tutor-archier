namespace TutorArchier.Core.Media;

/// <summary>What ffprobe reports about one file. Codec names are ffmpeg's (<c>h264</c>, <c>aac</c>, …).</summary>
public sealed record MediaInfo(
    TimeSpan Duration,
    long SizeBytes,
    string Container,
    VideoStreamInfo? Video,
    AudioStreamInfo? Audio,
    int EmbeddedSubtitleCount);

/// <param name="Index">Stream index in the file; ffmpeg's concat demuxer pairs streams by index.</param>
/// <param name="FrameRate">Average frame rate; 0 when ffprobe can't tell.</param>
/// <param name="TimeBase">e.g. "1/15360". The concat demuxer reads every file in the first file's time base.</param>
public sealed record VideoStreamInfo(int Index, string Codec, int Width, int Height, string PixelFormat, double FrameRate, string TimeBase);

public sealed record AudioStreamInfo(int Index, string Codec, int SampleRate, int Channels, string TimeBase);

/// <summary>Cached ffprobe results, keyed by path and invalidated by size or modification time.</summary>
public interface IProbeCache
{
    MediaInfo? Get(string path, long sizeBytes, long lastWriteTicks);

    void Set(string path, long sizeBytes, long lastWriteTicks, MediaInfo info);
}
