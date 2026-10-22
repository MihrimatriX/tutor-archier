using System.Windows;
using System.Windows.Controls;
using TutorArchier.UI.ViewModels;

namespace TutorArchier.UI.Views.Pages;

public partial class HomePage : Page
{
    private readonly HomeViewModel _viewModel;

    public HomePage(HomeViewModel viewModel)
    {
        _viewModel = viewModel;
        DataContext = viewModel;
        InitializeComponent();
        Loaded += async (_, _) => await viewModel.OnShownAsync();
    }

    private void OnDragOver(object sender, DragEventArgs e)
    {
        e.Handled = true;
        if (GetDroppedPath(e) is null)
        {
            e.Effects = DragDropEffects.None;
            return;
        }

        e.Effects = DragDropEffects.Copy;
        DropPrompt.Visibility = Visibility.Hidden;
        SplitOverlay.Visibility = Visibility.Visible;
        Highlight(IsOverMergeHalf(e));
    }

    private void OnDragLeave(object sender, DragEventArgs e)
    {
        // DragLeave also fires when crossing child elements; only reset once the pointer has really left.
        var point = e.GetPosition(DropZone);
        if (point.X > 0 && point.Y > 0 && point.X < DropZone.ActualWidth && point.Y < DropZone.ActualHeight)
            return;
        ResetDropZone();
    }

    private void OnDrop(object sender, DragEventArgs e)
    {
        var path = GetDroppedPath(e);
        var merge = IsOverMergeHalf(e);
        ResetDropZone();
        if (path is not null)
            _viewModel.OpenDroppedPath(path, merge);
    }

    private bool IsOverMergeHalf(DragEventArgs e) => e.GetPosition(DropZone).X >= DropZone.ActualWidth / 2;

    private void Highlight(bool merge)
    {
        Style(AnalyzeHalf, active: !merge);
        Style(MergeHalf, active: merge);

        static void Style(Border half, bool active)
        {
            half.SetResourceReference(Border.BackgroundProperty, active ? "Brush.AccentSubtle" : "Brush.Surface");
            half.SetResourceReference(Border.BorderBrushProperty, active ? "Brush.Accent" : "Brush.Border");
            half.Opacity = active ? 1 : 0.6;
        }
    }

    private void ResetDropZone()
    {
        SplitOverlay.Visibility = Visibility.Collapsed;
        DropPrompt.Visibility = Visibility.Visible;
    }

    private static string? GetDroppedPath(DragEventArgs e) =>
        e.Data.GetData(DataFormats.FileDrop) is string[] { Length: > 0 } paths ? paths[0] : null;
}
