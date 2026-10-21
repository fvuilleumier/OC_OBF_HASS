using System.Collections.ObjectModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MHassnage.Core.Model;
using MHassnage.Core.Model.OwnException;
using MHassnage.Core.Service;

namespace MHassnage.Wpf.ViewController;

/// <summary>Indicateur chiffré d'une carte (« 1 245 entités »).</summary>
public sealed record Kpi(string Value, string Label);

/// <summary>Barre horizontale du graphique (Ratio entre 0 et 1).</summary>
public sealed record BarItem(string Label, int Value, double Ratio);

/// <summary>Carte du tableau de bord : titre, état (couleur), indicateurs, lignes de détail.</summary>
public sealed partial class DashboardCard(string title) : ObservableObject
{
    public string Title { get; } = title;
    public ObservableCollection<Kpi> Kpis { get; } = [];
    public ObservableCollection<string> Details { get; } = [];

    /// <summary>Points à vérifier (rouge si Serious).</summary>
    public ObservableCollection<HealthItem> Items { get; } = [];

    /// <summary>Texte quand la liste est vide (« Aucune mise à jour »).</summary>
    [ObservableProperty]
    private string _emptyText = "";

    public void Fill(IReadOnlyCollection<HealthItem> items, string empty, int max = 8)
    {
        foreach (var item in items.OrderByDescending(i => i.Serious).Take(max)) Items.Add(item);
        if (items.Count > max) Items.Add(new HealthItem($"… et {items.Count - max} autre(s)"));
        EmptyText = items.Count == 0 ? empty : "";
        State = items.Count == 0 ? "ok" : items.Any(i => i.Serious) ? "error" : "warning";
        Headline = items.Count == 0 ? "RAS" : $"{items.Count} à vérifier";
    }

    /// <summary>ok, warning, error, loading.</summary>
    [ObservableProperty]
    private string _state = "loading";

    [ObservableProperty]
    private string _headline = "Chargement…";

    public void Reset()
    {
        Kpis.Clear();
        Details.Clear();
        Items.Clear();
        EmptyText = "";
        State = "loading";
        Headline = "Chargement…";
    }
}

/// <summary>Accueil « État » : vue d'ensemble façon Power BI (dépôts, HA, Obfuscator, réglages).</summary>
public sealed partial class StatusViewController : ObservableObject, IRefreshable
{
    private readonly AppSettings _settings;

    public StatusViewController(AppSettings settings)
    {
        _settings = settings;
        Batteries = new($"BATTERIES < {settings.Health.BatteryWarningPercent} %");
        foreach (var problem in SettingsService.Validate(settings))
            SettingsProblems.Add(problem);

        if (SettingsProblems.Any(p => p.StartsWith("Hass:Token", StringComparison.Ordinal)))
            TokenHelp = HassTokenHelp.Text(settings.Hass.Url, SettingsService.SettingsPath);
    }

    public ObservableCollection<string> SettingsProblems { get; } = [];

    public DashboardCard Hass { get; } = new("HOME ASSISTANT");
    public DashboardCard PrivateRepo { get; } = new("DÉPÔT PRIVÉ");
    public DashboardCard PublicRepo { get; } = new("DÉPÔT PUBLIC (IA)");
    public DashboardCard Obfuscator { get; } = new("OBFUSCATOR (ÉCHANGES AVEC L'IA)");

    public DashboardCard Updates { get; } = new("MISES À JOUR");
    public DashboardCard Repairs { get; } = new("RÉPARATIONS ET NOTIFICATIONS");
    public DashboardCard Security { get; } = new("SÉCURITÉ");
    public DashboardCard Backups { get; } = new("SAUVEGARDES HA");
    public DashboardCard Batteries { get; }
    public DashboardCard Unavailable { get; } = new("ENTITÉS INDISPONIBLES");

    private IEnumerable<DashboardCard> AllCards =>
        [Hass, PrivateRepo, PublicRepo, Obfuscator, Updates, Repairs, Security, Backups, Batteries, Unavailable];

