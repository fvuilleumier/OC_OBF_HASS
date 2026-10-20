using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace MHassnage.Wpf.ViewController;

/// <summary>Barre de commit réutilisable : message, Commit, Push, Commit &amp; Push.</summary>
public sealed partial class CommitBarViewController(IRepositoryPanel panel) : ObservableObject
{
    [ObservableProperty]
    private string _message = "";

    [RelayCommand]
    private Task CommitAsync() => RunAsync(commit: true, push: false);

    [RelayCommand]
    private Task PushAsync() => RunAsync(commit: false, push: true);

    [RelayCommand]
    private Task CommitAndPushAsync() => RunAsync(commit: true, push: true);

    private async Task RunAsync(bool commit, bool push)
    {
        if (panel.IsBusy) return;
        panel.IsBusy = true;
        try
        {
            if (commit)
            {
                var files = panel.SelectedFiles;
                if (files.Count == 0) { panel.Log.Error("Aucun fichier coché."); return; }
                if (string.IsNullOrWhiteSpace(Message)) { panel.Log.Error("Message de commit vide."); return; }

                await panel.Git.CommitAsync(files, Message.Trim());
                Message = "";
            }
            if (push) await panel.Git.PushAsync();
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
