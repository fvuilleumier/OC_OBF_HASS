using DiffPlex;
using DiffPlex.Chunkers;
using DiffPlex.DiffBuilder;
using DiffPlex.DiffBuilder.Model;
using MHassnage.Core.Model;

namespace MHassnage.Core.Service;

/// <summary>
/// Diff ligne à ligne côte à côte (DiffPlex), mots modifiés repérés dans les lignes changées,
/// lignes identiques repliées au-delà de <c>context</c> lignes autour des modifications.
/// </summary>
public static class DiffService
{
    private static readonly SideBySideDiffBuilder Builder =
        new(new Differ(), new LineChunker(), new WordChunker());

    /// <param name="context">Lignes identiques gardées autour d'un changement ; négatif = tout afficher.</param>
    public static FileDiff Compare(string path, string oldText, string newText, int context = 3)
    {
        var model = Builder.BuildDiffModel(oldText, newText, ignoreWhitespace: false);
        var rows = new List<DiffRow>(model.OldText.Lines.Count);
        int added = 0, removed = 0;

        for (int i = 0; i < model.OldText.Lines.Count; i++)
        {
            var oldSide = ToSide(model.OldText.Lines[i]);
            var newSide = ToSide(model.NewText.Lines[i]);
            if (oldSide.Kind is DiffLineKind.Deleted or DiffLineKind.Modified) removed++;
            if (newSide.Kind is DiffLineKind.Inserted or DiffLineKind.Modified) added++;
            rows.Add(new DiffRow(oldSide, newSide));
        }

        return new FileDiff(path, context < 0 ? rows : Fold(rows, context), added, removed);
    }

    private static DiffSide ToSide(DiffPiece piece)
    {
        var kind = piece.Type switch
        {
            ChangeType.Deleted => DiffLineKind.Deleted,
            ChangeType.Inserted => DiffLineKind.Inserted,
            ChangeType.Modified => DiffLineKind.Modified,
            ChangeType.Imaginary => DiffLineKind.Empty,
            _ => DiffLineKind.Unchanged,
        };
        if (kind == DiffLineKind.Empty) return DiffSide.Blank;

        IReadOnlyList<DiffSegment> segments = kind == DiffLineKind.Modified && piece.SubPieces.Count > 0
            ? piece.SubPieces
                .Where(p => p.Type != ChangeType.Imaginary && p.Text is not null)
                .Select(p => new DiffSegment(p.Text, p.Type != ChangeType.Unchanged))
                .ToList()
            : [new DiffSegment(piece.Text ?? "", false)];

        return new DiffSide(piece.Position, kind, segments);
    }

    /// <summary>Remplace les suites de lignes identiques éloignées des changements par un repli.</summary>
    private static List<DiffRow> Fold(List<DiffRow> rows, int context)
    {
        bool[] keep = new bool[rows.Count];
        for (int i = 0; i < rows.Count; i++)
        {
            if (IsUnchanged(rows[i])) continue;
            for (int j = Math.Max(0, i - context); j <= Math.Min(rows.Count - 1, i + context); j++)
                keep[j] = true;
        }

        var result = new List<DiffRow>();
        int folded = 0;
        for (int i = 0; i < rows.Count; i++)
        {
            if (keep[i])
            {
                if (folded > 0) result.Add(new DiffRow(DiffSide.Blank, DiffSide.Blank, folded));
                folded = 0;
                result.Add(rows[i]);
            }
            else folded++;
        }
        if (folded > 0) result.Add(new DiffRow(DiffSide.Blank, DiffSide.Blank, folded));
        return result;
    }

    private static bool IsUnchanged(DiffRow row) =>
        row.Old.Kind == DiffLineKind.Unchanged && row.New.Kind == DiffLineKind.Unchanged;
}
