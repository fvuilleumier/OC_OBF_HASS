using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MHassnage.Core.Model;
using MHassnage.Core.Model.OwnException;
using MHassnage.Core.Service;

namespace MHassnage.Wpf.ViewController;

/// <summary>Panel qui se met à jour à sa première ouverture.</summary>
public interface IRefreshable
{
    Task RefreshAsync();
}

/// <summary>
/// Opération « Sauvegarder HA » : export de HA dans le dépôt privé, revue du diff,
/// puis Commit / Push / Commit &amp; Push.
/// </summary>
public sealed partial class BackupViewController : ObservableObject, IRefreshable
{
    private readonly AppSettings _settings;

    public BackupViewController(AppSettings settings)
    {
        _settings = settings;
        Repository = new RepositoryViewController(settings.Repositories.Private.Path, settings.Git.ExecutablePath, Log);
    }

    public LogViewController Log { get; } = new();
    public RepositoryViewController Repository { get; }

    public Task RefreshAsync() => Repository.RefreshAsync();

    /// <summary>Export de HA dans le dépôt privé, puis relecture des modifications.</summary>
    [RelayCommand]
    private async Task ExportAsync()
    {
        if (Repository.IsBusy) return;
        Repository.IsBusy = true;
        try
        {
            Log.Clear();
            await HassExportStep.RunAsync(_settings, Log);
            Repository.SuggestMessage($"Sauvegarde HA du {DateTime.Now:dd.MM.yyyy HH:mm}");
        }
        finally
        {
            Repository.IsBusy = false;
        }
        await Repository.RefreshAsync();
    }
}

/// <summary>Export de HA partagé par les panels ; les erreurs vont au journal.</summary>
internal static class HassExportStep
{
    /// <returns>Vrai si l'export a réussi.</returns>
    public static async Task<bool> RunAsync(AppSettings settings, LogViewController log)
    {
        try
        {
            await Task.Run(() => new HassExportService(settings, log).ExportAsync());
            log.Success("Export terminé.");
            return true;
        }
        catch (HassAuthException ex)
        {
            log.Error($"Jeton refusé par Home Assistant : {ex.Message}");
            foreach (string line in HassTokenHelp.Text(settings.Hass.Url, SettingsService.SettingsPath).Split('\n'))
                log.Report(line.TrimEnd());
        }
        catch (Exception ex)
        {
            log.Error(ex.Message);
        }
        return false;
    }
}
