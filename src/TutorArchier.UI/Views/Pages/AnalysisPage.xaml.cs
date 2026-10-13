using System.Windows.Controls;
using TutorArchier.UI.ViewModels;

namespace TutorArchier.UI.Views.Pages;

public partial class AnalysisPage : Page
{
    public AnalysisPage(AnalysisViewModel viewModel)
    {
        DataContext = viewModel;
        InitializeComponent();
        // Pages are singletons; Loaded fires each time navigation shows this one again.
        Loaded += async (_, _) => await viewModel.OnShownAsync();
    }
}
