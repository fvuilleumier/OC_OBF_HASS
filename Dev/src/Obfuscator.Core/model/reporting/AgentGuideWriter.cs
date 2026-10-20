using System.Globalization;
using System.Text;
using Obfuscator.Core.Model.Engine;
using Obfuscator.Core.Model.Policy;

namespace Obfuscator.Core.Model.Reporting;

/// <summary>
/// Fichier publié, décrit pour le guide de l'IA : son chemin dans la cible et
/// les familles de pseudonymes qu'il contient.
/// </summary>
/// <param name="TargetPath">Chemin dans la cible, nom pseudonymisé, avec des barres obliques</param>
/// <param name="Counts">Valeurs remplacées dans le fichier, par nature</param>
public sealed record PublishedFile(string TargetPath, IReadOnlyDictionary<SpanCategory, int> Counts);

/// <summary>
/// Écrit le guide destiné à l'IA qui travaille dans le dossier de la cible :
/// la section Obfuscator de son <c>AGENTS.md</c>.
///
/// Une IA de développement lit d'elle-même le fichier <c>AGENTS.md</c> placé à
/// la racine du dépôt où elle travaille. Il lui dit ce que la légende disait à
/// un modèle de conversation — comment lire les pseudonymes, ce qui est
/// authentique —, et surtout comment travailler pour que son travail revienne
/// proprement en clair : recopier les pseudonymes à l'identique, ne pas
/// renommer ni reformater, marquer ce qu'elle ne sait pas.
///
/// La section est délimitée et régénérée à chaque obfuscation ; ce que
/// l'utilisateur écrit autour est gardé. Elle ne cite ni chemin absolu, ni nom
/// de projet, ni aucun nom en clair : elle part du côté de l'IA.
/// </summary>
public static class AgentGuideWriter
{
    /// <summary>Nom du guide, à la racine du dossier de l'IA.</summary>
    public const string AgentsFileName = "AGENTS.md";

    /// <summary>Nom du fichier lu par Claude Code, qui renvoie au guide.</summary>
    public const string ClaudeFileName = "CLAUDE.md";

    /// <summary>Contenu du fichier lu par Claude Code : un renvoi au guide.</summary>
    public const string ClaudeContent = "@AGENTS.md\n";

    /// <summary>Marqueur de début de la section régénérée.</summary>
    public const string StartMarker = "<!-- obf:début · section régénérée par Obfuscator à chaque obfuscation : ne pas la modifier -->";

    /// <summary>Marqueur de fin de la section régénérée.</summary>
    public const string EndMarker = "<!-- obf:fin -->";

    /// <summary>Repère qu'une IA écrit à la place d'une valeur réelle qu'elle ne connaît pas.</summary>
    public const string Placeholder = "À_COMPLÉTER";

    /// <summary>Début reconnaissable du marqueur de début, quelle que soit la suite.</summary>
    private const string StartMarkerPrefix = "<!-- obf:début";


    /// <summary>
    /// Compose la section Obfuscator du guide.
    /// </summary>
    /// <param name="project">Projet qui publie</param>
    /// <param name="files">Fichiers publiés, avec leurs familles de pseudonymes</param>
    /// <returns>La section, marqueurs compris</returns>
    public static string Section(ObfuscationProject project, IReadOnlyList<PublishedFile> files)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(files);

        StringBuilder builder = new();
        builder.AppendLine(StartMarker);
        builder.AppendLine("# Fichiers pseudonymisés");
        builder.AppendLine();
        builder.AppendLine("Les fichiers de ce dépôt ont été pseudonymisés par Obfuscator avant de vous être");
        builder.AppendLine("confiés : les valeurs identifiantes — noms, adresses, secrets, coordonnées — ont été");
        builder.AppendLine("remplacées par des pseudonymes ; tout le reste est authentique. Vos modifications seront");
        builder.AppendLine("restaurées en clair dans le dépôt d'origine, puis relues avec git avant d'être appliquées.");
        builder.AppendLine();

        AppendWorkRules(builder);
        LegendWriter.AppendRules(builder, "ces fichiers");

        Dictionary<SpanCategory, int> totals = [];
        foreach (PublishedFile file in files)
        {
            foreach ((SpanCategory category, int count) in file.Counts)
            {
                totals[category] = totals.GetValueOrDefault(category) + count;
            }
        }

        LegendWriter.AppendFamilies(builder, totals, project, "Aucune valeur n'a dû être remplacée dans ces fichiers.");
        LegendWriter.AppendPreserved(builder, project);
        AppendFiles(builder, files);

