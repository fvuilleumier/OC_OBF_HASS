using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using MHassnage.Core.Model;
using MHassnage.Core.Service;
using MHassnage.Wpf.Controller;

namespace MHassnage.Wpf.ViewController;

/// <summary>Fenêtre principale : liste des opérations et panel courant.</summary>
public sealed partial class MainViewController : ObservableObject
{
    private readonly NavigationController _navigation;

    public MainViewController(AppSettings settings, NavigationController navigation)
    {
        _navigation = navigation;
        Settings = settings;

        Operations =
        [
            new(Operation.Status, "État", "Dépôts, jeton HA, dernier export"),
            new(Operation.BackupHass, "Sauvegarder HA", "J'ai modifié HA : exporter et commiter"),
            new(Operation.PrepareForAi, "Préparer pour l'IA", "Exporter et publier pseudonymisé"),
            new(Operation.RetrieveAiWork, "Récupérer le travail de l'IA", "Rapatrier vers le dépôt privé"),
            new(Operation.ApplyToHass, "Appliquer dans HA", "Diff, validation, écriture"),
        ];

        foreach (var problem in SettingsService.Validate(settings))
            SettingsProblems.Add(problem);

        if (SettingsProblems.Any(p => p.StartsWith("Hass:Token", StringComparison.Ordinal)))
            TokenHelp = HassTokenHelp.Text(settings.Hass.Url, SettingsService.SettingsPath);

        _selected = Operations[0];
    }

    public AppSettings Settings { get; }
    public IReadOnlyList<OperationItem> Operations { get; }
    public ObservableCollection<string> SettingsProblems { get; } = [];

    /// <summary>Marche à suivre pour recréer le jeton HA ; null si le jeton est présent.</summary>
    [ObservableProperty]
    private string? _tokenHelp;

    [ObservableProperty]
    private OperationItem _selected;

    partial void OnSelectedChanged(OperationItem value) => _navigation.Current = value.Operation;
}

public sealed record OperationItem(Operation Operation, string Title, string Description);
