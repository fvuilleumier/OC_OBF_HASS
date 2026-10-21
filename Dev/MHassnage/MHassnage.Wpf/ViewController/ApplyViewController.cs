using System.IO;
using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MHassnage.Core.Model;
using MHassnage.Core.Model.OwnException;
using MHassnage.Core.Service;

namespace MHassnage.Wpf.ViewController;

/// <summary>Ligne de la liste « à appliquer ».</summary>
public sealed partial class ApplyItemViewModel : ObservableObject
{
    private readonly Action<ApplyItemViewModel> _nameChanged;

    public ApplyItemViewModel(ApplyItem item, Action<ApplyItemViewModel> nameChanged)
    {
        Item = item;
        _nameChanged = nameChanged;
        _haName = item.Target.Key;
        _isChecked = !item.ChangedInHa;
    }

    public ApplyItem Item { get; }

    [ObservableProperty]
    private bool _isChecked;

    /// <summary>Nom du fichier dans HA (modifiable pour un nouveau fichier : « Helpers.yaml » → « helper.yaml »).</summary>
    [ObservableProperty]
    private string _haName;

    partial void OnHaNameChanged(string value) => _nameChanged(this);

    public string Letter => Item.Status switch { ApplyStatus.New => "A", ApplyStatus.Modified => "M", _ => " " };

    public string Text => Item.Target.Kind == ApplyKind.Dashboard ? $"{Item.RepoPath}  →  {Item.Target}" : Item.RepoPath;

    public string Warning => Item.ChangedInHa
        ? "⚠ Modifié dans HA depuis le dernier commit : l'appliquer écrasera ces changements (décoché par défaut)."
        : Item.IsUnmapped ? "Nouveau fichier : vérifie son nom dans HA (casse comprise)." : "";

    public void Refresh()
    {
        OnPropertyChanged(nameof(Letter));
        OnPropertyChanged(nameof(Warning));
    }
}

/// <summary>
/// « Appliquer dans HA » : diff HA actuel ↔ dépôt privé, sélection, écriture avec sauvegarde
/// préalable, vérification de la configuration, redémarrage, export de contrôle.
/// « Restaurer la version précédente » remet HA dans l'état d'une sauvegarde.
/// </summary>
public sealed partial class ApplyViewController : ObservableObject, IRefreshable
{
    private readonly AppSettings _settings;
    private readonly HassApplyService _service;

    /// <summary>Noms côté HA choisis pour les nouveaux fichiers (conservés entre deux analyses).</summary>
    private readonly Dictionary<string, string> _unmappedNames = new(StringComparer.OrdinalIgnoreCase);

    public ApplyViewController(AppSettings settings)
    {
        _settings = settings;
        _service = new HassApplyService(settings, Log);
    }

    public LogViewController Log { get; } = new();
    public ObservableCollection<ApplyItemViewModel> Items { get; } = [];
    public ObservableCollection<ApplyBackup> Backups { get; } = [];

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private ApplyItemViewModel? _selectedItem;

    [ObservableProperty]
    private DiffViewController? _diff;

    [ObservableProperty]
    private ApplyBackup? _selectedBackup;

    [ObservableProperty]
    private string _statusText = "Clique sur « Analyser » pour comparer HA au dépôt privé.";

    /// <summary>Faux : seuls les changements et 3 lignes de contexte sont affichés.</summary>
    [ObservableProperty]
    private bool _showWholeFile;

    partial void OnSelectedItemChanged(ApplyItemViewModel? value) => ShowDiff();
    partial void OnShowWholeFileChanged(bool value) => ShowDiff();

    public Task RefreshAsync()
    {
        LoadBackups();
        return Task.CompletedTask;
    }

    [RelayCommand]
    private Task AnalyzeAsync() => Busy(async () =>
    {
        Log.Step("Comparaison de Home Assistant avec le dépôt privé");
        var items = await Task.Run(() => _service.AnalyzeAsync(_unmappedNames));

        Items.Clear();
        foreach (var item in items.Where(i => i.Status != ApplyStatus.Identical))
            Items.Add(new ApplyItemViewModel(item, OnHaNameChanged));
        SelectedItem = Items.FirstOrDefault();

        int identical = items.Count(i => i.Status == ApplyStatus.Identical);
        StatusText = Items.Count == 0
            ? $"HA est identique au dépôt privé ({identical} élément(s) comparé(s)) : rien à appliquer."
            : $"{Items.Count} élément(s) diffèrent de HA ({identical} identique(s)). Vérifie chaque diff (à gauche HA, à droite le dépôt), puis « Appliquer ».";
        Log.Success(StatusText);
        if (Items.Any(i => i.Item.ChangedInHa))
            Log.Report("ABSENT  certains éléments ont été modifiés dans HA depuis le dernier commit : ils sont décochés. Fais d'abord « Sauvegarder HA » si ces changements comptent.");
    });