        builder.AppendLine(EndMarker);
        return builder.ToString();
    }

    /// <summary>
    /// Place la section dans un guide existant : elle remplace la précédente,
    /// ou se place en tête si le guide n'en a pas encore. Le texte de
    /// l'utilisateur, autour, est gardé.
    /// </summary>
    /// <param name="existing">Guide actuel, ou <c>null</c> s'il n'existe pas</param>
    /// <param name="section">Section régénérée</param>
    /// <returns>Le guide complet</returns>
    public static string Merge(string? existing, string section)
    {
        ArgumentNullException.ThrowIfNull(section);

        if (string.IsNullOrEmpty(existing))
        {
            return section;
        }

        int start = existing.IndexOf(StartMarkerPrefix, StringComparison.Ordinal);
        int end = start < 0 ? -1 : existing.IndexOf(EndMarker, start, StringComparison.Ordinal);

        if (start < 0 || end < 0)
        {
            return section + Environment.NewLine + existing;
        }

        // La fin de ligne qui suit le marqueur de fin appartient à la section.
        int after = end + EndMarker.Length;
        if (after < existing.Length && existing[after] == '\r')
        {
            after++;
        }

        if (after < existing.Length && existing[after] == '\n')
        {
            after++;
        }

        return existing[..start] + section + existing[after..];
    }

    /// <summary>
    /// Écrit les consignes de travail.
    /// </summary>
    /// <param name="builder">Tampon de construction</param>
    private static void AppendWorkRules(StringBuilder builder)
    {
        builder.AppendLine("## Consignes de travail");
        builder.AppendLine();
        builder.AppendLine("- Travaillez uniquement dans ce dépôt. Les valeurs réelles n'y figurent pas et ne");
        builder.AppendLine("  doivent pas être recherchées ailleurs.");
        builder.AppendLine("- Un même pseudonyme désigne une même entité, dans tous les fichiers. Recopiez-le à");
        builder.AppendLine("  l'identique — casse, séparateurs, chiffres — quand vous le réutilisez.");
        builder.AppendLine("- N'inventez pas de pseudonyme pour une entité existante. Une valeur réelle que vous ne");
        builder.AppendLine(CultureInfo.InvariantCulture, $"  connaissez pas — un mot de passe, une adresse, une coordonnée — s'écrit `{Placeholder}` :");
        builder.AppendLine("  elle sera signalée, pour être complétée.");
        builder.AppendLine("- Une adresse nouvelle dans un sous-réseau existant — même début qu'une adresse des");
        builder.AppendLine("  fichiers — sera restituée dans le vrai réseau : vous pouvez en attribuer.");
        builder.AppendLine("- Les coordonnées géographiques sont fictives : n'en déduisez ni lieu ni distance.");
        builder.AppendLine("- Ne renommez pas les fichiers, ne déplacez pas de dossier et ne reformatez pas un");
        builder.AppendLine("  fichier entier : chaque ligne changée apparaîtra au contrôle git du dépôt d'origine.");
        builder.AppendLine("  Gardez l'indentation et les fins de ligne.");
        builder.AppendLine("- Vous pouvez créer des fichiers : leur nom sera restitué de la même façon. Une");
        builder.AppendLine("  suppression sera proposée, jamais appliquée d'office.");
        builder.AppendLine("- Ne modifiez pas cette section : elle est régénérée à chaque obfuscation.");
        builder.AppendLine();
    }

    /// <summary>
    /// Écrit la liste des fichiers publiés et des familles de pseudonymes de
    /// chacun.
    /// </summary>
    /// <param name="builder">Tampon de construction</param>
    /// <param name="files">Fichiers publiés</param>
    private static void AppendFiles(StringBuilder builder, IReadOnlyList<PublishedFile> files)
    {
        builder.AppendLine("## Fichiers publiés");
        builder.AppendLine();

        if (files.Count == 0)
        {
            builder.AppendLine("Aucun fichier.");
            builder.AppendLine();
            return;
        }

        builder.AppendLine("| Fichier | Pseudonymes |");
        builder.AppendLine("|---|---|");

        foreach (PublishedFile file in files.OrderBy(file => file.TargetPath, StringComparer.OrdinalIgnoreCase))
        {
            string families = file.Counts.Count == 0
                ? "aucun"
                : string.Join(", ", file.Counts
                    .OrderByDescending(pair => pair.Value)
                    .Select(pair => string.Create(CultureInfo.InvariantCulture, $"{CategoryLabels.Label(pair.Key)} ×{pair.Value}")));

            builder.AppendLine(CultureInfo.InvariantCulture, $"| `{file.TargetPath.Replace("|", "\\|", StringComparison.Ordinal)}` | {families} |");
        }

        builder.AppendLine();
    }
}
