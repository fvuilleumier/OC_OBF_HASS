using TebiObfuscator.Core.Model.Engine;
using TebiObfuscator.Core.Model.Policy;

namespace TebiObfuscator.Core.Presenter;

/// <summary>
/// Compte rendu d'une restitution.
/// </summary>
/// <param name="Text">Texte restitué</param>
/// <param name="Substitutions">Pseudonymes remplacés par leur valeur réelle</param>
/// <param name="UnknownTokens">Jetons rencontrés mais absents du coffre</param>
public sealed record DeobfuscationReport(
    string Text,
    IReadOnlyList<TokenSubstitution> Substitutions,
    IReadOnlyList<UnknownToken> UnknownTokens);

/// <summary>
/// Restitue les valeurs réelles dans un texte ou un fichier.
///
/// Le compte rendu fait partie du livrable et non du confort. Les pseudonymes
/// typés étant des valeurs plausibles, il reste concevable qu'un modèle en
/// écrive un spontanément, sans rapport avec le fichier d'origine ; c'est la
/// lecture du relevé des substitutions qui permet de s'en apercevoir. Les
/// jetons inconnus sont signalés au même titre, mais laissés en place : un
/// modèle à qui l'on demande d'imaginer trois serveurs de plus en inventera
/// légitimement.
/// </summary>
/// <param name="project">Projet dont le coffre sert à la restitution</param>
public sealed class DeobfuscatePresenter(ObfuscationProject project)
{
    /// <summary>
    /// Restitue un texte, typiquement une réponse de modèle collée par
    /// l'utilisateur.
    /// </summary>
    /// <param name="text">Texte contenant des pseudonymes</param>
    /// <returns>Le compte rendu de la restitution</returns>
    public DeobfuscationReport RunOnText(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        DeobfuscationResult result = project.CreateDeobfuscationEngine().Deobfuscate(text);
        return new DeobfuscationReport(result.Text, result.Substitutions, result.UnknownTokens);
    }

    /// <summary>
    /// Restitue les valeurs réelles dans un fragment HTML, par exemple la
    /// version mise en forme d'une réponse copiée depuis une interface de
    /// conversation. Les valeurs restituées sont encodées pour le HTML ; la mise
    /// en forme est conservée telle quelle.
    /// </summary>
    /// <param name="html">Fragment HTML contenant des pseudonymes</param>
    /// <returns>Le fragment restitué et le compte rendu des substitutions</returns>
    public DeobfuscationReport RunOnHtml(string html)
    {
        ArgumentNullException.ThrowIfNull(html);

        DeobfuscationResult result = project.CreateDeobfuscationEngine().Deobfuscate(html, System.Net.WebUtility.HtmlEncode);
        return new DeobfuscationReport(result.Text, result.Substitutions, result.UnknownTokens);
    }

    /// <summary>
    /// Restitue un fichier et écrit le résultat.
    /// </summary>
    /// <param name="sourcePath">Fichier contenant des pseudonymes</param>
    /// <param name="outputPath">Fichier à produire</param>
    /// <returns>Le compte rendu de la restitution</returns>
    public DeobfuscationReport RunOnFile(string sourcePath, string outputPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);

        SourceDocument document = SourceDocument.Load(File.ReadAllBytes(sourcePath));
        DeobfuscationReport report = RunOnText(document.Text);

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath))!);
        File.WriteAllBytes(outputPath, document.ToBytes(report.Text));

        return report;
    }
}
