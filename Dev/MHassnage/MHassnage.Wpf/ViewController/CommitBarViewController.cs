using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MHassnage.Core.Model;
using MHassnage.Core.Service;

namespace MHassnage.Wpf.ViewController;

/// <summary>Dépôt visé par la barre de commit (case à cocher).</summary>
public sealed partial class CommitTarget(string name, GitService git, Func<Task<IReadOnlyCollection<CommitEntry>>> entries)
    : ObservableObject
{
    public string Name { get; } = name;
    public GitService Git { get; } = git;

    /// <summary>Fichiers à commiter dans ce dépôt.</summary>
    public Func<Task<IReadOnlyCollection<CommitEntry>>> Entries { get; } = entries;

    [ObservableProperty]
    private bool _isChecked = true;

    /// <summary>Faux si le dossier n'est pas un dépôt git (case grisée).</summary>
    [ObservableProperty]
    private bool _isAvailable = true;

    [ObservableProperty]
    private string _toolTip = "";

    /// <summary>Toutes les modifications non exclues du dépôt (dépôts annexes, sans sélection fine).</summary>
    public static Func<Task<IReadOnlyCollection<CommitEntry>>> AllChanges(GitService git, IReadOnlyList<string> excluded) =>
        async () =>
        {
            var state = await git.GetStateAsync();
            return state.Changes
                .Where(c => !Exclusions.IsExcluded(c.Path, excluded))
                .Select(c => new CommitEntry(c))
                .ToList();
        };
}

/// <summary>
/// Barre de commit réutilisable : message, Pull, Commit, Push, Commit &amp; Push.
/// S'applique à chaque dépôt coché ; « rien à commiter » ou « rien à pousser » n'est pas une erreur.
/// </summary>
public sealed partial class CommitBarViewController : ObservableObject
{
    private readonly IRepositoryPanel _panel;

    public CommitBarViewController(IRepositoryPanel panel, string name)
    {
        _panel = panel;
        Targets.Add(new CommitTarget(name, panel.Git, () => Task.FromResult(panel.SelectedEntries))
        {
            ToolTip = "Fichiers et blocs cochés dans la liste ci-dessus",
        });
    }

    /// <summary>Dépôts concernés ; les cases ne sont affichées que s'il y en a plusieurs.</summary>
    public ObservableCollection<CommitTarget> Targets { get; } = [];

    public bool ShowTargets => Targets.Count > 1;

    /// <summary>Rafraîchissement après une action (tous les onglets) ; null = le dépôt principal seul.</summary>
    public Func<Task>? RefreshAll { get; set; }

    /// <summary>Exécuté après chaque Commit / Push / Commit &amp; Push (sauvegarde annexe).</summary>
    public Func<Task>? AfterAction { get; set; }

    [ObservableProperty]
    private string _message = "";

    /// <summary>Ajoute un dépôt affiché dans un autre onglet : ses fichiers et blocs cochés sont commités.</summary>
    public void AddTarget(RepositoryViewController repository)
    {
        var target = new CommitTarget(repository.Name, repository.Git, () => Task.FromResult(repository.SelectedEntries))
        {
            ToolTip = $"{repository.Git.RepositoryPath} : fichiers et blocs cochés dans son onglet",
        };
        Targets.Add(target);
        OnPropertyChanged(nameof(ShowTargets));
        _ = CheckAvailableAsync(target);
    }

    /// <summary>Ajoute un dépôt annexe (toutes ses modifications non exclues sont commitées).</summary>
    public void AddTarget(string name, GitService git, IReadOnlyList<string> excluded)
    {
        var target = new CommitTarget(name, git, CommitTarget.AllChanges(git, excluded))
        {
            ToolTip = $"{git.RepositoryPath} : toutes les modifications non exclues",
        };
        Targets.Add(target);
        OnPropertyChanged(nameof(ShowTargets));
        _ = CheckAvailableAsync(target);
    }

    private static async Task CheckAvailableAsync(CommitTarget target)
    {
        bool ok = System.IO.Directory.Exists(target.Git.RepositoryPath)
                  && (await Task.Run(() => target.Git.RunAsync(["rev-parse", "--is-inside-work-tree"]))).Success;
        target.IsAvailable = ok;
        if (!ok)
        {
            target.IsChecked = false;
            target.ToolTip = $"{target.Git.RepositoryPath} n'est pas un dépôt git";
        }
    }

    private IEnumerable<CommitTarget> Selected => Targets.Where(t => t.IsChecked && t.IsAvailable);

    /// <summary>Pull en avance rapide uniquement : jamais de merge implicite.</summary>
    [RelayCommand]
    private Task PullAsync() => RunAsync(async () =>
    {
        foreach (var t in Selected)
        {
            _panel.Log.Step($"Pull de {t.Name} (avance rapide)");
            await t.Git.PullAsync();
        }
    });

    [RelayCommand]
    private Task CommitAsync() => RunAsync(async () => { await CommitAll(); await After(); });

    [RelayCommand]
    private Task PushAsync() => RunAsync(async () => { await PushAll(); await After(); });

    [RelayCommand]
    private Task CommitAndPushAsync() => RunAsync(async () =>
    {
        await CommitAll();
        await PushAll();
        await After();
    });

    private Task After() => AfterAction?.Invoke() ?? Task.CompletedTask;

    private async Task CommitAll()
    {
        // D'abord tout lire : on refuse un message vide avant d'avoir commité quoi que ce soit
        var work = new List<(CommitTarget Target, IReadOnlyCollection<CommitEntry> Entries)>();
        foreach (var t in Selected)
            work.Add((t, await t.Entries()));

        if (work.Any(w => w.Entries.Count > 0) && string.IsNullOrWhiteSpace(Message))
            throw new InvalidOperationException("Message de commit vide.");

        foreach (var (target, entries) in work)
        {
            if (entries.Count == 0)
            {
                _panel.Log.Report($"Rien à commiter dans {target.Name} : tout est déjà commité.");
                continue;
            }
            _panel.Log.Step($"Commit de {entries.Count} fichier(s) dans {target.Name}");
            await target.Git.CommitAsync(entries, Message.Trim());
        }
        if (work.Any(w => w.Entries.Count > 0)) Message = "";
    }

    private async Task PushAll()
    {
        foreach (var t in Selected)
        {
            var state = await t.Git.GetStateAsync();
            if (state.Upstream is not null && state.Ahead == 0)
            {
                _panel.Log.Report($"Rien à pousser pour {t.Name} : déjà à jour avec {state.Upstream}.");
                continue;
            }
            _panel.Log.Step($"Push de {t.Name}");
            await t.Git.PushAsync();
        }
    }

    private async Task RunAsync(Func<Task> action)
    {
        if (_panel.IsBusy) return;
        _panel.IsBusy = true;
        try
        {
            await action();
        }
        catch (Exception ex)
        {
            _panel.Log.Error(ex.Message);
        }
        finally
        {
            _panel.IsBusy = false;
        }
        await (RefreshAll?.Invoke() ?? _panel.RefreshAsync());
    }
}