    [RelayCommand]
    private Task ApplyAsync() => Busy(async () =>
    {
        var selected = Items.Where(i => i.IsChecked).Select(i => i.Item).ToList();
        if (selected.Count == 0) { Log.Error("Aucun élément coché."); return; }

        bool restart = selected.Any(i => i.Category == ApplyCategory.Config);
        string message = $"Écrire {selected.Count} élément(s) dans Home Assistant ?\n\n"
                         + string.Join("\n", selected.Select(i => $"• {i.Target}").Take(15))
                         + (selected.Count > 15 ? "\n…" : "")
                         + "\n\nLa version actuelle de HA est sauvegardée avant (restaurable)."
                         + (restart ? "\nUn fichier de configuration change : HA sera vérifié puis redémarré." : "");
        if (MessageBox.Show(message, "Appliquer dans HA", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK)
            return;

        Log.Step($"Application de {selected.Count} élément(s) dans Home Assistant");
        await Task.Run(() => _service.ApplyAsync(selected));
        LoadBackups();

        Log.Step("Export de contrôle (sans les states)");
        await ControlAsync(selected);
        StatusText = "Appliqué. Teste HA ; en cas de problème : « Restaurer la version précédente ».";
        Items.Clear();
        Diff = null;
    });

    /// <summary>Réexporte HA dans un dossier temporaire et compare aux éléments appliqués.</summary>
    private async Task ControlAsync(List<ApplyItem> applied)
    {
        string temp = Path.Combine(Path.GetTempPath(), "MHassnage-controle");
        try
        {
            if (Directory.Exists(temp)) Directory.Delete(temp, recursive: true);
            await Task.Run(() => new HassExportService(_settings).ExportAsync(temp, includeStates: false));

            int gaps = 0;
            foreach (var item in applied)
            {
                string file = Path.Combine(temp, item.RepoPath);
                string? now = File.Exists(file) ? ApplyItem.Normalize(await File.ReadAllTextAsync(file)) : null;
                if (now == item.RepoContent) continue;
                gaps++;
                Log.Report($"ABSENT  écart après application : {item.RepoPath} ne correspond pas exactement à ce que HA renvoie");
            }
            if (gaps == 0) Log.Success("Export de contrôle : HA correspond au dépôt privé pour tous les éléments appliqués.");
        }
        catch (Exception ex)
        {
            Log.Error($"Export de contrôle : {ex.Message}");
        }
        finally
        {
            try { if (Directory.Exists(temp)) Directory.Delete(temp, recursive: true); } catch (IOException) { }
        }
    }

    [RelayCommand]
    private Task RestoreAsync() => Busy(async () =>
    {
        if (SelectedBackup is not { } backup) { Log.Error("Aucune sauvegarde sélectionnée."); return; }

        string message = $"Remettre Home Assistant dans l'état du {backup.Manifest.Created.LocalDateTime:dd.MM.yyyy HH:mm:ss} ?\n\n"
                         + string.Join("\n", backup.Manifest.Entries.Select(e => $"• {new ApplyTarget(e.Kind, e.Key)}{(e.Existed ? "" : " (sera supprimé)")}").Take(15))
                         + "\n\nL'état actuel est sauvegardé avant (la restauration est elle-même annulable).";
        if (MessageBox.Show(message, "Restaurer la version précédente", MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK)
            return;

        Log.Step($"Restauration de la sauvegarde du {backup.Manifest.Created.LocalDateTime:dd.MM.yyyy HH:mm:ss}");
        await Task.Run(() => _service.RestoreAsync(backup));
        LoadBackups();
        StatusText = "HA restauré. Pense à « Sauvegarder HA » pour remettre le dépôt privé en phase (ou corrige le dépôt).";
        Log.Success(StatusText);
    });

    [RelayCommand]
    private void OpenBackupFolder()
    {
        string folder = SelectedBackup?.Folder ?? _settings.Hass.ApplyBackupPath;
        Directory.CreateDirectory(folder);
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", folder));
    }

    private void OnHaNameChanged(ApplyItemViewModel vm)
    {
        string name = vm.HaName.Trim().Replace('\\', '/');
        if (name.Length == 0 || !vm.Item.IsUnmapped) return;
        _unmappedNames[vm.Item.RepoPath] = name;
        vm.Item.Target = vm.Item.Target with { Key = name };
        vm.Item.HaContent = _service.ReadHaFile(name);
        vm.Refresh();
        if (SelectedItem == vm) ShowDiff();
    }

    private void ShowDiff()
    {
        if (SelectedItem?.Item is not { } item) { Diff = null; return; }
        var diff = DiffService.Compare($"HA : {item.Target}   ↔   dépôt : {item.RepoPath}",
            item.HaContent ?? "", item.RepoContent, ShowWholeFile ? -1 : 3);
        Diff = new DiffViewController(diff, null);
    }

    private void LoadBackups()
    {
        Backups.Clear();
        foreach (var b in _service.ListBackups()) Backups.Add(b);
        SelectedBackup = Backups.FirstOrDefault();
    }

    private async Task Busy(Func<Task> action)
    {
        if (IsBusy) return;
        IsBusy = true;
        try
        {
            await action();
        }
        catch (HassAuthException ex)
        {
            Log.Error($"Jeton refusé par Home Assistant : {ex.Message}");
        }
        catch (Exception ex)
        {
            Log.Error(ex.Message);
            StatusText = "Erreur : voir la console.";
        }
        finally
        {
            IsBusy = false;
        }
    }
}
