using CommunityToolkit.Mvvm.ComponentModel;
using MHassnage.Core.Model;

namespace MHassnage.Wpf.ViewController;

/// <summary>Fichier modifié dans la liste : case du fichier + blocs exclus (commit partiel).</summary>
public sealed partial class ChangeItem(FileChange change) : ObservableObject
{
    public FileChange Change { get; } = change;

    [ObservableProperty]
    private bool _isChecked = true;

    /// <summary>Nombre de blocs du diff (connu une fois le diff affiché ; 0 = fichier entier).</summary>
    public int HunkCount { get; set; }

    /// <summary>Blocs décochés : non commités, ils restent en modification.</summary>
    public HashSet<int> ExcludedHunks { get; } = [];

    /// <summary>Vrai si une partie seulement des blocs sera commitée (affiché « partiel »).</summary>
    public bool IsPartial => ExcludedHunks.Count > 0 && ExcludedHunks.Count < HunkCount;

    public void SetHunk(int index, bool included)
    {
        if (included) ExcludedHunks.Remove(index); else ExcludedHunks.Add(index);
        if (included && !IsChecked) IsChecked = true;
        OnPropertyChanged(nameof(IsPartial));
    }

    /// <summary>Entrée de commit, ou null si rien n'est retenu.</summary>
    public CommitEntry? ToCommitEntry()
    {
        if (!IsChecked) return null;
        if (ExcludedHunks.Count == 0 || HunkCount == 0) return new CommitEntry(Change);
        if (ExcludedHunks.Count >= HunkCount) return null;
        return new CommitEntry(Change, Enumerable.Range(0, HunkCount).Except(ExcludedHunks).ToHashSet());
    }
}
