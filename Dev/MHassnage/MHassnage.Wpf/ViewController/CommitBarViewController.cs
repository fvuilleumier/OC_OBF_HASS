using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace MHassnage.Wpf.ViewController;

/// <summary>Barre de commit réutilisable : message, Pull, Commit, Push, Commit &amp; Push.</summary>
public sealed partial class CommitBarViewController(IRepositoryPanel panel) : ObservableObject
{
    [ObservableProperty]
    private string _message = "";

    /// <summary>Pull en avance rapide uniquement : jamais de merge implicite.</summary>
    [RelayCommand]
    private Task PullAsync() => RunAsync(() => { panel.Log.Step("Pull (avance rapide)"); return panel.Git.PullAsync(); });

    [RelayCommand]
    private Task CommitAsync() => RunAsync(Commit);

    [RelayCommand]
    private Task PushAsync() => RunAsync(() => { panel.Log.Step("Push"); return panel.Git.PushAsync(); });

    [RelayCommand]
    private Task CommitAndPushAsync() => RunAsync(async () =>
    {
        if (!await Commit()) return;
        panel.Log.Step("Push");
        await panel.Git.PushAsync();
    });

    private async Task<bool> Commit()
    {
        var entries = panel.SelectedEntries;
        if (entries.Count == 0) { panel.Log.Error("Aucun fichier ni bloc coché."); return false; }
        if (string.IsNullOrWhiteSpace(Message)) { panel.Log.Error("Message de commit vide."); return false; }

        panel.Log.Step($"Commit de {entries.Count} fichier(s)");
        await panel.Git.CommitAsync(entries, Message.Trim());
        Message = "";
        return true;
    }

    private async Task RunAsync(Func<Task> action)
    {
        if (panel.IsBusy) return;
        panel.IsBusy = true;
        try
        {
            await action();
        }
        catch (Exception ex)
        {
            panel.Log.Error(ex.Message);
        }
        finally
        {
            panel.IsBusy = false;
        }
        await panel.RefreshAsync();
    }
}
