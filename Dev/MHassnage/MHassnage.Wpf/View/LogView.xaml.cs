using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using MHassnage.Wpf.ViewController;

namespace MHassnage.Wpf.View;

/// <summary>
/// Journal : colle toujours au bas pendant le travail ; bouton pour l'ouvrir en grand
/// dans une fenêtre (à sa fermeture, le journal revient dans son panel).
/// </summary>
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
        Unloaded += (_, _) =>
        {
            if (DataContext is LogViewController log) log.Lines.CollectionChanged -= ScrollToEnd;
        };
        Loaded += (_, _) =>
        {
            if (DataContext is LogViewController log)
            {
                log.Lines.CollectionChanged -= ScrollToEnd;
                log.Lines.CollectionChanged += ScrollToEnd;
            }
            ScrollToEnd(null, null);
        };
    }

    /// <summary>Masque le bouton « ouvrir en grand » (fenêtre détachée).</summary>
    public bool CanPopOut
    {
        get => PopOutButton.Visibility == Visibility.Visible;
        set => PopOutButton.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Défilement après la mise en page (sinon la dernière ligne n'est pas encore générée).</summary>
    private void ScrollToEnd(object? sender, NotifyCollectionChangedEventArgs? e) =>
        Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            if (List.Items.Count > 0) List.ScrollIntoView(List.Items[^1]);
        });

    private void OnPopOut(object sender, RoutedEventArgs e)
    {
        if (DataContext is not LogViewController log || log.IsPoppedOut) return;
        log.IsPoppedOut = true;
        var window = new LogWindow { DataContext = log, Owner = Window.GetWindow(this) };
        window.Closed += (_, _) => log.IsPoppedOut = false;
        window.Show();
    }
}
