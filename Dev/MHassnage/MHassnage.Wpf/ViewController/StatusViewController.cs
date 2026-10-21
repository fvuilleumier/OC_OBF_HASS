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

    /// <summary>ok, warning, error, loading.</summary>
    [ObservableProperty]
    private string _state = "loading";

    [ObservableProperty]
    private string _headline = "Chargement…";

    public void Reset()
    {
        Kpis.Clear();
        Details.Clear();
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
        foreach (var problem in SettingsService.Validate(settings))
            SettingsProblems.Add(problem);

        if (SettingsProblems.Any(p => p.StartsWith("Hass:Token", StringComparison.Ordinal)))
            TokenHelp = HassTokenHelp.Text(settings.Hass.Url, SettingsService.SettingsPath);
    }

    public ObservableCollection<string> SettingsProblems { get; } = [];

    public DashboardCard Hass { get; } = new("HOME ASSISTANT");
    public DashboardCard PrivateRepo { get; } = new("DÉPÔT PRIVÉ");
    public DashboardCard PublicRepo { get; } = new("DÉPÔT PUBLIC (IA)");
    public DashboardCard Obfuscator { get; } = new("OBFUSCATOR");

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
        foreach (var card in new[] { Hass, PrivateRepo, PublicRepo, Obfuscator }) card.Reset();
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
        try
        {
            await using var client = await HassClient.ConnectAsync(_settings.Hass.Url, _settings.Hass.Token);
            var states = (await client.CommandAsync("get_states"))?.AsArray() ?? [];
            var devices = (await client.CommandAsync("config/device_registry/list"))?.AsArray() ?? [];
            var areas = (await client.CommandAsync("config/area_registry/list"))?.AsArray() ?? [];

            var byDomain = states
                .Select(s => s?["entity_id"]?.GetValue<string>() ?? "")
                .Where(id => id.Contains('.'))
                .GroupBy(id => id[..id.IndexOf('.')])
                .Select(g => (Domain: g.Key, Count: g.Count()))
                .OrderByDescending(g => g.Count)
                .ToList();
            int unavailable = states.Count(s => s?["state"]?.GetValue<string>() is "unavailable");
            int automations = byDomain.FirstOrDefault(d => d.Domain == "automation").Count;

            Hass.Kpis.Add(new Kpi(states.Count.ToString("N0"), "entités"));
            Hass.Kpis.Add(new Kpi(devices.Count.ToString("N0"), "appareils"));
            Hass.Kpis.Add(new Kpi(automations.ToString("N0"), "automations"));
            Hass.Kpis.Add(new Kpi(areas.Count.ToString("N0"), "pièces"));
            Hass.Details.Add($"Version {client.HaVersion} · {_settings.Hass.Url}");
            Hass.Details.Add($"{unavailable} entité(s) indisponible(s)");

            int max = byDomain.Count > 0 ? byDomain[0].Count : 1;
            foreach (var (domain, count) in byDomain.Take(10))
                Domains.Add(new BarItem(domain, count, (double)count / max));

            Hass.State = unavailable > 0 ? "warning" : "ok";
            Hass.Headline = "Connecté";
        }
        catch (HassAuthException)
        {
            Hass.State = "error";
            Hass.Headline = "Jeton refusé ou absent";
            TokenHelp ??= HassTokenHelp.Text(_settings.Hass.Url, SettingsService.SettingsPath);
        }
        catch (Exception ex)
        {
            Fail(Hass, ex);
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

            int target = Int(status, "targetPending");
            int source = Int(status, "sourcePending");
            Obfuscator.Kpis.Add(new Kpi(source.ToString(), "à publier"));
            Obfuscator.Kpis.Add(new Kpi(target.ToString(), "à rapatrier"));
            Obfuscator.Details.Add($"Dernière publication : {Date(status, "lastObfuscation")}");
            Obfuscator.Details.Add($"Dernier rapatriement : {Date(status, "lastReturn")}");

            Obfuscator.State = target > 0 || source > 0 ? "warning" : "ok";
            Obfuscator.Headline = target > 0 ? "Travail de l'IA à récupérer"
                : source > 0 ? "Publication à faire"
                : "Synchronisé";
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
