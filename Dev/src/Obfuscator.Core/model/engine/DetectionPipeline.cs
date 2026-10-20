using Obfuscator.Core.Model.Detectors;
using Obfuscator.Core.Model.Mapping;
using Obfuscator.Core.Model.Policy;

namespace Obfuscator.Core.Model.Engine;

/// <summary>
/// Phases de détection communes à l'obfuscation, au relevé et à l'aperçu.
///
/// Les trois opérations doivent voir exactement les mêmes valeurs, sinon
/// l'aperçu que l'utilisateur valide à l'écran ne correspondrait pas à ce que
/// l'obfuscation remplace ensuite. Ces phases sont donc écrites une fois et
/// partagées : gel des jetons déjà présents, collecte auprès des détecteurs,
/// filtrage par la politique, retrait des occurrences exceptées, arbitrage des
/// chevauchements.
/// </summary>
/// <param name="format">Format des jetons du projet</param>
/// <param name="detectors">Règles de détection activées</param>
/// <param name="policy">Politique d'obfuscation</param>
public sealed class DetectionPipeline(TokenFormat format, IEnumerable<IDetector> detectors, ObfuscationPolicy policy)
{
    private readonly List<IDetector> _detectors = [.. detectors];

    /// <summary>
    /// Exécute les phases de détection sur un texte.
    /// </summary>
    /// <param name="text">Texte source, jamais modifié</param>
    /// <param name="fileName">Nom du fichier analysé, pour les rapports et les exceptions</param>
    /// <returns>Les spans retenus et le journal des arbitrages</returns>
    public SpanResolution Run(string text, string? fileName = null)
    {
        return Run(text, fileName, CancellationToken.None);
    }

    /// <summary>
    /// Exécute les phases de détection sur un texte, en s'interrompant si
    /// l'annulation est demandée.
    ///
    /// L'annulation est examinée entre deux détecteurs : c'est le grain le plus
    /// fin qui ne coûte rien, une expression régulière en cours ne pouvant pas
    /// être interrompue proprement. Sur un gros journal, cela suffit à rendre
    /// la main en une fraction de seconde.
    /// </summary>
    /// <param name="text">Texte source, jamais modifié</param>
    /// <param name="fileName">Nom du fichier analysé, pour les rapports et les exceptions</param>
    /// <param name="cancellationToken">Jeton d'annulation</param>
    /// <returns>Les spans retenus et le journal des arbitrages</returns>
    /// <exception cref="OperationCanceledException">Si l'annulation a été demandée</exception>
    public SpanResolution Run(string text, string? fileName, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(text);
        return Run(new DetectionContext(text, fileName), cancellationToken);
    }

    /// <summary>
    /// Exécute les phases de détection dans un contexte préparé par
    /// l'appelant — celui d'un nom de fichier, par exemple, que l'on ne veut
    /// pas voir lu comme un document.
    /// </summary>
    /// <param name="context">Contexte d'analyse, texte source compris</param>
    /// <param name="cancellationToken">Jeton d'annulation</param>
    /// <returns>Les spans retenus et le journal des arbitrages</returns>
    /// <exception cref="OperationCanceledException">Si l'annulation a été demandée</exception>
    public SpanResolution Run(DetectionContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        string text = context.Text;
        string? fileName = context.FileName;
        List<DetectedSpan> candidates = [.. format.FindProtectedSpans(text)];

        foreach (IDetector detector in _detectors)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Le filtrage par la politique a lieu avant l'arbitrage : une valeur
            // conservée volontairement ne doit pas pouvoir évincer un span
            // voisin qui, lui, devait être masqué.
            candidates.AddRange(detector.Detect(context).Where(policy.ShouldObfuscate));
        }

        cancellationToken.ThrowIfCancellationRequested();
        return SpanResolver.Resolve(RemoveExcepted(SpareEntityIdentifiers(candidates, text), text, fileName));
    }

    /// <summary>
    /// Écarte les portions qui ne tiennent qu'à des identifiants d'entités
    /// gardés en clair, sauf celles qu'on a explicitement demandé de masquer.
    ///
    /// Le filtre a lieu avant l'arbitrage : l'identifiant entier repéré par une
    /// règle générale (la clé <c>entity_id</c>) l'emporterait sinon sur le terme
    /// du vocabulaire qu'il contient, et rien de lui ne resterait lisible.
    /// </summary>
    /// <param name="candidates">Spans retenus par la politique</param>
    /// <param name="text">Texte source</param>
    /// <returns>Les spans à garder</returns>
    private List<DetectedSpan> SpareEntityIdentifiers(List<DetectedSpan> candidates, string text)
    {
        if (policy.Entities.IsEmpty)
        {
            return candidates;
        }

        EntityMask mask = policy.Entities.Find(text);
        return mask.IsEmpty
            ? candidates
            : [.. candidates.Where(span => policy.IsExplicitlyMasked(span) || !mask.Covers(span.Start, span.Length))];
    }

    /// <summary>
    /// Écarte les occurrences que l'utilisateur a exceptées à un endroit précis :
    /// la valeur, à sa ligne, et à son rang sur la ligne.
    ///
    /// Le calcul des numéros de ligne n'a lieu que si des exceptions existent :
    /// indexer le texte à chaque analyse coûterait pour rien dans le cas
    /// courant, où il n'y en a aucune.
    /// </summary>
    /// <param name="candidates">Spans retenus par la politique</param>
    /// <param name="text">Texte source</param>
    /// <param name="fileName">Nom du fichier analysé</param>
    /// <returns>Les spans à soumettre à l'arbitrage</returns>
    private List<DetectedSpan> RemoveExcepted(List<DetectedSpan> candidates, string text, string? fileName)
    {
        if (!policy.HasLineExemptions || string.IsNullOrEmpty(fileName))
        {
            return candidates;
        }

        TextLines lines = new(text);
        List<DetectedSpan> kept = new(candidates.Count);

        foreach (DetectedSpan span in candidates)
        {
            if (span.Category == SpanCategory.ProtectedToken
                || !policy.IsExceptedAt(fileName, lines.LineOf(span.Start), text, span.Start, span.RawValue))
            {
                kept.Add(span);
            }
        }

        return kept;
    }
}
