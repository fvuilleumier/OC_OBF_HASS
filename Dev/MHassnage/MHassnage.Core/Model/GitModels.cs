namespace MHassnage.Core.Model;

/// <summary>Nature d'une modification dans la copie de travail.</summary>
public enum ChangeKind
{
    Modified,
    Added,
    Deleted,
    Renamed,
    Untracked,
    Conflicted,
}

/// <summary>Un fichier modifié (chemin relatif à la racine du dépôt, séparateurs « / »).</summary>
public sealed record FileChange(string Path, ChangeKind Kind, string? OldPath = null)
{
    /// <summary>Lettre affichée dans la liste (comme GitKraken / git status).</summary>
    public string Letter => Kind switch
    {
        ChangeKind.Modified => "M",
        ChangeKind.Added => "A",
        ChangeKind.Deleted => "D",
        ChangeKind.Renamed => "R",
        ChangeKind.Untracked => "?",
        ChangeKind.Conflicted => "!",
        _ => " ",
    };
}

/// <summary>État d'un dépôt : branche, synchronisation avec le distant, modifications.</summary>
public sealed record RepoState(
    string Root,
    string Branch,
    string? Upstream,
    int Ahead,
    int Behind,
    IReadOnlyList<FileChange> Changes)
{
    public bool HasChanges => Changes.Count > 0;

    public string SyncText => Upstream is null
        ? "pas de branche distante"
        : (Ahead, Behind) switch
        {
            (0, 0) => $"à jour avec {Upstream}",
            (_, 0) => $"↑{Ahead} à pousser vers {Upstream}",
            (0, _) => $"↓{Behind} à récupérer de {Upstream}",
            _ => $"↑{Ahead} ↓{Behind} — divergé de {Upstream}",
        };
}

/// <summary>
/// Fichier à commiter : entier (IncludedHunks null), ou seulement les blocs listés
/// (index des blocs du diff HEAD → copie de travail).
/// </summary>
public sealed record CommitEntry(FileChange Change, IReadOnlySet<int>? IncludedHunks = null)
{
    public bool IsPartial => IncludedHunks is not null;
}

/// <summary>Résultat brut d'une commande git.</summary>
public sealed record GitResult(int ExitCode, string Output, string Error)
{
    public bool Success => ExitCode == 0;
}