    /// <summary>Entités par domaine (10 premiers).</summary>
    public ObservableCollection<BarItem> Domains { get; } = [];

    /// <summary>Marche à suivre pour recréer le jeton HA ; null si le jeton est présent.</summary>
    [ObservableProperty]
    private string? _tokenHelp;

    [ObservableProperty]
    private string _updatedText = "";

    [RelayCommand]
    public async Task RefreshAsync()
    {
        foreach (var card in AllCards) card?.Reset();
        Domains.Clear();

        await Task.WhenAll(
            LoadRepoAsync(PrivateRepo, _settings.Repositories.Private),
            LoadRepoAsync(PublicRepo, _settings.Repositories.Public),
            LoadHassAsync(),
            LoadObfuscatorAsync());

        UpdatedText = $"Mis à jour à {DateTime.Now:HH:mm:ss}";
    }

    private async Task LoadRepoAsync(DashboardCard card, RepositorySettings repo)
    {
        try
        {
            var git = new GitService(repo.Path, _settings.Git.ExecutablePath);
            var state = await Task.Run(() => git.GetStateAsync());
            var last = await Task.Run(() => git.LastCommitAsync());
            int changes = state.Changes.Count(c => !Exclusions.IsExcluded(c.Path, repo.Excluded));

            card.Kpis.Add(new Kpi(changes.ToString(), "modifié(s)"));
            card.Kpis.Add(new Kpi($"↑{state.Ahead} ↓{state.Behind}", "distant"));
            card.Details.Add($"{state.Branch} · {state.SyncText}");
            if (last is { } l) card.Details.Add($"Dernier commit {Ago(l.Date)} : {l.Subject}");
            card.Details.Add(state.Root);

            card.State = state.Behind > 0 || state.Ahead > 0 || changes > 0 ? "warning" : "ok";
            card.Headline = changes > 0 ? "Modifications non commitées"
                : state.Ahead > 0 ? "Commits à pousser"
                : state.Behind > 0 ? "Commits à récupérer"
                : "À jour";
        }
        catch (Exception ex)
        {
            Fail(card, ex);
        }
    }

    private async Task LoadHassAsync()
    {
        var hassCards = new[] { Updates, Repairs, Security, Backups, Batteries, Unavailable };
        try
        {
            var h = await Task.Run(() => new HassHealthService(_settings).LoadAsync());

            Hass.Kpis.Add(new Kpi(h.Entities.ToString("N0"), "entités"));
            Hass.Kpis.Add(new Kpi(h.Devices.ToString("N0"), "appareils"));
            Hass.Kpis.Add(new Kpi(h.Automations.ToString("N0"), "automations"));
            Hass.Kpis.Add(new Kpi(h.Areas.ToString("N0"), "pièces"));
            Hass.Details.Add($"Version {h.Version} · {_settings.Hass.Url}");
            if (h.Skipped.Count > 0) Hass.Details.Add($"Non disponible sur cette version : {string.Join(", ", h.Skipped)}");
            Hass.State = "ok";
            Hass.Headline = "Connecté";

            int max = h.Domains.Count > 0 ? h.Domains[0].Count : 1;
            foreach (var (domain, count) in h.Domains.Take(10))
                Domains.Add(new BarItem(domain, count, (double)count / max));

            Updates.Fill(h.Updates, "Tout est à jour");
            Repairs.Fill([.. h.Repairs, .. h.Notifications.Select(n => n with { Detail = "notification" })], "Aucun problème signalé");
            Batteries.Fill(h.LowBatteries, "Toutes les batteries sont correctes");
            Unavailable.Fill(h.Unavailable, "Toutes les entités répondent");

            Security.Fill(h.Security.Where(s => s.Serious).ToList(), "Aucun point faible détecté");
            foreach (var ok in h.Security.Where(s => !s.Serious)) Security.Details.Add($"✓ {ok.Text} — {ok.Detail}");

            // Sauvegardes : ancienneté de la dernière
            var age = h.LastBackup is { } last ? DateTimeOffset.Now - last : (TimeSpan?)null;
            Backups.Kpis.Add(new Kpi(h.BackupCount.ToString(), "sauvegarde(s)"));
            Backups.Kpis.Add(new Kpi(age is { } a ? $"{(int)a.TotalDays} j" : "—", "depuis la dernière"));
            Backups.Details.Add(h.LastBackup is { } d ? $"Dernière : {d.LocalDateTime:dd.MM.yyyy HH:mm}" : "Aucune sauvegarde trouvée");
            Backups.State = age is null ? "error" : age.Value.TotalDays > _settings.Health.BackupMaxAgeDays ? "warning" : "ok";
            Backups.Headline = age is null ? "Aucune sauvegarde"
                : age.Value.TotalDays > _settings.Health.BackupMaxAgeDays ? $"Plus de {_settings.Health.BackupMaxAgeDays} jours" : "Récente";
        }
        catch (HassAuthException)
        {
            Hass.State = "error";
            Hass.Headline = "Jeton refusé ou absent";
            TokenHelp ??= HassTokenHelp.Text(_settings.Hass.Url, SettingsService.SettingsPath);
            foreach (var card in hassCards.Where(c => c.State == "loading")) { card.State = "error"; card.Headline = "HA inaccessible"; }
        }
        catch (Exception ex)
        {
            Fail(Hass, ex);
            foreach (var card in hassCards.Where(c => c.State == "loading")) { card.State = "error"; card.Headline = "HA inaccessible"; }
        }
    }

