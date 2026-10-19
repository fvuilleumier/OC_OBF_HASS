using System.Text.RegularExpressions;
using TebiObfuscator.Core.Model.Policy;

namespace TebiObfuscator.Core.Model.Exchange;

/// <summary>
/// Résultat d'une fusion.
/// </summary>
/// <param name="Text">Texte fusionné, prêt à être écrit</param>
/// <param name="Added">Lignes du résultat qui ne viennent pas du fichier actuel</param>
/// <param name="Removed">Lignes du fichier actuel absentes du résultat</param>
/// <param name="ConflictLines">Ligne de chaque marqueur d'ouverture de conflit, à partir de 1</param>
/// <param name="CurrentToResult">Pour chaque ligne du fichier actuel, son rang dans le résultat, ou -1</param>
/// <param name="TheirsToResult">Pour chaque ligne de la version de l'IA, son rang dans le résultat, ou -1</param>
public sealed record MergeResult(
    string Text,
    int Added,
    int Removed,
    IReadOnlyList<int> ConflictLines,
    IReadOnlyList<int> CurrentToResult,
    IReadOnlyList<int> TheirsToResult)
{
    /// <summary>Nombre de conflits à résoudre.</summary>
    public int Conflicts => ConflictLines.Count;

    /// <summary>Vrai si le résultat ne diffère pas du fichier actuel.</summary>
    public bool IsUnchanged => Added == 0 && Removed == 0 && Conflicts == 0;

    /// <summary>
    /// Passages changés différemment des deux côtés, résolus ou non, dans
    /// l'ordre du fichier.
    /// </summary>
    public IReadOnlyList<ConflictBlock> Blocks { get; init; } = [];
}

/// <summary>Ce que l'on garde d'un passage en conflit.</summary>
public enum ConflictChoice
{
    /// <summary>Rien n'est tranché : les deux versions sont écrites entre marqueurs.</summary>
    Unresolved,

    /// <summary>La version en clair actuelle.</summary>
    Clear,

    /// <summary>La version de l'IA, restaurée.</summary>
    Ai,

    /// <summary>Les deux : la version en clair, puis celle de l'IA.</summary>
    Both,
}

/// <summary>
/// Passage changé différemment des deux côtés, tel qu'on le montre pour le
/// trancher. Il porte des valeurs réelles : il est fait pour l'écran.
/// </summary>
/// <param name="Index">Rang du conflit dans le fichier, à partir de 0</param>
/// <param name="Line">Première ligne du passage dans le fichier écrit, à partir de 1</param>
/// <param name="Before">Lignes qui le précèdent, pour le situer</param>
/// <param name="Clear">Version en clair actuelle</param>
/// <param name="Ai">Version de l'IA, restaurée</param>
/// <param name="After">Lignes qui le suivent</param>
public sealed record ConflictBlock(
    int Index,
    int Line,
    IReadOnlyList<string> Before,
    IReadOnlyList<string> Clear,
    IReadOnlyList<string> Ai,
    IReadOnlyList<string> After);

/// <summary>
/// Fusionne, à la façon de git, le travail de l'IA dans le fichier en clair.
///
/// Trois versions sont en jeu : la base — ce que la cible avait reçu, restauré
/// —, le fichier en clair tel qu'il est aujourd'hui, et le fichier de l'IA,
/// restauré lui aussi. Ce que l'IA a changé se lit entre la base et sa
/// version ; ce qui a changé côté clair, entre la base et le fichier actuel.
/// Un changement d'un seul côté est repris ; le même changement des deux côtés
/// aussi ; deux changements différents aux mêmes lignes sont un conflit, écrit
/// entre marqueurs dans le fichier, à résoudre dans l'outil git.
///
/// Une ligne que l'IA n'a pas touchée vient du fichier actuel, octet pour
/// octet : la restauration rend la forme canonique d'une valeur — une casse,
/// des accents, des zéros de tête uniformisés —, et ce n'est pas un changement
/// à faire paraître. La base, restaurée de la même façon que la version de
/// l'IA, porte les mêmes artefacts : ils s'annulent dans la comparaison.
/// </summary>
public static partial class ThreeWayMerge
{
    /// <summary>Marqueur d'ouverture d'un conflit, suivi de la version en clair.</summary>
    public const string OursMarker = "<<<<<<< en clair";

