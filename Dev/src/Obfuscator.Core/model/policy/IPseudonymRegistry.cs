namespace Obfuscator.Core.Model.Policy;

/// <summary>
/// Reconnaît les pseudonymes déjà attribués par le projet.
///
/// C'est le second pilier de l'idempotence, à côté du gel des jetons neutres.
/// Un pseudonyme format-préservant ressemble par construction à la valeur qu'il
/// remplace : une adresse MAC de substitution reste une adresse MAC, et les
/// détecteurs la repèrent donc au passage suivant. Sans cette reconnaissance,
/// repasser un fichier déjà traité lui attribuerait un second pseudonyme, et la
/// chaîne de restitution se romprait.
///
/// Certains pseudonymes échappent à ce besoin parce qu'ils vivent dans une
/// plage réservée que la liste blanche connaît déjà — adresses de la plage
/// d'essai, domaines en <c>.invalid</c>. La reconnaissance couvre les autres, et
/// vaut pour toute stratégie ajoutée par la suite.
/// </summary>
public interface IPseudonymRegistry
{
    /// <summary>
    /// Indique si une valeur est un pseudonyme déjà attribué.
    /// </summary>
    /// <param name="value">Valeur repérée dans un fichier</param>
    /// <returns><c>true</c> si la valeur est une production de l'outil</returns>
    bool IsPseudonym(string value);
}
