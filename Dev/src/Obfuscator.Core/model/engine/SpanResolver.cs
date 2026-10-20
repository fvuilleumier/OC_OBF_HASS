namespace Obfuscator.Core.Model.Engine;

/// <summary>
/// Raison pour laquelle un span candidat a été écarté au profit d'un autre.
/// </summary>
public enum EvictionReason
{
    /// <summary>Les deux spans couvrent exactement le même intervalle.</summary>
    DuplicateRange,

    /// <summary>Le perdant était entièrement contenu dans le gagnant.</summary>
    Contained,

    /// <summary>Les deux spans se chevauchaient partiellement.</summary>
    CrossOverlap,
}

/// <summary>
/// Trace d'un arbitrage entre deux spans. Conservée pour permettre de
/// comprendre pourquoi une règle n'a rien produit sur un fichier donné :
/// sans cette trace, régler les détecteurs relève de la divination.
/// </summary>
/// <param name="Loser">Span écarté</param>
/// <param name="Winner">Span retenu</param>
/// <param name="Reason">Nature du conflit</param>
public sealed record SpanEviction(DetectedSpan Loser, DetectedSpan Winner, EvictionReason Reason);

/// <summary>
/// Résultat de la phase de résolution : les spans retenus, deux à deux
/// disjoints et triés par position croissante, et le journal des arbitrages.
/// </summary>
/// <param name="Accepted">Spans retenus, y compris les zones gelées</param>
/// <param name="Evictions">Journal des spans écartés</param>
public sealed record SpanResolution(IReadOnlyList<DetectedSpan> Accepted, IReadOnlyList<SpanEviction> Evictions)
{
    /// <summary>
    /// Spans effectivement à remplacer : les zones gelées (jetons déjà présents
    /// dans la source) sont retirées, car leur seul rôle est de protéger leur
    /// intervalle contre les autres détecteurs.
    /// </summary>
    public IReadOnlyList<DetectedSpan> Replacements
    {
        get
        {
            List<DetectedSpan> replacements = new(Accepted.Count);
            foreach (DetectedSpan span in Accepted)
            {
                if (span.Category != SpanCategory.ProtectedToken)
                {
                    replacements.Add(span);
                }
            }

            return replacements;
        }
    }
}

/// <summary>
/// Transforme un ensemble de spans candidats — potentiellement redondants,
/// imbriqués ou en chevauchement — en une suite de spans deux à deux disjoints.
///
/// C'est le point de correction central de l'outil : une erreur ici produit soit
/// une fuite (valeur partiellement remplacée), soit un fichier corrompu.
/// Deux invariants sont tenus :
/// un span n'est jamais tronqué (il gagne entièrement ou disparaît entièrement),
/// et l'ordre de tri est total, donc le résultat est reproductible d'une
/// exécution à l'autre quel que soit l'ordre d'arrivée des détecteurs.
/// </summary>
public static class SpanResolver
{
    /// <summary>
    /// Résout les chevauchements entre spans candidats.
    /// </summary>
    /// <param name="candidates">Spans émis par l'ensemble des détecteurs, dans un ordre quelconque</param>
    /// <returns>Les spans retenus, disjoints et triés, accompagnés du journal des arbitrages</returns>
    public static SpanResolution Resolve(IEnumerable<DetectedSpan> candidates)
    {
        ArgumentNullException.ThrowIfNull(candidates);

        List<DetectedSpan> ordered = [.. candidates];
        ordered.Sort(ArbitrationOrder.Instance);

        List<DetectedSpan> accepted = new(ordered.Count);
        List<SpanEviction> evictions = [];

        foreach (DetectedSpan candidate in ordered)
        {
            // Les spans déjà retenus étant disjoints et triés, le seul conflit
            // possible est avec le dernier d'entre eux : tous les autres se
            // terminent avant lui.
            if (accepted.Count == 0)
            {
                accepted.Add(candidate);
                continue;
            }

            DetectedSpan last = accepted[^1];
            if (!candidate.Overlaps(last))
            {
                accepted.Add(candidate);
                continue;
            }

            // Priorité strictement supérieure exigée pour déloger le span déjà
            // retenu : à égalité, c'est l'ordre de tri (donc le plus long, puis
            // le plus prioritaire) qui a déjà tranché.
            if (candidate.Priority > last.Priority)
            {
                accepted[^1] = candidate;
                evictions.Add(new SpanEviction(last, candidate, ClassifyConflict(last, candidate)));
            }
            else
            {
                evictions.Add(new SpanEviction(candidate, last, ClassifyConflict(candidate, last)));
            }
        }

        return new SpanResolution(accepted, evictions);
    }

    /// <summary>
    /// Qualifie la nature d'un conflit entre deux spans, pour le journal.
    /// </summary>
    /// <param name="loser">Span écarté</param>
    /// <param name="winner">Span retenu</param>
    /// <returns>La raison correspondant à la géométrie des deux intervalles</returns>
    private static EvictionReason ClassifyConflict(DetectedSpan loser, DetectedSpan winner)
    {
        if (loser.Start == winner.Start && loser.End == winner.End)
        {
            return EvictionReason.DuplicateRange;
        }

        return winner.Contains(loser) || loser.Contains(winner)
            ? EvictionReason.Contained
            : EvictionReason.CrossOverlap;
    }

    /// <summary>
    /// Ordre total et déterministe des spans candidats : position croissante,
    /// puis span le plus long, puis le plus prioritaire, puis le plus sûr, et
    /// enfin des critères purement lexicaux qui garantissent qu'aucune paire ne
    /// reste ex aequo. Sans ce dernier étage, deux exécutions successives
    /// pourraient produire des fichiers différents.
    /// </summary>
    private sealed class ArbitrationOrder : IComparer<DetectedSpan>
    {
        /// <summary>Instance partagée : le comparateur est sans état.</summary>
        public static readonly ArbitrationOrder Instance = new();

        /// <summary>
        /// Compare deux spans selon l'ordre d'arbitrage.
        /// </summary>
        /// <param name="x">Premier span</param>
        /// <param name="y">Second span</param>
        /// <returns>Un entier négatif, nul ou positif selon l'ordre relatif</returns>
        public int Compare(DetectedSpan? x, DetectedSpan? y)
        {
            if (ReferenceEquals(x, y))
            {
                return 0;
            }

            if (x is null)
            {
                return -1;
            }

            if (y is null)
            {
                return 1;
            }

            int byStart = x.Start.CompareTo(y.Start);
            if (byStart != 0)
            {
                return byStart;
            }

            int byLength = y.Length.CompareTo(x.Length);
            if (byLength != 0)
            {
                return byLength;
            }

            int byPriority = y.Priority.CompareTo(x.Priority);
            if (byPriority != 0)
            {
                return byPriority;
            }

            int byConfidence = y.Confidence.CompareTo(x.Confidence);
            if (byConfidence != 0)
            {
                return byConfidence;
            }

            int byCategory = x.Category.CompareTo(y.Category);
            if (byCategory != 0)
            {
                return byCategory;
            }

            return string.CompareOrdinal(x.RuleId, y.RuleId);
        }
    }
}