    private async Task LoadObfuscatorAsync()
    {
        try
        {
            var report = await Task.Run(() => new ObfuscatorService(_settings.Obfuscator).StatusAsync());
            if (report.Code == ObfuscatorExitCode.Error || report.Status is not { } status)
            {
                Obfuscator.State = "error";
                Obfuscator.Headline = report.Errors.FirstOrDefault() ?? "Statut indisponible";
                return;
            }

            // sourcePending : fichiers du dépôt privé modifiés depuis la dernière publication
            // targetPending : fichiers modifiés par l'IA dans le dépôt public, pas encore rapatriés
            int source = Int(status, "sourcePending");
            int target = Int(status, "targetPending");
            Obfuscator.Kpis.Add(new Kpi(source.ToString(), "privé → IA\nchangé(s) chez toi, pas encore publié(s)"));
            Obfuscator.Kpis.Add(new Kpi(target.ToString(), "IA → privé\nmodifié(s) par l'IA, pas encore rapatrié(s)"));
            Obfuscator.Details.Add($"Dernière publication vers l'IA : {Date(status, "lastObfuscation")}");
            Obfuscator.Details.Add($"Dernier rapatriement depuis l'IA : {Date(status, "lastReturn")}");

            Obfuscator.State = target > 0 || source > 0 ? "warning" : "ok";
            Obfuscator.Headline = target > 0 ? "Travail de l'IA à récupérer (« Récupérer le travail de l'IA »)"
                : source > 0 ? "L'IA voit une version dépassée (« Préparer pour l'IA »)"
                : "L'IA et le dépôt privé sont alignés";
        }
        catch (Exception ex)
        {
            Fail(Obfuscator, ex);
        }
    }

    private static int Int(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : 0;

    private static string Date(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String && DateTimeOffset.TryParse(v.GetString(), out var d)
            ? Ago(d)
            : "jamais";

    private static string Ago(DateTimeOffset date)
    {
        var span = DateTimeOffset.Now - date;
        return span.TotalMinutes < 1 ? "à l'instant"
            : span.TotalHours < 1 ? $"il y a {(int)span.TotalMinutes} min"
            : span.TotalDays < 1 ? $"il y a {(int)span.TotalHours} h"
            : span.TotalDays < 30 ? $"il y a {(int)span.TotalDays} j"
            : date.ToString("dd.MM.yyyy");
    }

    private static void Fail(DashboardCard card, Exception ex)
    {
        card.State = "error";
        card.Headline = ex.Message.Split('\n')[0];
    }
}

/// <summary>Opération pas encore implémentée.</summary>
public sealed record PlaceholderViewController(string Title, string Description);
