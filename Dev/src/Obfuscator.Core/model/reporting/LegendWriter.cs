using System.Globalization;
using System.Text;
using Obfuscator.Core.Model.Engine;
using Obfuscator.Core.Model.Policy;

namespace Obfuscator.Core.Model.Reporting;

/// <summary>
/// Produit la légende qui accompagne un fichier obfusqué.
///
/// C'est ce document qui fait la différence entre un fichier caviardé et un
/// fichier exploitable. Sans lui, un modèle voit des valeurs opaques et perd
/// trois informations essentielles : que deux occurrences d'un même pseudonyme
/// désignent bien la même entité, que les valeurs conservées — masques, noms de
/// produits, comptes techniques, structure des adresses — sont authentiques et
/// peuvent servir au raisonnement, et qu'il doit répondre en réutilisant les
/// pseudonymes pour que sa réponse puisse être restituée.
/// </summary>
public static class LegendWriter
{
    /// <summary>
    /// Compose la légende d'un fichier obfusqué.
    /// </summary>
    /// <param name="project">Projet ayant produit le fichier</param>
    /// <param name="applied">Spans effectivement remplacés dans ce fichier</param>
    /// <param name="fileName">Nom du fichier obfusqué</param>
    /// <returns>Le contenu Markdown de la légende</returns>
    public static string Create(ObfuscationProject project, IReadOnlyList<DetectedSpan> applied, string fileName)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(applied);

        StringBuilder builder = new();
        builder.AppendLine(CultureInfo.InvariantCulture, $"# Légende de « {fileName} »");
        builder.AppendLine();
        builder.AppendLine("Ce fichier a été pseudonymisé avant de vous être transmis. Les valeurs");
        builder.AppendLine("identifiantes ont été remplacées ; tout le reste est authentique.");
        builder.AppendLine();

        AppendRules(builder, "ce fichier");
        AppendFamilies(builder, CountByCategory(applied), project, "Aucune valeur n'a dû être remplacée dans ce fichier.");
        AppendPreserved(builder, project);
        AppendInstruction(builder);

