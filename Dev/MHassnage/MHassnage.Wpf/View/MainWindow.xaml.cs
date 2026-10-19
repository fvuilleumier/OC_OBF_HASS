using System.Windows;
using System.Windows.Controls;
using MHassnage.Wpf.ViewController;

namespace MHassnage.Wpf.View;

public partial class MainWindow : Window
{
    public MainWindow() => InitializeComponent();

    private void OnOperationChecked(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton { Tag: OperationItem item } && DataContext is MainViewController vc)
            vc.Selected = item;
    }
}
