using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MHassnage.Core.Model;
using MHassnage.Core.Service;

namespace MHassnage.Wpf.ViewController;

/// <summary>Point signalé par l'Obfuscator (déjà masqué / pseudonymisé).</summary>
public sealed record ExchangeIssue(string Kind, string Text, string Detail);

/// <summary>
/// Base des deux échanges avec l'Obfuscator :
///   simulation (--dry-run) → si tout passe, application directe dans MHassnage ;
///   sinon, liste des points et ouverture de la WPF de l'Obfuscator sur le bon dialogue.
/// Le dépôt affiché (diff, commit) est celui qui reçoit le résultat.
/// </summary>
public abstract partial class ExchangeViewController : ObservableObject, IRefreshable
{
    protected ExchangeViewController(AppSettings settings, string repositoryPath)
    {
        Settings = settings;
        Obfuscator = new ObfuscatorService(settings.Obfuscator, Log);
        Repository = new RepositoryViewController(repositoryPath, settings.Git.ExecutablePath, Log);
    }

    protected AppSettings Settings { get; }
    protected ObfuscatorService Obfuscator { get; }

    public LogViewController Log { get; } = new();
    public RepositoryViewController Repository { get; }
    public ObservableCollection<ExchangeIssue> Issues { get; } = [];

    public abstract string Title { get; }
    public abstract string Description { get; }
    public abstract string RunLabel { get; }

    /// <summary>Libellé de l'option exécutée avant la simulation (export, pull).</summary>
    public abstract string PreStepLabel { get; }

    [ObservableProperty]
    private bool _runPreStep = true;

    /// <summary>Vrai quand l'Obfuscator attend une décision : bouton « Ouvrir dans l'Obfuscator ».</summary>
    [ObservableProperty]
    private bool _needsObfuscator;

    [ObservableProperty]
    private string _statusText = "";

    public Task RefreshAsync() => Repository.RefreshAsync();

    protected abstract ObfuscatorDialog Dialog { get; }
    protected abstract Task<bool> PreStepAsync();
    protected abstract Task<ObfuscatorReport> SimulateAsync();
    protected abstract Task<ObfuscatorReport> ApplyAsync();
    protected abstract string CommitMessage { get; }

    /// <summary>Enchaînement complet : étape préalable, simulation, application si tout passe.</summary>
    [RelayCommand]
    private Task RunAsync() => Busy(async () =>
    {
        Log.Clear();
        if (RunPreStep && !await PreStepAsync()) return;
        await CheckAndApplyAsync();
    });

    /// <summary>Après un passage dans l'Obfuscator : nouvelle simulation sans étape préalable.</summary>
    [RelayCommand]
    private Task RecheckAsync() => Busy(CheckAndApplyAsync);

    [RelayCommand]
    private void OpenObfuscator()
    {
        try
        {
            Obfuscator.OpenWpf(Dialog);
            StatusText = "Tranche dans l'Obfuscator, puis clique sur « Revérifier ».";
        }
        catch (Exception ex)
        {
            Log.Error(ex.Message);
        }
    }

    private async Task CheckAndApplyAsync()
    {
        Issues.Clear();
        NeedsObfuscator = false;

        var simulation = await SimulateAsync();
        ShowIssues(simulation);
        if (simulation.Code != ObfuscatorExitCode.Ok)
        {
            NeedsObfuscator = simulation.Code != ObfuscatorExitCode.Error;
            StatusText = NeedsObfuscator
                ? "L'Obfuscator attend une décision : ouvre-le, tranche, puis « Revérifier »."
                : "Erreur de l'Obfuscator : voir le journal.";
            return;
        }

        var result = await ApplyAsync();
        ShowIssues(result);
        if (result.Code != ObfuscatorExitCode.Ok)
        {
            // Rare : l'état a changé entre la simulation et l'application
            NeedsObfuscator = result.Code != ObfuscatorExitCode.Error;
            StatusText = "L'application a été refusée : voir le journal.";
            return;
        }

        StatusText = result.Outcome is { } o
            ? $"Appliqué : {o.Created} créé(s), {o.Modified} modifié(s), {o.Deleted} supprimé(s)"
              + (o.Skipped.Count > 0 ? $", {o.Skipped.Count} ignoré(s)" : "") + ". Vérifie le diff, puis commit."
            : "Rien à appliquer.";
        Repository.SuggestMessage(CommitMessage);
    }