        return builder.ToString();
    }

    /// <summary>
    /// Compte les valeurs remplacées par nature.
    /// </summary>
    /// <param name="applied">Spans remplacés</param>
    /// <returns>Le nombre de valeurs de chaque nature</returns>
    internal static Dictionary<SpanCategory, int> CountByCategory(IEnumerable<DetectedSpan> applied)
    {
        return applied
            .GroupBy(span => span.Category)
            .ToDictionary(group => group.Key, group => group.Count());
    }

    /// <summary>
    /// Écrit les règles de lecture communes à tous les pseudonymes.
    /// </summary>
    /// <param name="builder">Tampon de construction</param>
    /// <param name="subject">Ce que lit le modèle : « ce fichier », « ces fichiers »</param>
    internal static void AppendRules(StringBuilder builder, string subject)
    {
        builder.AppendLine(CultureInfo.InvariantCulture, $"## Comment lire {subject}");
        builder.AppendLine();
        builder.AppendLine("- Deux occurrences d'un même pseudonyme désignent **la même entité réelle**,");
        builder.AppendLine("  y compris d'un fichier à l'autre du même lot.");
        builder.AppendLine("- Deux pseudonymes différents désignent **deux entités différentes**.");
        builder.AppendLine("- Les pseudonymes typés restent syntaxiquement valides : une adresse reste");
        builder.AppendLine("  une adresse, une adresse de courrier reste une adresse de courrier.");
        builder.AppendLine("- Un pseudonyme ne contient aucune information sur la valeur d'origine :");
        builder.AppendLine("  il est inutile d'essayer de la deviner.");
        builder.AppendLine();
    }

    /// <summary>
    /// Écrit le tableau des familles de pseudonymes réellement employées.
    /// </summary>
    /// <param name="builder">Tampon de construction</param>
    /// <param name="counts">Nombre de valeurs remplacées, par nature</param>
    /// <param name="project">Projet ayant produit le fichier</param>
    /// <param name="emptyMessage">Phrase écrite quand rien n'a été remplacé</param>
    internal static void AppendFamilies(
        StringBuilder builder,
        IReadOnlyDictionary<SpanCategory, int> counts,
        ObfuscationProject project,
        string emptyMessage)
    {
        if (counts.Count == 0)
        {
            builder.AppendLine(emptyMessage);
            builder.AppendLine();
            return;
        }

        builder.AppendLine("## Familles de pseudonymes présentes");
        builder.AppendLine();
        builder.AppendLine("| Forme | Signifie | Occurrences |");
        builder.AppendLine("|---|---|---|");

        foreach ((SpanCategory category, int count) in counts.OrderByDescending(entry => entry.Value))
        {
            string example = ExampleFor(category, project);
            string description = CategoryLabels.Description(category);
            builder.AppendLine(CultureInfo.InvariantCulture, $"| `{example}` | {description} | {count} |");
        }

        builder.AppendLine();
    }

    /// <summary>
    /// Écrit la liste de ce qui a été volontairement conservé. C'est la partie
    /// la plus utile au raisonnement : elle dit au modèle sur quoi il peut
    /// s'appuyer sans réserve.
    /// </summary>
    /// <param name="builder">Tampon de construction</param>
    /// <param name="project">Projet ayant produit le fichier</param>
    internal static void AppendPreserved(StringBuilder builder, ObfuscationProject project)
    {
        builder.AppendLine("## Ce qui est authentique");
        builder.AppendLine();
        builder.AppendLine("- La structure du fichier : clés, commentaires, indentation, ordre des sections.");
        builder.AppendLine("- Les masques de sous-réseau et masques génériques.");
        builder.AppendLine("- Les adresses à signification protocolaire (bouclage, diffusion, multidiffusion)");
        builder.AppendLine("  et les résolveurs publics.");
        builder.AppendLine("- Les noms de constructeurs, de produits, de protocoles et les numéros de version.");
        builder.AppendLine("- Les comptes techniques et les rôles (`admin`, `root`, `SYSTEM`…), qui ne");
        builder.AppendLine("  désignent personne en particulier.");
        builder.AppendLine("- Les ports de service normalisés (22, 443…), les chemins d'URL et les");
        builder.AppendLine("  paramètres de configuration.");

        if (project.Policy.PrivateIp == PrivateIpPolicy.Keep)
        {
            builder.AppendLine("- **Les adresses IPv4 privées**, conservées telles quelles : le plan");
            builder.AppendLine("  d'adressage interne fait partie du contexte à analyser.");
        }
        else
        {
            builder.AppendLine("- **La structure des adresses IPv4.** Les réseaux sont fictifs, mais une");
            builder.AppendLine("  adresse privée a été remplacée par une adresse privée de la même plage");
            builder.AppendLine("  (10.x, 172.16–31.x, 192.168.x) et une adresse publique par une adresse de");
            builder.AppendLine("  198.18.0.0/15 ; deux machines d'un même sous-réseau le restent, et la partie");
            builder.AppendLine("  hôte (.1, .254…) est d'origine.");
        }

        builder.AppendLine();
    }

    /// <summary>
    /// Écrit la consigne de réponse, sans laquelle la restitution échouerait.
    /// </summary>
    /// <param name="builder">Tampon de construction</param>
    private static void AppendInstruction(StringBuilder builder)
    {
        builder.AppendLine("## Consigne de réponse");
        builder.AppendLine();
        builder.AppendLine("Réutilisez les pseudonymes tels quels dans votre réponse, sans les reformater");
        builder.AppendLine("ni chercher à leur substituer des noms plus lisibles : ils sont réinjectés");
        builder.AppendLine("automatiquement dans le texte d'origine. Si vous devez introduire une entité");
        builder.AppendLine("nouvelle, nommez-la explicitement comme telle plutôt que d'inventer un");
        builder.AppendLine("pseudonyme de la même forme.");
        builder.AppendLine();
    }

    /// <summary>
    /// Fabrique un exemple représentatif de pseudonyme pour une catégorie, en
    /// réutilisant si possible une attribution réelle du coffre.
    /// </summary>
    /// <param name="category">Catégorie concernée</param>
    /// <param name="project">Projet ayant produit le fichier</param>
    /// <returns>Un exemple de pseudonyme</returns>
    internal static string ExampleFor(SpanCategory category, ObfuscationProject project)
    {
        foreach (Mapping.MappingEntry entry in project.Store.Entries)
        {
            if (entry.Category == category)
            {
                return entry.Pseudonym.Split('\n')[0];
            }
        }

        return project.Format.Compose(category.FamilyCode(), 1);
    }
}
