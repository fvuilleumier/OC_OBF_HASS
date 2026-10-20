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

/// <summary>Ligne du diff côte à côte ; Fold = lignes identiques repliées (« … N lignes »).</summary>
public sealed record DiffRow(DiffSide Old, DiffSide New, int FoldedCount = 0)
{
    public bool IsFold => FoldedCount > 0;
}

/// <summary>Diff complet d'un fichier.</summary>
public sealed record FileDiff(string Path, IReadOnlyList<DiffRow> Rows, int Added, int Removed)
{
    public bool IsIdentical => Added == 0 && Removed == 0;
}
