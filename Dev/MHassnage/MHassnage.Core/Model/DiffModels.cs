namespace MHassnage.Core.Model;

/// <summary>Type d'une moitié de ligne dans le diff côte à côte.</summary>
public enum DiffLineKind
{
    Unchanged,
    Deleted,
    Inserted,
    Modified,

    /// <summary>Ligne absente de ce côté (remplissage pour l'alignement).</summary>
    Empty,
}

/// <summary>Morceau de texte d'une ligne ; Changed = mot modifié (surligné).</summary>
public sealed record DiffSegment(string Text, bool Changed);

/// <summary>Une moitié de ligne (ancien ou nouveau côté).</summary>
public sealed record DiffSide(int? Number, DiffLineKind Kind, IReadOnlyList<DiffSegment> Segments)
{
    public static readonly DiffSide Blank = new(null, DiffLineKind.Empty, []);
    public string Text => string.Concat(Segments.Select(s => s.Text));
}

/// <summary>
/// Ligne du diff côte à côte.
/// FoldedCount &gt; 0 : lignes identiques repliées (« … N lignes »).
/// Header non null : en-tête du bloc n° Hunk (case à cocher du commit partiel).
/// Hunk ≥ 0 sur une ligne modifiée : bloc auquel elle appartient.
/// </summary>
public sealed record DiffRow(DiffSide Old, DiffSide New, int FoldedCount = 0, int Hunk = -1, string? Header = null)
{
    public bool IsFold => FoldedCount > 0;
    public bool IsHunkHeader => Header is not null;
}

/// <summary>Diff complet d'un fichier ; HunkCount = 0 si le découpage en blocs n'est pas fiable.</summary>
public sealed record FileDiff(string Path, IReadOnlyList<DiffRow> Rows, int Added, int Removed, int HunkCount)
{
    public bool IsIdentical => Added == 0 && Removed == 0;
}
