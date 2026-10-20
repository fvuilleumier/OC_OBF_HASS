using System.Collections.Specialized;
using System.Windows.Controls;
using MHassnage.Wpf.ViewController;

namespace MHassnage.Wpf.View;

/// <summary>Journal : défile automatiquement jusqu'à la dernière ligne.</summary>
public partial class LogView : UserControl
{
    public LogView()
    {
        InitializeComponent();
        DataContextChanged += (_, e) =>
        {
            if (e.OldValue is LogViewController old) old.Lines.CollectionChanged -= ScrollToEnd;
            if (e.NewValue is LogViewController log) log.Lines.CollectionChanged += ScrollToEnd;
        };
    }

    private void ScrollToEnd(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (List.Items.Count > 0) List.ScrollIntoView(List.Items[^1]);
    }
}
