using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Threading;
using MHassnage.Wpf.ViewController;

namespace MHassnage.Wpf.View;

/// <summary>
/// Journal : texte sélectionnable et copiable, colle toujours au bas, bouton pour l'ouvrir en grand
/// dans une fenêtre (à sa fermeture, il revient dans son panel).
/// </summary>
public partial class LogView : UserControl
{
    private static readonly Brush Muted = Frozen("#6B7380");
    private static readonly Brush Step = Frozen("#41BDF5");
    private static readonly Brush Ok = Frozen("#6CC17A");
    private static readonly Brush Warning = Frozen("#F5A623");
    private static readonly Brush Error = Frozen("#E06C75");
    private static readonly Brush Normal = Frozen("#E6E9EE");

    private LogViewController? _log;

    public LogView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => Attach(DataContext as LogViewController);
        Loaded += (_, _) => Attach(DataContext as LogViewController);
        Unloaded += (_, _) => Attach(null);
    }

    /// <summary>Masque le bouton « ouvrir en grand » (fenêtre détachée).</summary>
    public bool CanPopOut
    {
        get => PopOutButton.Visibility == Visibility.Visible;
        set => PopOutButton.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Attach(LogViewController? log)
    {
        if (_log is not null) _log.Lines.CollectionChanged -= OnLinesChanged;
        _log = log;
        Text.Document.Blocks.Clear();
        if (_log is null) return;
        _log.Lines.CollectionChanged += OnLinesChanged;
        foreach (var line in _log.Lines) Append(line);
        ScrollToEnd();
    }

    private void OnLinesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Add && e.NewItems is not null)
            foreach (LogLine line in e.NewItems) Append(line);
        else
        {
            Text.Document.Blocks.Clear();
            foreach (var line in _log?.Lines ?? []) Append(line);
        }
        ScrollToEnd();
    }

    private void Append(LogLine line)
    {
        var p = new Paragraph { Margin = new Thickness(0) };
        p.Inlines.Add(new Run(line.TimeText + "  ") { Foreground = Muted });
        var run = new Run(line.Text)
        {
            Foreground = line.Level switch
            {
                LogLevel.Step => Step,
                LogLevel.Ok => Ok,
                LogLevel.Warning => Warning,
                LogLevel.Error => Error,
                _ => Normal,
            },
        };
        if (line.Level == LogLevel.Step) run.FontWeight = FontWeights.Bold;
        p.Inlines.Add(run);
        Text.Document.Blocks.Add(p);
    }

    /// <summary>Défilement après la mise en page (sinon la dernière ligne n'est pas encore mesurée).</summary>
    private void ScrollToEnd() =>
        Dispatcher.BeginInvoke(DispatcherPriority.Background, Text.ScrollToEnd);

    private void OnCopyAll(object sender, RoutedEventArgs e)
    {
        if (_log is null) return;
        Clipboard.SetText(string.Join(Environment.NewLine, _log.Lines.Select(l => $"{l.TimeText}  {l.Text}")));
    }

    private void OnClear(object sender, RoutedEventArgs e) => _log?.Clear();

    private void OnPopOut(object sender, RoutedEventArgs e)
    {
        if (DataContext is not LogViewController log || log.IsPoppedOut) return;
        log.IsPoppedOut = true;
        var window = new LogWindow { DataContext = log, Owner = Window.GetWindow(this) };
        window.Closed += (_, _) => log.IsPoppedOut = false;
        window.Show();
    }

    private static Brush Frozen(string color)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
        brush.Freeze();
        return brush;
    }
}
