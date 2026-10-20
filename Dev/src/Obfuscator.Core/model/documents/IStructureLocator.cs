namespace Obfuscator.Core.Model.Documents;

/// <summary>
/// Valeur scalaire localisée dans un document structuré.
/// </summary>
/// <param name="Start">Index de début de la valeur, en unités UTF-16</param>
/// <param name="Length">Longueur de la valeur, en unités UTF-16</param>
/// <param name="Path">Chemin structurel, par exemple <c>$.db.password</c></param>
/// <param name="KeyName">Nom de la clé portant la valeur</param>
/// <param name="IsQuoted">Vrai si la valeur est délimitée par des guillemets dans la source</param>
/// <param name="IsNumeric">Vrai si la valeur est un scalaire numérique nu</param>
/// <param name="SpansSeveralLines">Vrai si la valeur s'étend sur plusieurs lignes</param>
public sealed record ValueLocation(
    int Start,
    int Length,
    string Path,
    string KeyName,
    bool IsQuoted,
    bool IsNumeric,
    bool SpansSeveralLines);

/// <summary>
/// Localise les valeurs scalaires d'un document structuré, sans jamais le
/// réécrire.
///
/// Le contrat est volontairement limité à la localisation. Analyser puis
/// resérialiser un document détruirait les commentaires, l'ordre des clés, le
/// style des guillemets et l'indentation — rédhibitoire pour un JSON commenté ou
/// un YAML annoté, et suffisant pour que l'utilisateur cesse de faire confiance
/// à l'outil en voyant le diff. On ne retient donc du lecteur que des positions,
/// et la substitution reste purement textuelle.
/// </summary>
public interface IStructureLocator
{
    /// <summary>
    /// Indique si ce localisateur sait traiter un format.
    /// </summary>
    /// <param name="kind">Nature du fichier</param>
    /// <returns><c>true</c> si le format est pris en charge</returns>
    bool CanHandle(FileKind kind);

    /// <summary>
    /// Localise les valeurs scalaires d'un document.
    /// </summary>
    /// <param name="text">Contenu du document</param>
    /// <returns>Les valeurs localisées, ou une liste vide si le document ne s'analyse pas</returns>
    IReadOnlyList<ValueLocation> Locate(string text);
}
