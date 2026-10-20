using MHassnage.Core.Model;
using MHassnage.Core.Service;

namespace MHassnage.Wpf.ViewController;

/// <summary>Ce qu'un dépôt fournit à la <see cref="CommitBarViewController"/>.</summary>
public interface IRepositoryPanel : IRefreshable
{
    GitService Git { get; }
    LogViewController Log { get; }
    bool IsBusy { get; set; }

    /// <summary>Fichiers (entiers ou par blocs) retenus pour le commit.</summary>
    IReadOnlyCollection<CommitEntry> SelectedEntries { get; }
}
