using TutorArchier.UI.ViewModels;
using TutorArchier.UI.Views.Pages;
using Wpf.Ui;
using Wpf.Ui.Controls;

namespace TutorArchier.UI.Views;

public partial class MainWindow : FluentWindow
{
    public MainWindow(MainWindowViewModel viewModel, IServiceProvider services, INavigationService navigation)
    {
        DataContext = viewModel;
        InitializeComponent();

        RootNavigation.SetServiceProvider(services);
        navigation.SetNavigationControl(RootNavigation);

        Loaded += async (_, _) =>
        {
            RootNavigation.Navigate(typeof(HomePage));
            await viewModel.InitializeAsync();
        };
    }
}
