using Obfuscator.Core.Model.Mapping;
using Obfuscator.Core.Model.Policy;

namespace Obfuscator.Cli.View;

/// <summary>
/// Retrouve le projet auquel une commande s'applique.
///
/// La recherche remonte l'arborescence depuis le dossier courant, à la manière
/// d'un dépôt de code : on peut ainsi lancer une commande depuis n'importe quel
/// sous-dossier du projet sans avoir à préciser sa racine.
/// </summary>
internal static class ProjectLocator
{
    /// <summary>
    /// Ouvre le projet contenant un chemin donné.
    /// </summary>
    /// <param name="startPath">Chemin de départ, dossier courant si absent</param>
    /// <returns>Le projet ouvert</returns>
    /// <exception cref="DirectoryNotFoundException">Si aucun projet n'est trouvé en remontant</exception>
    public static ObfuscationProject Open(string? startPath = null)
    {
        string root = FindRoot(startPath ?? Directory.GetCurrentDirectory());
        return ObfuscationProject.Open(root, VaultProtectors.CreateDefault());
    }

    /// <summary>
    /// Remonte l'arborescence à la recherche du dossier de projet.
    /// </summary>
    /// <param name="startPath">Chemin de départ</param>
    /// <returns>La racine du projet</returns>
    /// <exception cref="DirectoryNotFoundException">Si aucun projet n'est trouvé</exception>
    private static string FindRoot(string startPath)
    {
        DirectoryInfo? current = File.Exists(startPath)
            ? new FileInfo(startPath).Directory
            : new DirectoryInfo(startPath);

        while (current is not null)
        {
            if (ObfuscationProject.IsProjectFolder(current.FullName))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        throw new DirectoryNotFoundException(
            $"Aucun projet Obfuscator trouvé depuis « {startPath} ». Lancez d'abord « obfcli init ».");
    }
}
