using System.Globalization;
using System.IO;
using System.Windows;
using Microsoft.Win32;

namespace TutorArchier.UI.Services;

public static class UiCulture
{
    /// <summary>UI text is Turkish (an English UI is a later phase), so numbers and dates follow tr-TR.</summary>
    public static CultureInfo Current { get; } = CultureInfo.GetCultureInfo("tr-TR");

    public static string FormatDateTime(DateTimeOffset value) =>
        value.ToLocalTime().ToString("d MMMM yyyy, HH:mm", Current);

    /// <summary>When something last happened, for the recent courses on Home.</summary>
    public static string FormatAgo(DateTimeOffset value)
    {
        // TODO(human): turn the time since `value` into a short Turkish phrase for the recent-course cards.
        return FormatDateTime(value);
    }

    /// <summary>"14 sa 32 dk", "7 dk 5 sn", "42 sn".</summary>
    public static string FormatDuration(TimeSpan value) => value.TotalHours >= 1
        ? $"{(int)value.TotalHours} sa {value.Minutes} dk"
        : value.TotalMinutes >= 1 ? $"{value.Minutes} dk {value.Seconds} sn" : $"{Math.Max(0, (int)value.TotalSeconds)} sn";

    /// <summary>Position on a timeline: "1:02:03".</summary>
    public static string FormatClock(TimeSpan value) =>
        string.Create(Current, $"{(int)value.TotalHours}:{value.Minutes:00}:{value.Seconds:00}");

    public static string FormatSize(long bytes) => bytes >= 1L << 30
        ? string.Create(Current, $"{bytes / (double)(1L << 30):0.0} GB")
        : string.Create(Current, $"{bytes / (double)(1L << 20):0} MB");

    public static string FormatCodec(string codec) => codec switch
    {
        "h264" => "H.264",
        "hevc" => "HEVC",
        "av1" => "AV1",
        "vp9" => "VP9",
        "mpeg4" => "MPEG-4",
        "aac" => "AAC",
        "mp3" => "MP3",
        "opus" => "Opus",
        "ac3" => "AC-3",
        "eac3" => "E-AC-3",
        "flac" => "FLAC",
        _ => codec.ToUpperInvariant(),
    };

    public static string FormatChannels(int channels) => channels switch
    {
        1 => "mono",
        2 => "stereo",
        6 => "5.1",
        8 => "7.1",
        _ => $"{channels} kanal",
    };
}

public static class UiDispatcher
{
    /// <summary>Runs on the UI thread; service events arrive on worker threads.</summary>
    public static void Post(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null)
            return;
        if (dispatcher.CheckAccess())
            action();
        else
            dispatcher.BeginInvoke(action);
    }
}

public static class FolderPicker
{
    public static string? Pick(string title, string? initialDirectory = null)
    {
        var dialog = new OpenFolderDialog
        {
            Title = title,
            Multiselect = false,
        };
        if (!string.IsNullOrEmpty(initialDirectory) && Directory.Exists(initialDirectory))
            dialog.InitialDirectory = initialDirectory;

        return dialog.ShowDialog(Application.Current?.MainWindow) == true ? dialog.FolderName : null;
    }
}
