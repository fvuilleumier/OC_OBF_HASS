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

        rows = NumberHunks(rows, out int hunks);

        // Le commit partiel s'appuie sur les blocs de Differ : on vérifie la correspondance
        int blocks = LineBlocks(oldText, newText).Count;
        int hunkCount = blocks == hunks ? hunks : 0;

        return new FileDiff(path, context < 0 ? rows : Fold(rows, context), added, removed, hunkCount);
    }

    /// <summary>
    /// Contenu « HEAD + blocs retenus » : chaque bloc retenu prend sa version nouvelle,
    /// les autres gardent l'ancienne. Fins de ligne d'origine conservées.
    /// </summary>
    public static string ApplyHunks(string oldText, string newText, IReadOnlySet<int> included)
    {
        var blocks = LineBlocks(oldText, newText);
        var oldLines = SplitKeepingEndings(oldText);
        var newLines = SplitKeepingEndings(newText);
        string newline = newText.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";

        var sb = new System.Text.StringBuilder(newText.Length);
        void Append(List<string> lines, int start, int count)
        {
            for (int k = start; k < start + count && k < lines.Count; k++)
            {
                // Dernière ligne sans fin de ligne suivie d'autres lignes : on la termine
                if (sb.Length > 0 && sb[^1] is not ('\n' or '\r') && lines[k].Length > 0) sb.Append(newline);
                sb.Append(lines[k]);
            }
        }

        int pos = 0;
        for (int i = 0; i < blocks.Count; i++)
        {
            var b = blocks[i];
            Append(oldLines, pos, b.DeleteStartA - pos);
            if (included.Contains(i)) Append(newLines, b.InsertStartB, b.InsertCountB);
            else Append(oldLines, b.DeleteStartA, b.DeleteCountA);
            pos = b.DeleteStartA + b.DeleteCountA;
        }
        Append(oldLines, pos, oldLines.Count - pos);
        return sb.ToString();
    }

    private static IList<DiffPlex.Model.DiffBlock> LineBlocks(string oldText, string newText) =>
        Differ.Instance.CreateDiffs(oldText, newText, false, false, new LineChunker()).DiffBlocks;

    /// <summary>Même découpage que LineChunker (\r\n, \r, \n), fins de ligne gardées.</summary>
    private static List<string> SplitKeepingEndings(string text)
    {
        var lines = new List<string>();
        int start = 0;
        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++;
            else if (text[i] is not ('\r' or '\n')) continue;
            lines.Add(text[start..(i + 1)]);
            start = i + 1;
        }
        lines.Add(text[start..]);
        return lines;
    }

    /// <summary>Chaque suite continue de lignes modifiées = un bloc ; un en-tête est inséré devant.</summary>
    private static List<DiffRow> NumberHunks(List<DiffRow> rows, out int count)
    {
        var result = new List<DiffRow>(rows.Count + 16);
        count = 0;
        for (int i = 0; i < rows.Count;)
        {
            if (IsUnchanged(rows[i])) { result.Add(rows[i++]); continue; }

            int end = i;
            while (end < rows.Count && !IsUnchanged(rows[end])) end++;
            var run = rows.GetRange(i, end - i);
            int removed = run.Count(r => r.Old.Kind != DiffLineKind.Empty);
            int added = run.Count(r => r.New.Kind != DiffLineKind.Empty);
            int firstLine = run.Select(r => r.New.Number ?? r.Old.Number).FirstOrDefault(n => n is not null) ?? 0;

            int hunk = count;
            result.Add(new DiffRow(DiffSide.Blank, DiffSide.Blank, Hunk: hunk,
                                   Header: $"Bloc {hunk + 1}  ·  ligne {firstLine}  ·  −{removed} +{added}"));
            result.AddRange(run.Select(r => r with { Hunk = hunk }));
            count++;
            i = end;
        }
        return result;
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
        !row.IsHunkHeader && row.Old.Kind == DiffLineKind.Unchanged && row.New.Kind == DiffLineKind.Unchanged;
}
