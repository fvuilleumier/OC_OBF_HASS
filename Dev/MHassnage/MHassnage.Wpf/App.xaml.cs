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

        Core.Model.AppSettings settings;
        try
        {
            settings = SettingsService.Load();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Configuration illisible :\n{SettingsService.SettingsPath}\n\n{ex.Message}",
                            "MHassnage", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
            return;
        }
        var navigation = new NavigationController();
        var main = new MainViewController(settings, navigation);

        new MainWindow { DataContext = main }.Show();
    }
}
