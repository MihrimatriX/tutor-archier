using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using TutorArchier.UI.Services;

namespace TutorArchier.UI.Controls;

public partial class CourseHeader : UserControl
{
    public CourseHeader() => InitializeComponent();

    private void OnOpenInExplorer(object sender, RoutedEventArgs e)
    {
        if (DataContext is WorkspaceState { SelectedFolder: { } folder } && Directory.Exists(folder))
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{folder}\"") { UseShellExecute = true });
    }
}
