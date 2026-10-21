using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;

namespace MHassnage.Wpf.ViewController;

/// <summary>Niveau d'une ligne de journal, déduit de son préfixe (comme la console).</summary>
public enum LogLevel { Info, Step, Ok, Warning, Error }

public sealed record LogLine(DateTime Time, LogLevel Level, string Text)
{
    public string TimeText => Time.ToString("HH:mm:ss");
}

/// <summary>
/// Journal d'un panel. Reçoit les lignes des services (« OK  … », « ERREUR  … ») depuis
/// n'importe quel thread, et les étapes propres à MHassnage (<see cref="Step"/>).
/// </summary>
public sealed partial class LogViewController : ObservableObject, IProgress<string>
{
    private static readonly (string Prefix, LogLevel Level)[] Prefixes =
    [
        ("OK", LogLevel.Ok),
        ("ABSENT", LogLevel.Warning),
        ("IGNORÉ", LogLevel.Warning),
        ("ERREUR", LogLevel.Error),
    ];

    public ObservableCollection<LogLine> Lines { get; } = [];

    /// <summary>Vrai quand le journal est affiché dans sa propre fenêtre.</summary>
    [ObservableProperty]
    private bool _isPoppedOut;

    public void Report(string value)
    {
        var level = LogLevel.Info;
        string text = value;
        foreach (var (prefix, l) in Prefixes)
        {
            if (!value.StartsWith(prefix + " ", StringComparison.Ordinal)) continue;
            level = l;
            text = $"[{prefix}] {value[prefix.Length..].TrimStart()}";
            break;
        }
        Add(level, text);
    }

    /// <summary>Étape de MHassnage (« ▶ Export de Home Assistant »).</summary>
    public void Step(string text) => Add(LogLevel.Step, $"▶ {text}");

    public void Error(string text) => Add(LogLevel.Error, $"[ERREUR] {text}");
    public void Success(string text) => Add(LogLevel.Ok, $"[OK] {text}");

    public void Clear() => Dispatch(Lines.Clear);

    private void Add(LogLevel level, string text) => Dispatch(() => Lines.Add(new LogLine(DateTime.Now, level, text)));

    private static void Dispatch(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess()) action();
        else dispatcher.Invoke(action);
    }
}
