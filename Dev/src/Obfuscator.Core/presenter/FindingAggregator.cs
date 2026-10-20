using Obfuscator.Core.Model.Engine;
using Obfuscator.Core.Model.Mapping;
using Obfuscator.Core.Model.Reporting;

namespace Obfuscator.Core.Presenter;

/// <summary>
/// Regroupe les spans repérés dans un lot en valeurs distinctes, telles que la
/// revue les présente.
///
/// Deux graphies d'une même valeur — <c>OBFCOM.CH</c> et <c>obfcom.ch</c> —
/// forment une seule ligne, parce qu'elles recevront le même pseudonyme : la
/// revue doit montrer les décisions à prendre, pas les variations d'écriture.
/// Le relevé et l'analyse complète partagent ce regroupement, pour que la
/// ligne de commande et la fenêtre comptent exactement la même chose.
/// </summary>
internal sealed class FindingAggregator
{
    private readonly Dictionary<(SpanCategory Category, string Value), Accumulator> _accumulators = [];

    /// <summary>
    /// Ajoute les spans retenus dans un fichier.
    /// </summary>
    /// <param name="spans">Spans qui seront remplacés</param>
    /// <param name="fileName">Nom du fichier concerné</param>
    public void Add(IEnumerable<DetectedSpan> spans, string fileName)
    {
        foreach (DetectedSpan span in spans)
        {
            string canonical = ValueCanonicalizer.Canonicalize(span.Category, span.RawValue);
            (SpanCategory, string) key = (span.Category, canonical);

            if (!_accumulators.TryGetValue(key, out Accumulator? accumulator))
            {
                accumulator = new Accumulator(span.RawValue, span.RuleId);
                _accumulators[key] = accumulator;
            }

            accumulator.Occurrences++;
            accumulator.Files.Add(fileName);
        }
    }

    /// <summary>
    /// Produit les valeurs distinctes, de la plus fréquente à la moins fréquente.
    /// </summary>
    /// <returns>Les valeurs regroupées</returns>
    public List<ScanFinding> ToFindings()
    {
        List<ScanFinding> findings = [];
        foreach (((SpanCategory category, _), Accumulator accumulator) in _accumulators)
        {
            findings.Add(new ScanFinding(
                category,
                accumulator.Sample,
                SensitiveText.Mask(accumulator.Sample),
                accumulator.Occurrences,
                [.. accumulator.Files.OrderBy(name => name, StringComparer.OrdinalIgnoreCase)],
                accumulator.RuleId));
        }

        return [.. findings
            .OrderByDescending(finding => finding.Occurrences)
            .ThenBy(finding => finding.Category)
            .ThenBy(finding => finding.Value, StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>
    /// Accumulateur des occurrences d'une même valeur canonique.
    /// </summary>
    /// <param name="sample">Première graphie rencontrée</param>
    /// <param name="ruleId">Règle l'ayant repérée en premier</param>
    private sealed class Accumulator(string sample, string ruleId)
    {
        /// <summary>Graphie retenue pour l'affichage.</summary>
        public string Sample { get; } = sample;

        /// <summary>Règle l'ayant repérée en premier.</summary>
        public string RuleId { get; } = ruleId;

        /// <summary>Nombre d'occurrences cumulées.</summary>
        public int Occurrences { get; set; }

        /// <summary>Fichiers où la valeur apparaît.</summary>
        public HashSet<string> Files { get; } = new(StringComparer.OrdinalIgnoreCase);
    }
}
