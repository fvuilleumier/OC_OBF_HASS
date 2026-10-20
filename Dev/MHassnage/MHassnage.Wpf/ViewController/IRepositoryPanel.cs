using MHassnage.Core.Model;
using MHassnage.Core.Service;

namespace MHassnage.Wpf.ViewController;

/// <summary>Ce qu'un panel fournit à la <see cref="CommitBarViewController"/>.</summary>
public interface IRepositoryPanel
{
    GitService Git { get; }
    LogViewController Log { get; }
    bool IsBusy { get; set; }

    /// <summary>Fichiers cochés pour le commit.</summary>
    IReadOnlyCollection<FileChange> SelectedFiles { get; }

    Task RefreshAsync();
}
