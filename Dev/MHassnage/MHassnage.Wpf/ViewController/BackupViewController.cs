using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MHassnage.Core.Model;
using MHassnage.Core.Model.OwnException;
using MHassnage.Core.Service;

namespace MHassnage.Wpf.ViewController;

/// <summary>Fichier modifié dans la liste, avec sa case à cocher pour le commit.</summary>
public sealed partial class ChangeItem(FileChange change) : ObservableObject
{
    public FileChange Change { get; } = change;

    [ObservableProperty]
    private bool _isChecked = true;
}

/// <summary>
/// Opération « Sauvegarder HA » : export de HA dans le dépôt privé, revue du diff,
/// puis Commit / Push / Commit &amp; Push.
/// </summary>
public sealed partial class BackupViewController : ObservableObject, IRepositoryPanel
{
    private readonly AppSettings _settings;
    private RepoState? _state;
    private CancellationTokenSource? _diffLoading;

    public BackupViewController(AppSettings settings)
    {
        _settings = settings;
        Git = new GitService(settings.Repositories.Private.Path, settings.Git.ExecutablePath, Log);
        CommitBar = new CommitBarViewController(this);
    }

    public GitService Git { get; }
    public LogViewController Log { get; } = new();
    public CommitBarViewController CommitBar { get; }
    public ObservableCollection<ChangeItem> Changes { get; } = [];

    public IReadOnlyCollection<FileChange> SelectedFiles =>
        Changes.Where(c => c.IsChecked).Select(c => c.Change).ToList();

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string _repoText = "";

    [ObservableProperty]
    private ChangeItem? _selectedChange;

    [ObservableProperty]
    private FileDiff? _diff;

    /// <summary>Faux : seuls les changements et 3 lignes de contexte sont affichés.</summary>
    [ObservableProperty]
    private bool _showWholeFile;

    partial void OnSelectedChangeChanged(ChangeItem? value) => _ = LoadDiffAsync();
    partial void OnShowWholeFileChanged(bool value) => _ = LoadDiffAsync();

    [RelayCommand]
    private void CheckAll() => SetAllChecked(true);

    [RelayCommand]
    private void UncheckAll() => SetAllChecked(false);

    private void SetAllChecked(bool value)
    {
        foreach (var change in Changes) change.IsChecked = value;
    }

    /// <summary>Export de HA dans le dépôt privé, puis relecture des modifications.</summary>
    [RelayCommand]
    private async Task ExportAsync()
    {
        if (IsBusy) return;
        IsBusy = true;
        try
        {
            Log.Clear();
            await Task.Run(() => new HassExportService(_settings, Log).ExportAsync());
            Log.Success("Export terminé.");
            if (string.IsNullOrWhiteSpace(CommitBar.Message))
                CommitBar.Message = $"Sauvegarde HA du {DateTime.Now:dd.MM.yyyy HH:mm}";
        }
        catch (HassAuthException ex)
        {
            Log.Error($"Jeton refusé par Home Assistant : {ex.Message}");
            foreach (string line in HassTokenHelp.Text(_settings.Hass.Url, SettingsService.SettingsPath).Split('\n'))
                Log.Report(line.TrimEnd());
        }
        catch (Exception ex)
        {
            Log.Error(ex.Message);
        }
        finally
        {
            IsBusy = false;
        }
        await RefreshAsync();
    }

    [RelayCommand]
    public async Task RefreshAsync()
    {
        try
        {
            string? selected = SelectedChange?.Change.Path;
            _state = await Task.Run(() => Git.GetStateAsync());

            RepoText = $"{_state.Root}  ·  {_state.Branch}  ·  {_state.SyncText}  ·  {_state.Changes.Count} fichier(s) modifié(s)";

            Changes.Clear();
            foreach (var change in _state.Changes) Changes.Add(new ChangeItem(change));
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

            if (!cts.IsCancellationRequested) Diff = diff;
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Log.Error($"Diff de {change.Path} : {ex.Message}");
        }
    }
}
