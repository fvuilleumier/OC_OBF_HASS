namespace TebiObfuscator.Core.Model.Mapping;

/// <summary>
/// Chiffrement du coffre d'un projet.
///
/// Le choix porte sur une seule question : qui peut ouvrir le coffre ? Le
/// coffre associe chaque pseudonyme à sa valeur réelle ; sans lui, la réponse
/// de l'IA ne peut plus être restaurée. Le chiffrement élevé le lie à un
/// compte et à un poste, au risque de le perdre avec eux ; le chiffrement bas
/// le rend transportable, au prix d'une clé que l'application porte en elle.
/// </summary>
public enum VaultProtection
{
    /// <summary>
    /// Chiffrement bas, par défaut : la clé est celle de l'application, qui
    /// ouvre le coffre sur tout poste où elle est installée.
    /// </summary>
    Low,

    /// <summary>
    /// Chiffrement élevé : DPAPI, seul le compte Windows qui a chiffré le
    /// coffre, sur son poste, peut l'ouvrir.
    /// </summary>
    High,
}
