namespace TebiObfuscator.Core.Model.Mapping;

/// <summary>
/// Protège le coffre de mapping avant son écriture sur disque.
///
/// Le coffre associe chaque pseudonyme à la valeur réelle correspondante,
/// secrets compris : c'est le fichier le plus sensible du système, davantage
/// que les sources elles-mêmes. L'abstraction existe pour deux raisons : garder
/// le cœur métier testable sans dépendre d'une API propre à Windows, et offrir
/// plusieurs chiffrements — la clé de l'application pour le chiffrement bas,
/// DPAPI pour le chiffrement élevé, demain une phrase de passe — sans toucher
/// au moteur. L'en-tête qui dit lequel a servi est l'affaire de
/// <see cref="VaultProtectors"/>, pas des protections elles-mêmes.
/// </summary>
public interface IMappingProtector
{
    /// <summary>Nom court de la protection, affiché dans les rapports.</summary>
    string Name { get; }

    /// <summary>
    /// Protège le contenu sérialisé du coffre.
    /// </summary>
    /// <param name="plaintext">Contenu en clair</param>
    /// <returns>Le contenu protégé, tel qu'il doit être écrit sur disque</returns>
    byte[] Protect(byte[] plaintext);

    /// <summary>
    /// Restitue le contenu en clair d'un coffre protégé.
    /// </summary>
    /// <param name="payload">Contenu lu sur disque</param>
    /// <returns>Le contenu en clair</returns>
    byte[] Unprotect(byte[] payload);
}