    /// <summary>Séparateur des deux versions d'un conflit.</summary>
    public const string SeparatorMarker = "=======";

    /// <summary>Marqueur de fermeture d'un conflit, précédé de la version de l'IA.</summary>
    public const string TheirsMarker = ">>>>>>> IA";

    private const int TimeoutMilliseconds = 1000;

    /// <summary>
    /// Comparaison tolérante aux artefacts de la restauration : casse, accents
    /// et zéros de tête d'un nombre ne distinguent pas deux lignes.
    /// </summary>
    public static IEqualityComparer<string> Tolerant { get; } = new TolerantComparer();

    /// <summary>
    /// Reporte le travail de l'IA sur un fichier en clair resté tel qu'au
    /// dernier échange, ligne pour ligne avec la base. Aucun conflit n'est
    /// possible : seuls les passages que l'IA a changés sont remplacés, le reste
    /// est repris du fichier actuel.
    /// </summary>
    /// <param name="baseText">Base : ce que la cible avait reçu, restauré</param>
    /// <param name="current">Fichier en clair actuel, de même nombre de lignes que la base</param>
    /// <param name="theirs">Version de l'IA, restaurée</param>
    /// <returns>Le résultat</returns>
    public static MergeResult Apply(TextLayout baseText, TextLayout current, TextLayout theirs)
    {
        ArgumentNullException.ThrowIfNull(baseText);
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(theirs);

        int[] map = LineDiff.Match(baseText.Lines, theirs.Lines, StringComparer.Ordinal);
        Builder builder = new(current, theirs, []);
        int basePosition = 0;
        int theirsPosition = 0;

        for (int line = 0; line <= baseText.Lines.Count; line++)
        {
            bool end = line == baseText.Lines.Count;
            if (!end && map[line] < 0)
            {
                continue;
            }

            int theirsEnd = end ? theirs.Lines.Count : map[line];

            // Passage changé par l'IA : sa version remplace celle du fichier
            // actuel, aligné sur la base.
            if (line > basePosition || theirsEnd > theirsPosition)
            {
                builder.Replace(basePosition, line - basePosition, theirsPosition, theirsEnd - theirsPosition);
            }

            if (!end)
            {
                builder.KeepCurrent(line, theirsEnd);
            }

            basePosition = line + 1;
            theirsPosition = theirsEnd + 1;
        }

        return builder.Finish();
    }

    /// <summary>
    /// Fusionne le travail de l'IA dans un fichier en clair qui a changé depuis
    /// le dernier échange.
    /// </summary>
    /// <param name="baseText">Base : ce que la cible avait reçu, restauré</param>
    /// <param name="current">Fichier en clair actuel</param>
    /// <param name="theirs">Version de l'IA, restaurée</param>
    /// <param name="choices">
    /// Ce que l'on garde de chaque conflit, dans l'ordre du fichier ; un conflit
    /// sans choix est écrit entre marqueurs. Les conflits ne dépendent pas des
    /// choix : leur rang reste le même d'une fusion à l'autre.
    /// </param>
    /// <returns>Le résultat, conflits compris</returns>
    public static MergeResult Merge(TextLayout baseText, TextLayout current, TextLayout theirs, IReadOnlyList<ConflictChoice>? choices = null)
    {
        ArgumentNullException.ThrowIfNull(baseText);
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(theirs);

        // Le fichier actuel diffère de la base par ses propres changements et
        // par les artefacts de la restauration : comparaison tolérante. La
        // version de l'IA porte les mêmes artefacts que la base : comparaison
        // exacte, pour ne manquer aucun de ses changements.
        int[] toCurrent = LineDiff.Match(baseText.Lines, current.Lines, Tolerant);
        int[] toTheirs = LineDiff.Match(baseText.Lines, theirs.Lines, StringComparer.Ordinal);

        Builder builder = new(current, theirs, choices ?? []);
        int baseStart = 0;
        int currentStart = 0;
        int theirsStart = 0;

        for (int line = 0; line <= baseText.Lines.Count; line++)
        {
            bool end = line == baseText.Lines.Count;

            // Une ligne de la base retrouvée des deux côtés est un point fixe :
            // tout ce qui la précède forme un passage à départager.
            if (!end && (toCurrent[line] < currentStart || toTheirs[line] < theirsStart))
            {
                continue;
            }

            int currentEnd = end ? current.Lines.Count : toCurrent[line];
            int theirsEnd = end ? theirs.Lines.Count : toTheirs[line];

            Resolve(builder, baseText, current, theirs, (baseStart, line), (currentStart, currentEnd), (theirsStart, theirsEnd));

            if (!end)
            {
                builder.KeepCurrent(currentEnd, theirsEnd);
            }

            baseStart = line + 1;
            currentStart = currentEnd + 1;
            theirsStart = theirsEnd + 1;
        }

        return builder.Finish();
    }

