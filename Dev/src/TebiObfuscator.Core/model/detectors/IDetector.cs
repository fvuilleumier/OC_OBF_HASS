using TebiObfuscator.Core.Model.Engine;
using TebiObfuscator.Core.Model.Documents;

namespace TebiObfuscator.Core.Model.Detectors;

/// <summary>
/// Contexte fourni aux détecteurs. Il transporte le texte source dans son état
/// d'origine — jamais une version partiellement substituée — ce qui est la
/// condition pour que les conflits entre règles soient arbitrés dans une phase
/// dédiée plutôt que par l'ordre d'exécution des détecteurs.
/// </summary>
/// <param name="text">Texte source complet</param>
/// <param name="fileName">Nom du fichier analysé, pour les rapports</param>
public sealed class DetectionContext(string text, string? fileName = null)
{
    /// <summary>Texte source, inchangé pendant toute la phase de détection.</summary>
    public string Text { get; } = text ?? throw new ArgumentNullException(nameof(text));

    /// <summary>Nom du fichier analysé, s'il est connu.</summary>
    public string? FileName { get; } = fileName;

    /// <summary>
    /// Nature devinée du fichier. Elle n'oriente que les détecteurs
    /// structurels : les règles textuelles s'appliquent à tout, quelle que soit
    /// la nature reconnue, afin qu'un format mal deviné n'ouvre pas de brèche.
    /// </summary>
    public FileKind Kind { get; init; } = FormatSniffer.Detect(fileName, text ?? string.Empty);

    /// <summary>
    /// Vrai quand le texte est un nom de fichier ou de dossier, et non le
    /// contenu d'un fichier. Un nom n'a ni clés ni valeurs, et une longue suite
    /// de chiffres y est un horodatage bien plus souvent qu'une carte de
    /// paiement : les règles qui n'ont de sens que dans un contenu s'abstiennent.
    /// </summary>
    public bool IsName { get; init; }
}

/// <summary>
/// Règle de détection : elle décrit ce qu'elle voit dans le texte source sans
/// jamais le modifier. Toutes les règles reçoivent le même texte et travaillent
/// indépendamment les unes des autres ; la cohérence de l'ensemble est
/// rétablie ensuite par la résolution des chevauchements.
/// </summary>
public interface IDetector
{
    /// <summary>Identifiant stable de la règle, utilisé dans les rapports et le journal d'arbitrage.</summary>
    string RuleId { get; }

    /// <summary>
    /// Recense les valeurs sensibles présentes dans le texte.
    /// </summary>
    /// <param name="context">Contexte d'analyse, texte source compris</param>
    /// <returns>Les spans repérés, dans un ordre quelconque</returns>
    IEnumerable<DetectedSpan> Detect(DetectionContext context);

    /// <summary>
    /// Vrai si la règle a un sens sur un nom de fichier ou de dossier. Les
    /// règles fondées sur une clé ou sur la structure d'un document n'en ont
    /// pas : un nom n'a ni l'une ni l'autre.
    /// </summary>
    bool AppliesToNames => true;
}
