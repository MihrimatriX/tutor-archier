using System.Windows.Controls;
using TutorArchier.UI.ViewModels;

namespace TutorArchier.UI.Views.Pages;

public partial class MergePage : Page
{
    public MergePage(MergeViewModel viewModel)
    {
        DataContext = viewModel;
        InitializeComponent();
        Loaded += async (_, _) => await viewModel.OnShownAsync();
    }
}
