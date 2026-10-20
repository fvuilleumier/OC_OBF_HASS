namespace Obfuscator.Core.Model.Exchange;

/// <summary>
/// Met en correspondance les lignes de deux versions d'un texte : la plus
/// longue suite de lignes communes, dans l'ordre (algorithme de Myers).
///
/// C'est la brique de la fusion : ce qui n'est pas mis en correspondance a été
/// retiré d'un côté ou ajouté de l'autre. Les débuts et les fins communs sont
/// écartés d'abord — une IA change en général quelques lignes, pas tout le
/// fichier —, et la recherche est bornée : au-delà d'un certain nombre de
/// différences, le milieu est tenu pour entièrement remplacé, ce qui reste juste
/// mais devient grossier, plutôt que de consommer une mémoire déraisonnable.
/// </summary>
public static class LineDiff
{
    /// <summary>
    /// Nombre maximal de différences recherchées dans la partie centrale. La
    /// mémoire du parcours croît avec le carré de ce nombre.
    /// </summary>
    private const int MaximumEdits = 2000;

    /// <summary>
    /// Met en correspondance les lignes de deux textes.
    /// </summary>
    /// <param name="first">Lignes du premier texte</param>
    /// <param name="second">Lignes du second texte</param>
    /// <param name="comparer">Égalité de deux lignes</param>
    /// <returns>Pour chaque ligne du premier texte, le rang de la ligne correspondante du second, ou -1</returns>
    public static int[] Match(IReadOnlyList<string> first, IReadOnlyList<string> second, IEqualityComparer<string> comparer)
    {
        ArgumentNullException.ThrowIfNull(first);
        ArgumentNullException.ThrowIfNull(second);
        ArgumentNullException.ThrowIfNull(comparer);

        int[] map = new int[first.Count];
        Array.Fill(map, -1);

        // Début commun.
        int prefix = 0;
        while (prefix < first.Count && prefix < second.Count && comparer.Equals(first[prefix], second[prefix]))
        {
            map[prefix] = prefix;
            prefix++;
        }

        // Fin commune, sans empiéter sur le début.
        int suffix = 0;
        while (suffix < first.Count - prefix
            && suffix < second.Count - prefix
            && comparer.Equals(first[first.Count - 1 - suffix], second[second.Count - 1 - suffix]))
        {
            map[first.Count - 1 - suffix] = second.Count - 1 - suffix;
            suffix++;
        }

        int length = first.Count - prefix - suffix;
        int otherLength = second.Count - prefix - suffix;
        if (length > 0 && otherLength > 0)
        {
            MatchMiddle(first, second, comparer, prefix, length, otherLength, map);
        }

        return map;
    }

    /// <summary>
    /// Met en correspondance les parties centrales, par l'algorithme de Myers.
    /// </summary>
    /// <param name="first">Lignes du premier texte</param>
    /// <param name="second">Lignes du second texte</param>
    /// <param name="comparer">Égalité de deux lignes</param>
    /// <param name="offset">Rang de la première ligne de la partie centrale, le même dans les deux textes</param>
    /// <param name="length">Longueur de la partie centrale du premier texte</param>
    /// <param name="otherLength">Longueur de la partie centrale du second texte</param>
    /// <param name="map">Correspondances, complétées ici</param>
    private static void MatchMiddle(
        IReadOnlyList<string> first,
        IReadOnlyList<string> second,
        IEqualityComparer<string> comparer,
        int offset,
        int length,
        int otherLength,
        int[] map)
    {
        int limit = Math.Min(length + otherLength, MaximumEdits);
        int center = limit + 1;
        int[] frontier = new int[(2 * limit) + 3];
        List<int[]> trace = [];

        for (int edits = 0; edits <= limit; edits++)
        {
            for (int diagonal = -edits; diagonal <= edits; diagonal += 2)
            {
                // Descendre (une ligne ajoutée) ou avancer (une ligne retirée),
                // selon le chemin qui est allé le plus loin.
                int x = diagonal == -edits || (diagonal != edits && frontier[center + diagonal - 1] < frontier[center + diagonal + 1])
                    ? frontier[center + diagonal + 1]
                    : frontier[center + diagonal - 1] + 1;
                int y = x - diagonal;

                while (x < length && y < otherLength && comparer.Equals(first[offset + x], second[offset + y]))
                {
                    x++;
                    y++;
                }

                frontier[center + diagonal] = x;
                if (x >= length && y >= otherLength)
                {
                    trace.Add(Snapshot(frontier, center, edits));
                    Backtrack(trace, length, otherLength, offset, map);
                    return;
                }
            }

            trace.Add(Snapshot(frontier, center, edits));
        }

        // Trop de différences : la partie centrale est tenue pour remplacée.
    }

    /// <summary>
    /// Relève la frontière atteinte après un nombre donné de différences.
    /// </summary>
    /// <param name="frontier">Frontière courante, indexée par diagonale</param>
    /// <param name="center">Rang de la diagonale zéro</param>
    /// <param name="edits">Nombre de différences</param>
    /// <returns>Les valeurs des diagonales -edits à +edits</returns>
    private static int[] Snapshot(int[] frontier, int center, int edits)
    {
        int[] copy = new int[(2 * edits) + 1];
        Array.Copy(frontier, center - edits, copy, 0, copy.Length);
        return copy;
    }

    /// <summary>
    /// Remonte le chemin trouvé et note les lignes communes.
    /// </summary>
    /// <param name="trace">Frontières successives</param>
    /// <param name="length">Longueur de la partie centrale du premier texte</param>
    /// <param name="otherLength">Longueur de la partie centrale du second texte</param>
    /// <param name="offset">Rang de la première ligne de la partie centrale</param>
    /// <param name="map">Correspondances, complétées ici</param>
    private static void Backtrack(List<int[]> trace, int length, int otherLength, int offset, int[] map)
    {
        int x = length;
        int y = otherLength;

        for (int edits = trace.Count - 1; edits > 0; edits--)
        {
            int[] previous = trace[edits - 1];
            int diagonal = x - y;

            // La frontière précédente couvre les diagonales -(edits-1) à edits-1.
            int ValueAt(int k) => previous[k + edits - 1];
            int previousDiagonal = diagonal == -edits || (diagonal != edits && ValueAt(diagonal - 1) < ValueAt(diagonal + 1))
                ? diagonal + 1
                : diagonal - 1;
            int previousX = ValueAt(previousDiagonal);
            int previousY = previousX - previousDiagonal;

            while (x > previousX && y > previousY)
            {
                x--;
                y--;
                map[offset + x] = offset + y;
            }

            x = previousX;
            y = previousY;
        }

        // Sans aucune différence avant ce point, le reste est une diagonale.
        while (x > 0 && y > 0)
        {
            x--;
            y--;
            map[offset + x] = offset + y;
        }
    }
}
