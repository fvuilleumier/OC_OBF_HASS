using Obfuscator.Core.Model.Engine;
using Obfuscator.Core.Model.Mapping;
using Obfuscator.Core.Model.Policy;

namespace Obfuscator.Core.Presenter;

/// <summary>
/// Résultat du contrôle d'un fichier.
/// </summary>
/// <param name="Path">Fichier contrôlé</param>
/// <param name="Residuals">Valeurs réelles du coffre retrouvées dans le fichier</param>
public sealed record VerifiedFile(string Path, IReadOnlyList<ResidualLiteral> Residuals);

/// <summary>
/// Résultat d'un contrôle de lot.
/// </summary>
/// <param name="Files">Contrôles par fichier</param>
/// <param name="IsClean">Vrai si aucune valeur réelle n'a été retrouvée</param>
public sealed record VerifyReport(IReadOnlyList<VerifiedFile> Files, bool IsClean);

/// <summary>
/// Contrôle qu'aucune valeur réelle connue du coffre ne subsiste dans des
/// fichiers censés être obfusqués.
///
/// C'est un contrôle indépendant de la chaîne qui a produit ces fichiers, et
/// c'est tout son intérêt : il ne fait aucune hypothèse sur les détecteurs, la
/// résolution des chevauchements ou les offsets, il se contente de chercher les
/// valeurs réelles. Il détecte donc exactement la classe de défauts la plus
/// grave — une fuite qui a traversé une chaîne apparemment saine.
/// </summary>
/// <param name="project">Projet dont le coffre sert de référence</param>
public sealed class VerifyPresenter(ObfuscationProject project)
{
    /// <summary>
    /// Contrôle un ensemble de fichiers.
    /// </summary>
    /// <param name="filePaths">Fichiers à contrôler</param>
    /// <returns>Le compte rendu du contrôle</returns>
    public VerifyReport Run(IEnumerable<string> filePaths)
    {
        ArgumentNullException.ThrowIfNull(filePaths);

        List<VerifiedFile> files = [];
        bool clean = true;

        foreach (string path in filePaths)
        {
            SourceDocument document = SourceDocument.Load(File.ReadAllBytes(path));
            IReadOnlyList<ResidualLiteral> residuals = MappingVerifier.FindResiduals(document.Text, project.Store);

            files.Add(new VerifiedFile(path, residuals));
            clean &= residuals.Count == 0;
        }

        return new VerifyReport(files, clean);
    }
}
