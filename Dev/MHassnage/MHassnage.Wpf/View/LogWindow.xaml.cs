using System.Windows;

namespace MHassnage.Wpf.View;

public partial class LogWindow : Window
{
    public LogWindow()
    {
        InitializeComponent();
        Log.CanPopOut = false;
        SourceInitialized += (_, _) => DarkTitleBar.Apply(this);
    }
}
