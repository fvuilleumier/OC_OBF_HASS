namespace MHassnage.Core.Model;

/// <summary>
/// Marche à suivre pour (re)créer le jeton Hass : affichée quand il manque,
/// et quand Hass le refuse (expiré ou révoqué).
/// </summary>
public static class HassTokenHelp
{
    public static string Text(string hassUrl, string settingsPath) => $"""
        Le jeton d'accès Home Assistant est absent, expiré ou révoqué.
        Pour en créer un nouveau :

          1. Ouvrir Hass dans le navigateur ({hassUrl}).
          2. Cliquer sur son nom d'utilisateur, en bas à gauche (page /profile).
          3. Ouvrir l'onglet « Sécurité » (/profile/security).
          4. Dans « Jetons d'accès longue durée », cliquer sur « Créer un jeton ».
          5. Le nommer (par exemple « MHassnage ») et valider.
          6. Copier le jeton tout de suite : Hass ne l'affiche qu'une seule fois.

        Le coller ensuite en clair dans :
          {settingsPath}
        à la clé "Hass": {"{"} "Token": "…" {"}"}.
        MHassnage le chiffrera au lancement suivant.
        Penser à révoquer l'ancien jeton sur la même page s'il existe encore.
        """;
}
