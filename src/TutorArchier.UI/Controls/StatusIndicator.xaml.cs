using System.Windows;
using System.Windows.Controls;
using Wpf.Ui.Controls;

namespace TutorArchier.UI.Controls;

public enum StatusKind
{
    None,
    Pending,
    Running,
    Success,
    Warning,
    Danger,
    Info,
}

/// <summary>Icon + colour + text, never colour alone (plan §6.1 principle 4).</summary>
public partial class StatusIndicator : UserControl
{
    public static readonly DependencyProperty KindProperty = DependencyProperty.Register(
        nameof(Kind), typeof(StatusKind), typeof(StatusIndicator),
        new PropertyMetadata(StatusKind.None, (d, _) => ((StatusIndicator)d).UpdateVisual()));

    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(
        nameof(Text), typeof(string), typeof(StatusIndicator),
        new PropertyMetadata(null, (d, _) => ((StatusIndicator)d).UpdateVisual()));

    public StatusIndicator()
    {
        InitializeComponent();
        UpdateVisual();
    }

    public StatusKind Kind
    {
        get => (StatusKind)GetValue(KindProperty);
        set => SetValue(KindProperty, value);
    }

    public string? Text
    {
        get => (string?)GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    private void UpdateVisual()
    {
        var (symbol, brushKey) = Kind switch
        {
            StatusKind.Pending => (SymbolRegular.Circle24, "Brush.TextMuted"),
            StatusKind.Success => (SymbolRegular.CheckmarkCircle24, "Brush.Success"),
            StatusKind.Warning => (SymbolRegular.Warning24, "Brush.Warning"),
            StatusKind.Danger => (SymbolRegular.DismissCircle24, "Brush.Danger"),
            StatusKind.Info => (SymbolRegular.Info24, "Brush.Info"),
            _ => (SymbolRegular.Empty, "Brush.TextMuted"),
        };

        var running = Kind == StatusKind.Running;
        Ring.Visibility = running ? Visibility.Visible : Visibility.Collapsed;
        Icon.Visibility = running || Kind == StatusKind.None ? Visibility.Collapsed : Visibility.Visible;
        Icon.Symbol = symbol;
        // Pending is an outline circle; everything else reads better filled.
        Icon.Filled = Kind != StatusKind.Pending;
        Icon.SetResourceReference(ForegroundProperty, brushKey);

        Label.Visibility = string.IsNullOrEmpty(Text) ? Visibility.Collapsed : Visibility.Visible;
        Label.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, Kind == StatusKind.Pending ? "Brush.TextMuted" : "Brush.Text");
    }
}