    /// <summary>
    /// Départage un passage compris entre deux points fixes.
    /// </summary>
    /// <param name="builder">Résultat en construction</param>
    /// <param name="baseText">Base</param>
    /// <param name="current">Fichier actuel</param>
    /// <param name="theirs">Version de l'IA</param>
    /// <param name="baseRange">Passage de la base, début et fin exclue</param>
    /// <param name="currentRange">Passage du fichier actuel</param>
    /// <param name="theirsRange">Passage de la version de l'IA</param>
    private static void Resolve(
        Builder builder,
        TextLayout baseText,
        TextLayout current,
        TextLayout theirs,
        (int Start, int End) baseRange,
        (int Start, int End) currentRange,
        (int Start, int End) theirsRange)
    {
        if (baseRange.End == baseRange.Start && currentRange.End == currentRange.Start && theirsRange.End == theirsRange.Start)
        {
            return;
        }

        bool theirsUnchanged = SameLines(baseText, baseRange, theirs, theirsRange, StringComparer.Ordinal);
        bool currentUnchanged = SameLines(baseText, baseRange, current, currentRange, Tolerant);

        if (theirsUnchanged)
        {
            // L'IA n'a rien changé ici : le fichier actuel reste tel quel.
            builder.KeepCurrentRange(currentRange.Start, currentRange.End);
        }
        else if (currentUnchanged || SameLines(current, currentRange, theirs, theirsRange, Tolerant))
        {
            // Seule l'IA a changé ce passage — ou les deux côtés de la même
            // façon : sa version est reprise.
            builder.ReplaceRange(currentRange.Start, currentRange.End, theirsRange.Start, theirsRange.End);
        }
        else
        {
            builder.Conflict(currentRange.Start, currentRange.End, theirsRange.Start, theirsRange.End);
        }
    }

