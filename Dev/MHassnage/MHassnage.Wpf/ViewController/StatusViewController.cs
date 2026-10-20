using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using MHassnage.Core.Model;
using MHassnage.Core.Service;

namespace MHassnage.Wpf.ViewController;

/// <summary>Accueil « État » : réglages à compléter, aide du jeton (état des dépôts : étape 8).</summary>
public sealed partial class StatusViewController : ObservableObject
{
    public StatusViewController(AppSettings settings)
    {
        foreach (var problem in SettingsService.Validate(settings))
            SettingsProblems.Add(problem);

        if (SettingsProblems.Any(p => p.StartsWith("Hass:Token", StringComparison.Ordinal)))
            TokenHelp = HassTokenHelp.Text(settings.Hass.Url, SettingsService.SettingsPath);
    }

    public ObservableCollection<string> SettingsProblems { get; } = [];

    /// <summary>Marche à suivre pour recréer le jeton HA ; null si le jeton est présent.</summary>
    [ObservableProperty]
    private string? _tokenHelp;
}

/// <summary>Opération pas encore implémentée.</summary>
public sealed record PlaceholderViewController(string Title, string Description);