    private void ShowIssues(ObfuscatorReport report)
    {
        Issues.Clear();
        foreach (var p in report.Pending)
            Issues.Add(new ExchangeIssue(p.Kind, p.Value,
                $"{p.Occurrences} occurrence(s) · {string.Join(", ", p.Files.Take(3))}{(p.Files.Count > 3 ? "…" : "")}"));
        foreach (var c in report.Conflicts)
            Issues.Add(new ExchangeIssue(c.Reason, c.Path, c.Count > 0 ? $"{c.Count} conflit(s)" : "choix à faire"));
        foreach (var change in report.Changes)
        {
            if (change.Action is "Blocked" or "Refused")
                Issues.Add(new ExchangeIssue(change.Action, change.Path, "fichier bloqué ou refusé"));
            foreach (var w in change.Warnings.Where(w => w.Serious))
                Issues.Add(new ExchangeIssue(w.Kind, change.Path, $"ligne {w.Line} · {w.Message}"));
        }
        if (!report.Isolation.Safe && report.Isolation.Message.Length > 0)
            Issues.Add(new ExchangeIssue("Isolation", "Dossiers non isolés", report.Isolation.Message));
        foreach (string e in report.Errors)
            Issues.Add(new ExchangeIssue("Erreur", e, ""));
    }

    private async Task Busy(Func<Task> action)
    {
        if (Repository.IsBusy) return;
        Repository.IsBusy = true;
        try
        {
            await action();
        }
        catch (Exception ex)
        {
            Log.Error(ex.Message);
        }
        finally
        {
            Repository.IsBusy = false;
        }
        await Repository.RefreshAsync();
    }
}

/// <summary>
/// « Préparer pour l'IA » : export HA (option) → simulation de publication →
/// publication directe si tout passe → diff et commit du dépôt public.
/// </summary>
public sealed class PrepareForAiViewController(AppSettings settings)
    : ExchangeViewController(settings, settings.Repositories.Public.Path)
{
    public override string Title => "Préparer pour l'IA";
    public override string Description => "Exporter HA, pseudonymiser et publier vers le dépôt public";
    public override string RunLabel => "▶ Préparer";
    public override string PreStepLabel => "Exporter HA d'abord";
    protected override ObfuscatorDialog Dialog => ObfuscatorDialog.Publish;
    protected override string CommitMessage => $"Publication pour l'IA du {DateTime.Now:dd.MM.yyyy HH:mm}";

    protected override Task<bool> PreStepAsync() => HassExportStep.RunAsync(Settings, Log);
    protected override Task<ObfuscatorReport> SimulateAsync() => Task.Run(() => Obfuscator.PublishAsync(dryRun: true));
    protected override Task<ObfuscatorReport> ApplyAsync() => Task.Run(() => Obfuscator.PublishAsync(dryRun: false));
}

/// <summary>
/// « Récupérer le travail de l'IA » : pull du dépôt public (option) → simulation du rapatriement →
/// rapatriement direct si aucun conflit → diff et commit du dépôt privé.
/// </summary>
public sealed class RetrieveAiWorkViewController(AppSettings settings)
    : ExchangeViewController(settings, settings.Repositories.Private.Path)
{
    public override string Title => "Récupérer le travail de l'IA";
    public override string Description => "Rapatrier les modifications de l'IA vers le dépôt privé";
    public override string RunLabel => "▶ Récupérer";
    public override string PreStepLabel => "Pull du dépôt public d'abord";
    protected override ObfuscatorDialog Dialog => ObfuscatorDialog.Return;
    protected override string CommitMessage => $"Travail de l'IA rapatrié le {DateTime.Now:dd.MM.yyyy HH:mm}";

    protected override async Task<bool> PreStepAsync()
    {
        try
        {
            var publicRepo = new GitService(Settings.Repositories.Public.Path, Settings.Git.ExecutablePath, Log);
            await Task.Run(() => publicRepo.PullAsync());
            return true;
        }
        catch (Exception ex)
        {
            Log.Error($"Pull du dépôt public : {ex.Message}");
            return false;
        }
    }

    protected override Task<ObfuscatorReport> SimulateAsync() => Task.Run(() => Obfuscator.ReturnAsync(dryRun: true));
    protected override Task<ObfuscatorReport> ApplyAsync() => Task.Run(() => Obfuscator.ReturnAsync(dryRun: false));
}
