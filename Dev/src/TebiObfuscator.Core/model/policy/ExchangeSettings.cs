using System.Text.Json.Serialization;

namespace TebiObfuscator.Core.Model.Policy;

/// <summary>
/// Liens d'un projet avec les dossiers qu'il fait communiquer : un dossier en
/// clair, la source de l'obfuscation, et un dossier où travaille l'IA, sa
/// cible.
///
/// Les deux liens sont facultatifs et indépendants. Sans dossier en clair, la
/// source est le dossier « in » du projet ; sans dossier pour l'IA, la cible
/// est « out ». Les chemins ne vivent qu'ici, dans le projet : jamais dans l'un
/// des deux dossiers, pour que rien, du côté de l'IA, ne mène au clair.
/// </summary>
[JsonObjectCreationHandling(JsonObjectCreationHandling.Populate)]
public sealed class ExchangeSettings
{
    /// <summary>Dossier en clair lié, source de l'obfuscation ; vide pour le dossier « in » du projet.</summary>
    public string ClearFolder { get; set; } = string.Empty;

    /// <summary>Dossier lié où travaille l'IA, cible de l'obfuscation ; vide pour le dossier « out » du projet.</summary>
    public string AiFolder { get; set; } = string.Empty;

    /// <summary>
    /// Chemins de la source tenus à l'écart de l'obfuscation, relatifs à la
    /// source et écrits avec des barres obliques ; un dossier se termine par
    /// une barre (<c>custom_components/</c>).
    /// </summary>
    public IList<string> Excluded { get; } = [];

    /// <summary>
    /// Chemins de la cible que le rapatriement ignore : des fichiers que
    /// l'utilisateur y garde pour lui — notes, consignes — et qui n'ont pas à
    /// rejoindre la source.
    /// </summary>
    public IList<string> Ignored { get; } = [];

    /// <summary>
    /// Vrai pour écrire aussi, dans le dossier de l'IA, un <c>CLAUDE.md</c> qui
    /// renvoie à <c>AGENTS.md</c> : c'est le fichier que lit Claude Code.
    /// </summary>
    public bool WriteClaudeMd { get; set; } = true;

    /// <summary>
    /// Recopie ces liens dans d'autres réglages.
    /// </summary>
    /// <param name="target">Liens à remplir, vides au départ</param>
    public void CopyTo(ExchangeSettings target)
    {
        ArgumentNullException.ThrowIfNull(target);

        target.ClearFolder = ClearFolder;
        target.AiFolder = AiFolder;
        target.WriteClaudeMd = WriteClaudeMd;

        target.Excluded.Clear();
        foreach (string path in Excluded)
        {
            target.Excluded.Add(path);
        }

        target.Ignored.Clear();
        foreach (string path in Ignored)
        {
            target.Ignored.Add(path);
        }
    }
}