    /// <summary>
    /// Compare deux passages ligne à ligne.
    /// </summary>
    /// <param name="left">Premier texte</param>
    /// <param name="leftRange">Passage du premier texte</param>
    /// <param name="right">Second texte</param>
    /// <param name="rightRange">Passage du second texte</param>
    /// <param name="comparer">Égalité de deux lignes</param>
    /// <returns><c>true</c> si les deux passages sont identiques</returns>
    private static bool SameLines(
        TextLayout left,
        (int Start, int End) leftRange,
        TextLayout right,
        (int Start, int End) rightRange,
        IEqualityComparer<string> comparer)
    {
        int length = leftRange.End - leftRange.Start;
        if (length != rightRange.End - rightRange.Start)
        {
            return false;
        }

        for (int offset = 0; offset < length; offset++)
        {
            if (!comparer.Equals(left.Lines[leftRange.Start + offset], right.Lines[rightRange.Start + offset]))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Zéros de tête d'un nombre, que la restauration ne garde pas.</summary>
    [GeneratedRegex(@"(?<![\d.])0+(?=\d)", RegexOptions.None, TimeoutMilliseconds)]
    private static partial Regex LeadingZeros();

    /// <summary>
    /// Égalité de deux lignes, sans tenir compte des artefacts de la
    /// restauration.
    /// </summary>
    private sealed class TolerantComparer : IEqualityComparer<string>
    {
        /// <inheritdoc />
        public bool Equals(string? x, string? y)
        {
            if (ReferenceEquals(x, y) || string.Equals(x, y, StringComparison.Ordinal))
            {
                return true;
            }

            return x is not null && y is not null && string.Equals(Fold(x), Fold(y), StringComparison.Ordinal);
        }

        /// <inheritdoc />
        public int GetHashCode(string obj)
        {
            ArgumentNullException.ThrowIfNull(obj);
            return StringComparer.Ordinal.GetHashCode(Fold(obj));
        }

        /// <summary>
        /// Ramène une ligne à sa forme de comparaison.
        /// </summary>
        /// <param name="line">Ligne</param>
        /// <returns>La ligne sans casse, sans accents et sans zéros de tête</returns>
        private static string Fold(string line)
        {
            return LeadingZeros().Replace(TextFolding.Fold(line), string.Empty);
        }
    }

    /// <summary>
    /// Résultat en construction : les lignes reprises du fichier actuel gardent
    /// leur fin de ligne, celles de l'IA prennent la fin dominante du fichier
    /// actuel, et la présence d'une fin de ligne en fin de fichier suit le
    /// fichier actuel.
    /// </summary>
    /// <param name="current">Fichier actuel</param>
    /// <param name="theirs">Version de l'IA</param>
    /// <param name="choices">Ce que l'on garde de chaque conflit, dans l'ordre</param>
    private sealed class Builder(TextLayout current, TextLayout theirs, IReadOnlyList<ConflictChoice> choices)
    {
        /// <summary>Lignes de contexte montrées avant et après un conflit.</summary>
        private const int Context = 3;

        private readonly List<(string Line, string Terminator)> _lines = [];
        private readonly List<int> _conflicts = [];
        private readonly List<(ConflictBlock Block, int End)> _blocks = [];
        private readonly int[] _currentToResult = [.. Enumerable.Repeat(-1, current.Lines.Count)];
        private readonly int[] _theirsToResult = [.. Enumerable.Repeat(-1, theirs.Lines.Count)];
        private readonly string _terminator = current.Lines.Count > 0 ? current.DominantTerminator : theirs.DominantTerminator;
        private int _added;
        private int _removed;

        /// <summary>Reprend une ligne du fichier actuel.</summary>
        /// <param name="line">Rang de la ligne</param>
        public void KeepCurrent(int line)
        {
            _currentToResult[line] = _lines.Count;
            _lines.Add((current.Lines[line], current.Terminators[line]));
        }

        /// <summary>Reprend une ligne du fichier actuel, que la version de l'IA a gardée telle quelle.</summary>
        /// <param name="line">Rang de la ligne dans le fichier actuel</param>
        /// <param name="theirsLine">Rang de la même ligne dans la version de l'IA</param>
        public void KeepCurrent(int line, int theirsLine)
        {
            _theirsToResult[theirsLine] = _lines.Count;
            KeepCurrent(line);
        }

        /// <summary>Reprend un passage du fichier actuel.</summary>
        /// <param name="start">Première ligne</param>
        /// <param name="end">Ligne qui suit le passage</param>
        public void KeepCurrentRange(int start, int end)
        {
            for (int line = start; line < end; line++)
            {
                KeepCurrent(line);
            }
        }

        /// <summary>
        /// Remplace un passage du fichier actuel, aligné sur la base, par un
        /// passage de la version de l'IA.
        /// </summary>
        /// <param name="start">Première ligne remplacée du fichier actuel</param>
        /// <param name="count">Nombre de lignes remplacées</param>
        /// <param name="theirsStart">Première ligne reprise de l'IA</param>
        /// <param name="theirsCount">Nombre de lignes reprises</param>
        public void Replace(int start, int count, int theirsStart, int theirsCount)
        {
            ReplaceRange(start, start + count, theirsStart, theirsStart + theirsCount);
        }

        /// <summary>
        /// Remplace un passage du fichier actuel par un passage de la version
        /// de l'IA.
        /// </summary>
        /// <param name="start">Première ligne remplacée</param>
        /// <param name="end">Ligne qui suit le passage remplacé</param>
        /// <param name="theirsStart">Première ligne reprise de l'IA</param>
        /// <param name="theirsEnd">Ligne qui suit le passage repris</param>
        public void ReplaceRange(int start, int end, int theirsStart, int theirsEnd)
        {
            _removed += end - start;
            for (int line = theirsStart; line < theirsEnd; line++)
            {
                _theirsToResult[line] = _lines.Count;
                _lines.Add((theirs.Lines[line], _terminator));
                _added++;
            }
        }

        /// <summary>
        /// Écrit un conflit selon le choix fait pour lui : la version en clair,
        /// celle de l'IA, les deux, ou — sans choix — les deux entre marqueurs.
        /// </summary>
        /// <param name="start">Première ligne du passage en clair</param>
        /// <param name="end">Ligne qui suit le passage en clair</param>
        /// <param name="theirsStart">Première ligne du passage de l'IA</param>
        /// <param name="theirsEnd">Ligne qui suit le passage de l'IA</param>
        public void Conflict(int start, int end, int theirsStart, int theirsEnd)
        {
            int index = _blocks.Count;
            ConflictChoice choice = index < choices.Count ? choices[index] : ConflictChoice.Unresolved;
            ConflictBlock block = new(
                index,
                _lines.Count + 1,
                [.. _lines.Skip(Math.Max(0, _lines.Count - Context)).Select(line => line.Line)],
                [.. current.Lines.Skip(start).Take(end - start)],
                [.. theirs.Lines.Skip(theirsStart).Take(theirsEnd - theirsStart)],
                []);

            switch (choice)
            {
                case ConflictChoice.Clear:
                    // Les lignes en clair gardent leur fin de ligne : rien ne
                    // change pour git.
                    KeepCurrentRange(start, end);
                    break;

                case ConflictChoice.Ai:
                    ReplaceRange(start, end, theirsStart, theirsEnd);
                    break;

                case ConflictChoice.Both:
                    KeepCurrentRange(start, end);
                    ReplaceRange(end, end, theirsStart, theirsEnd);
                    break;

                default:
                    WriteMarkers(start, end, theirsStart, theirsEnd);
                    break;
            }

            _blocks.Add((block, _lines.Count));
        }

        /// <summary>
        /// Écrit un conflit non tranché : la version en clair puis celle de
        /// l'IA, entre marqueurs.
        /// </summary>
        /// <param name="start">Première ligne du passage en clair</param>
        /// <param name="end">Ligne qui suit le passage en clair</param>
        /// <param name="theirsStart">Première ligne du passage de l'IA</param>
        /// <param name="theirsEnd">Ligne qui suit le passage de l'IA</param>
        private void WriteMarkers(int start, int end, int theirsStart, int theirsEnd)
        {
            _conflicts.Add(_lines.Count + 1);
            _lines.Add((OursMarker, _terminator));

            for (int line = start; line < end; line++)
            {
                _currentToResult[line] = _lines.Count;
                _lines.Add((current.Lines[line], _terminator));
            }

            _lines.Add((SeparatorMarker, _terminator));
            for (int line = theirsStart; line < theirsEnd; line++)
            {
                _theirsToResult[line] = _lines.Count;
                _lines.Add((theirs.Lines[line], _terminator));
            }

            _lines.Add((TheirsMarker, _terminator));
            _added += 3 + (theirsEnd - theirsStart);
        }

        /// <summary>
        /// Termine le résultat : la fin du fichier suit celle du fichier
        /// actuel.
        /// </summary>
        /// <returns>Le résultat</returns>
        public MergeResult Finish()
        {
            if (_lines.Count > 0)
            {
                bool endsWithTerminator = current.Lines.Count > 0 ? current.EndsWithTerminator : theirs.EndsWithTerminator;
                (string line, string terminator) = _lines[^1];

                if (endsWithTerminator && terminator.Length == 0)
                {
                    _lines[^1] = (line, _terminator);
                }
                else if (!endsWithTerminator && terminator.Length > 0)
                {
                    _lines[^1] = (line, string.Empty);
                }
            }

            // Le contexte qui suit un conflit n'est connu qu'une fois le fichier
            // entier composé.
            List<ConflictBlock> blocks = [.. _blocks.Select(item => item.Block with
            {
                After = [.. _lines.Skip(item.End).Take(Context).Select(line => line.Line)],
            })];

            return new MergeResult(TextLayout.Compose(_lines), _added, _removed, _conflicts, _currentToResult, _theirsToResult)
            {
                Blocks = blocks,
            };
        }
    }
}
