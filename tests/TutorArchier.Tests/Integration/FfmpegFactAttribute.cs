using TutorArchier.Core.Hardware;
using TutorArchier.Engine.Ffmpeg;

namespace TutorArchier.Tests.Integration;

/// <summary>Runs the test only on machines where ffmpeg can be found the same way the app finds it.</summary>
public sealed class FfmpegFactAttribute : FactAttribute
{
    public FfmpegFactAttribute()
    {
        if (TestFfmpeg.Directory is null)
            Skip = "ffmpeg not found on this machine";
    }
}

internal static class TestFfmpeg
{
    private static readonly Lazy<string?> LazyDirectory = new(() =>
        FfmpegLocator.CandidateDirectories(null).FirstOrDefault(dir =>
            File.Exists(Path.Combine(dir, OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg"))));

    public static string? Directory => LazyDirectory.Value;

    public static FfmpegInfo Locate(FfmpegLocator locator) =>
        locator.InspectAsync(Directory!).GetAwaiter().GetResult()
        ?? throw new InvalidOperationException("ffmpeg was found but could not be inspected");
}
