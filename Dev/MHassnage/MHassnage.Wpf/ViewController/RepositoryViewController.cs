using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MHassnage.Core.Model;
using MHassnage.Core.Service;

namespace MHassnage.Wpf.ViewController;

/// <summary>
/// Vue « à la GitKraken » d'un dépôt, réutilisée par les panels : fichiers modifiés,
/// diff avec sélection des blocs, barre Pull / Commit / Push / Commit &amp; Push.
/// </summary>
public sealed partial class RepositoryViewController : ObservableObject, IRepositoryPanel
{
    private RepoState? _state;
    private CancellationTokenSource? _diffLoading;

    private readonly AppSettings _settings;
    private readonly string _repositoryKey;

    /// <param name="repositoryKey">« Private » ou « Public » (section de Repositories).</param>
    public RepositoryViewController(AppSettings settings, string repositoryKey, LogViewController log)
    {
        _settings = settings;
        _repositoryKey = repositoryKey;
        Log = log;
        Git = new GitService(Settings.Path, settings.Git.ExecutablePath, log);
        CommitBar = new CommitBarViewController(this);
    }

    private RepositorySettings Settings =>
        _repositoryKey == "Public" ? _settings.Repositories.Public : _settings.Repositories.Private;

    public string Name => _repositoryKey == "Public" ? "dépôt public" : "dépôt privé";

    public GitService Git { get; }

    /// <summary>Fichiers modifiés masqués par les exclusions.</summary>
    [ObservableProperty]
    private int _excludedCount;

    /// <summary>Exclut le fichier sélectionné (enregistré dans la configuration).</summary>
    [RelayCommand]
    private Task ExcludeFileAsync(ChangeItem? item) => ExcludeAsync(item?.Change.Path);

    /// <summary>Exclut le dossier du fichier sélectionné.</summary>
    [RelayCommand]
    private Task ExcludeFolderAsync(ChangeItem? item)
    {
        string? path = item?.Change.Path;
        int slash = path?.LastIndexOf('/') ?? -1;
        return ExcludeAsync(slash > 0 ? path![..(slash + 1)] : null);
    }

    private async Task ExcludeAsync(string? rule)
    {
        if (rule is null) return;
        try
        {
            SettingsService.AddExclusion(_settings, _repositoryKey, rule);
            Log.Step($"Exclusion ajoutée au {Name} : {rule}");
        }
        catch (Exception ex)
        {
            Log.Error($"Exclusion non enregistrée : {ex.Message}");
        }
        await RefreshAsync();
    }
    public LogViewController Log { get; }
    public CommitBarViewController CommitBar { get; }
    public ObservableCollection<ChangeItem> Changes { get; } = [];

    public IReadOnlyCollection<CommitEntry> SelectedEntries =>
        Changes.Select(c => c.ToCommitEntry()).OfType<CommitEntry>().ToList();

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string _repoText = "";

    [ObservableProperty]
    private ChangeItem? _selectedChange;

    [ObservableProperty]
    private DiffViewController? _diff;

    /// <summary>Faux : seuls les changements et 3 lignes de contexte sont affichés.</summary>
    [ObservableProperty]
    private bool _showWholeFile;

    partial void OnSelectedChangeChanged(ChangeItem? value) => _ = LoadDiffAsync();
    partial void OnShowWholeFileChanged(bool value) => _ = LoadDiffAsync();

    /// <summary>Préremplit le message de commit s'il est vide.</summary>
    public void SuggestMessage(string message)
    {
        if (string.IsNullOrWhiteSpace(CommitBar.Message)) CommitBar.Message = message;
    }

    [RelayCommand]
    private void CheckAll() => SetAllChecked(true);

    [RelayCommand]
    private void UncheckAll() => SetAllChecked(false);

    private void SetAllChecked(bool value)
    {
        foreach (var change in Changes) change.IsChecked = value;
    }

    [RelayCommand]
    public async Task RefreshAsync()
    {
        try
        {
            string? selected = SelectedChange?.Change.Path;
            Log.Step($"Lecture du {Name}");
            _state = await Task.Run(() => Git.GetStateAsync());

            var visible = _state.Changes.Where(c => !Exclusions.IsExcluded(c.Path, Settings.Excluded)).ToList();
            ExcludedCount = _state.Changes.Count - visible.Count;
            RepoText = $"{_state.Root}  ·  {_state.Branch}  ·  {_state.SyncText}  ·  {visible.Count} fichier(s) modifié(s)"
                       + (ExcludedCount > 0 ? $" (+{ExcludedCount} exclu(s))" : "");

            Changes.Clear();
            foreach (var change in visible) Changes.Add(new ChangeItem(change));
            SelectedChange = Changes.FirstOrDefault(c => c.Change.Path == selected) ?? Changes.FirstOrDefault();
            if (SelectedChange is null) Diff = null;
        }
        catch (Exception ex)
        {
            RepoText = Git.RepositoryPath;
            Log.Error(ex.Message);
        }
    }

    private async Task LoadDiffAsync()
    {
        _diffLoading?.Cancel();
        var cts = _diffLoading = new CancellationTokenSource();

        if (SelectedChange?.Change is not { } change || _state is null)
        {
            Diff = null;
            return;
        }

        try
        {
            string root = _state.Root;
            int context = ShowWholeFile ? -1 : 3;
            var diff = await Task.Run(async () =>
            {
                string before = change.Kind is ChangeKind.Untracked or ChangeKind.Added
                    ? ""
                    : await Git.ReadAtHeadAsync(change.OldPath ?? change.Path, cts.Token);
                string after = GitService.ReadWorking(root, change.Path);
                return DiffService.Compare(change.Path, before, after, context);
            }, cts.Token);

            if (!cts.IsCancellationRequested) Diff = new DiffViewController(diff, SelectedChange);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Log.Error($"Diff de {change.Path} : {ex.Message}");
        }
    }
}
