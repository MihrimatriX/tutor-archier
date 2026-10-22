using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Wpf.Ui.Controls;

namespace TutorArchier.UI.Controls;

/// <summary>
/// A still of <see cref="Video"/> (made by ffmpeg, cached on disk) with a quiet placeholder until it's ready.
/// The picture is the Border's background so the corner radius clips it; an Image child would keep square corners.
/// Recycled list rows re-point <see cref="Video"/>, so a late result for the previous video is dropped.
/// </summary>
public sealed class VideoThumb : Border
{
    public static readonly DependencyProperty VideoProperty = DependencyProperty.Register(
        nameof(Video), typeof(string), typeof(VideoThumb), new PropertyMetadata(null, (d, e) => ((VideoThumb)d).Load(e.NewValue as string)));

    private readonly SymbolIcon _placeholder = new()
    {
        Symbol = SymbolRegular.Video24,
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center,
        Opacity = 0.45,
    };

    public VideoThumb()
    {
        CornerRadius = new CornerRadius(6);
        _placeholder.SetResourceReference(IconElement.ForegroundProperty, "Brush.TextMuted");
        Child = _placeholder;
        ShowPlaceholder();
    }

    /// <summary>Turns a video path into a cached still; set once at startup (it needs the current ffmpeg).</summary>
    public static Func<string, Task<string?>>? Loader { get; set; }

    public string? Video
    {
        get => (string?)GetValue(VideoProperty);
        set => SetValue(VideoProperty, value);
    }

    private async void Load(string? video)
    {
        ShowPlaceholder();
        if (video is null || Loader is null)
            return;

        try
        {
            var still = await Loader(video);
            if (still is null || Video != video)
                return;

            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad; // read now, so the file isn't held open
            bitmap.UriSource = new Uri(still);
            bitmap.EndInit();
            bitmap.Freeze();
            Background = new ImageBrush(bitmap) { Stretch = Stretch.UniformToFill };
            _placeholder.Visibility = Visibility.Collapsed;
        }
        catch (Exception ex) when (ex is IOException or NotSupportedException or InvalidOperationException or UnauthorizedAccessException)
        {
            // A still that can't be read just leaves the placeholder.
        }
    }

    private void ShowPlaceholder()
    {
        SetResourceReference(BackgroundProperty, "Brush.Surface2");
        _placeholder.Visibility = Visibility.Visible;
    }
}
