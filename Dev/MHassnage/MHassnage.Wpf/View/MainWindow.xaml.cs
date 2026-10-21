using System.Windows;
using System.Windows.Controls;
using MHassnage.Wpf.ViewController;

namespace MHassnage.Wpf.View;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        SourceInitialized += (_, _) => DarkTitleBar.Apply(this);
    }

    private void OnOperationChecked(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton { Tag: OperationItem item } && DataContext is MainViewController vc)
            vc.Selected = item;
    }

    /// <summary>Coche la tuile de l'opération affichée au démarrage.</summary>
    private void OnTileLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton { Tag: OperationItem item } tile && DataContext is MainViewController vc)
            tile.IsChecked = ReferenceEquals(item, vc.Selected);
    }
}
