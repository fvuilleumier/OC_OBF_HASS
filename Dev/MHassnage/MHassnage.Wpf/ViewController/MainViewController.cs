using CommunityToolkit.Mvvm.ComponentModel;
using MHassnage.Core.Model;
using MHassnage.Wpf.Controller;

namespace MHassnage.Wpf.ViewController;

/// <summary>Fenêtre principale : liste des opérations et panel de l'opération choisie.</summary>
public sealed partial class MainViewController : ObservableObject
{
    private readonly NavigationController _navigation;
    private readonly AppSettings _settings;

    /// <summary>Un panel par opération, créé à la première ouverture puis conservé.</summary>
    private readonly Dictionary<Operation, object> _panels = [];

    public MainViewController(AppSettings settings, NavigationController navigation)
    {
        _navigation = navigation;
        _settings = settings;

        Operations =
        [
            new(Operation.Status, "État", "Dépôts, jeton HA, dernier export"),
            new(Operation.BackupHass, "Sauvegarder HA", "J'ai modifié HA : exporter et commiter"),
            new(Operation.PrepareForAi, "Préparer pour l'IA", "Exporter et publier pseudonymisé"),
            new(Operation.RetrieveAiWork, "Récupérer le travail de l'IA", "Rapatrier vers le dépôt privé"),
            new(Operation.ApplyToHass, "Appliquer dans HA", "Diff, validation, écriture"),
        ];

        _selected = Operations[0];
        _currentPanel = PanelFor(_selected);
        if (_currentPanel is IRefreshable refreshable) _ = refreshable.RefreshAsync();
    }

    public IReadOnlyList<OperationItem> Operations { get; }

    /// <summary>« v.1.0.0.0 » : version du projet MHassnage.Core.</summary>
    public string VersionText { get; } = $"v.{typeof(AppSettings).Assembly.GetName().Version}";

    public string WindowTitle => $"MHassnage {VersionText}";

    [ObservableProperty]
    private OperationItem _selected;

    /// <summary>ViewController du panel affiché ; la vue est choisie par DataTemplate.</summary>
    [ObservableProperty]
    private object _currentPanel;

    partial void OnSelectedChanged(OperationItem value)
    {
        _navigation.Current = value.Operation;
        bool firstOpening = !_panels.ContainsKey(value.Operation);
        CurrentPanel = PanelFor(value);

        if (firstOpening && CurrentPanel is IRefreshable refreshable)
            _ = refreshable.RefreshAsync();
    }

    private object PanelFor(OperationItem item)
    {
        if (_panels.TryGetValue(item.Operation, out var panel)) return panel;

        panel = item.Operation switch
        {
            Operation.Status => new StatusViewController(_settings),
            Operation.BackupHass => new BackupViewController(_settings),
            Operation.PrepareForAi => new PrepareForAiViewController(_settings),
            Operation.RetrieveAiWork => new RetrieveAiWorkViewController(_settings),
            Operation.ApplyToHass => new ApplyViewController(_settings),
            _ => new PlaceholderViewController(item.Title, "Prévu dans une prochaine étape."),
        };
        _panels[item.Operation] = panel;
        return panel;
    }
}

public sealed record OperationItem(Operation Operation, string Title, string Description);
