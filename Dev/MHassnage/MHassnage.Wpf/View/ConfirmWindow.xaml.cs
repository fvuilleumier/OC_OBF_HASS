using System.Windows;
using System.Windows.Media;

namespace MHassnage.Wpf.View;

/// <summary>Boîte de confirmation au thème sombre de l'application.</summary>
public partial class ConfirmWindow : Window
{
    private ConfirmWindow(string title, string message, string okText, bool danger)
    {
        InitializeComponent();
        Title = title;
        Heading.Text = title;
        Body.Text = message;
        OkButton.Content = okText;
        if (danger) OkButton.Background = new SolidColorBrush(Color.FromRgb(0xA8, 0x43, 0x4D));
        SourceInitialized += (_, _) => DarkTitleBar.Apply(this);
    }

    /// <returns>Vrai si l'utilisateur a confirmé.</returns>
    public static bool Ask(string title, string message, string okText, bool danger = false)
    {
        var window = new ConfirmWindow(title, message, okText, danger)
        {
            Owner = Application.Current?.MainWindow,
        };
        return window.ShowDialog() == true;
    }

    private void OnOk(object sender, RoutedEventArgs e) => DialogResult = true;
    private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;
}
