namespace TebiObfuscator.Core.Model.Engine;

/// <summary>
/// Portion de texte repérée par un détecteur, exprimée en offsets sur le texte
/// source d'origine. C'est l'unique monnaie d'échange entre les détecteurs et
/// le moteur : un détecteur ne modifie jamais le texte, il décrit ce qu'il a vu.
///
/// Les offsets sont toujours exprimés en unités UTF-16 (index dans une
/// <see cref="string"/> .NET), y compris pour les détecteurs qui s'appuient sur
/// un lecteur indexant en octets : la conversion a lieu chez eux, pas ici.
/// </summary>
/// <param name="Start">Index de début, inclus, dans le texte source</param>
/// <param name="Length">Longueur en unités UTF-16</param>
/// <param name="Category">Nature de la donnée repérée</param>
/// <param name="RawValue">Texte exact couvert par le span, tel qu'il figure dans la source</param>
/// <param name="Priority">Priorité d'arbitrage en cas de chevauchement</param>
/// <param name="Confidence">Confiance du détecteur, dans l'intervalle [0, 1]</param>
/// <param name="RuleId">Identifiant de la règle émettrice, pour la traçabilité</param>
/// <param name="Path">Chemin structurel de la valeur (<c>$.db.password</c>), si connu</param>
public sealed record DetectedSpan(
    int Start,
    int Length,
    SpanCategory Category,
    string RawValue,
    int Priority,
    double Confidence,
    string RuleId,
    string? Path = null)
{
    /// <summary>Index de fin, exclu.</summary>
    public int End => Start + Length;

    /// <summary>
    /// Crée un span en dérivant la priorité de la catégorie, cas de loin le
    /// plus fréquent côté détecteurs.
    /// </summary>
    /// <param name="start">Index de début dans le texte source</param>
    /// <param name="length">Longueur en unités UTF-16</param>
    /// <param name="category">Nature de la donnée repérée</param>
    /// <param name="rawValue">Texte exact couvert par le span</param>
    /// <param name="ruleId">Identifiant de la règle émettrice</param>
    /// <param name="confidence">Confiance du détecteur, 1 par défaut</param>
    /// <param name="path">Chemin structurel de la valeur, si connu</param>
    /// <returns>Le span correspondant, doté de la priorité par défaut de sa catégorie</returns>
    public static DetectedSpan Create(
        int start,
        int length,
        SpanCategory category,
        string rawValue,
        string ruleId,
        double confidence = 1.0,
        string? path = null)
    {
        return new DetectedSpan(start, length, category, rawValue, category.DefaultPriority(), confidence, ruleId, path);
    }

    /// <summary>
    /// Indique si ce span recouvre, même partiellement, le span fourni.
    /// </summary>
    /// <param name="other">Span à confronter</param>
    /// <returns><c>true</c> si les deux intervalles ont au moins une position commune</returns>
    public bool Overlaps(DetectedSpan other)
    {
        ArgumentNullException.ThrowIfNull(other);
        return Start < other.End && other.Start < End;
    }

    /// <summary>
    /// Indique si ce span contient entièrement le span fourni.
    /// </summary>
    /// <param name="other">Span à confronter</param>
    /// <returns><c>true</c> si <paramref name="other"/> est inclus dans ce span</returns>
    public bool Contains(DetectedSpan other)
    {
        ArgumentNullException.ThrowIfNull(other);
        return Start <= other.Start && other.End <= End;
    }
}
