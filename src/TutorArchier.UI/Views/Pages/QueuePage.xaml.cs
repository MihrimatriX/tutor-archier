using System.Windows.Controls;
using TutorArchier.UI.ViewModels;

namespace TutorArchier.UI.Views.Pages;

public partial class QueuePage : Page
{
    public QueuePage(QueueViewModel viewModel)
    {
        DataContext = viewModel;
        InitializeComponent();
    }
}
