using System.Windows;
using MHassnage.Core.Service;
using MHassnage.Wpf.Controller;
using MHassnage.Wpf.View;
using MHassnage.Wpf.ViewController;

namespace MHassnage.Wpf;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var settings = SettingsService.Load();
        var navigation = new NavigationController();
        var main = new MainViewController(settings, navigation);

        new MainWindow { DataContext = main }.Show();
    }
}
