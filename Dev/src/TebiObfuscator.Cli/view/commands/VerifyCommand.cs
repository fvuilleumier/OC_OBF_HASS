using System.ComponentModel;
using System.Globalization;
using Spectre.Console;
using Spectre.Console.Cli;
using TebiObfuscator.Core.Model.Mapping;
using TebiObfuscator.Core.Model.Policy;
using TebiObfuscator.Core.Presenter;

namespace TebiObfuscator.Cli.View.Commands;

/// <summary>
/// Contrôle qu'aucune valeur réelle du coffre ne subsiste dans des fichiers
/// censés être obfusqués.
/// </summary>
internal sealed class VerifyCommand : Command<VerifyCommand.Settings>
{
    /// <summary>
    /// Paramètres de la commande de contrôle.
    /// </summary>
    internal sealed class Settings : CommandSettings
    {
        /// <summary>Fichier ou dossier à contrôler.</summary>
        [CommandArgument(0, "[chemin]")]
        [Description("Fichier ou dossier à contrôler. Dossier « out » du projet par défaut.")]
        public string? Path { get; init; }
    }

    /// <summary>
    /// Exécute le contrôle.
    /// </summary>
    /// <param name="context">Contexte d'exécution de la commande</param>
    /// <param name="settings">Paramètres saisis</param>
    /// <param name="cancellationToken">Jeton d'annulation transmis par l'hôte</param>
    /// <returns>Code de sortie : 0 si aucune fuite, 2 sinon</returns>
    protected override int Execute(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);

        ObfuscationProject project = ProjectLocator.Open(settings.Path);
        string target = settings.Path ?? project.OutputPath;

        IReadOnlyList<string> files = new TextFileCollector().Collect(target);
        VerifyReport report = new VerifyPresenter(project).Run(files);

        foreach (VerifiedFile file in report.Files.Where(file => file.Residuals.Count > 0))
        {
            AnsiConsole.MarkupLine("[red]{0}[/]", Path.GetFileName(file.Path).EscapeMarkup());
            foreach (ResidualLiteral residual in file.Residuals)
            {
                AnsiConsole.MarkupLine(
                    "  {0} : {1} occurrence(s), aurait dû devenir {2}",
                    residual.Category.ToString().EscapeMarkup(),
                    residual.Occurrences.ToString(CultureInfo.InvariantCulture),
                    residual.Pseudonym.EscapeMarkup());
            }
        }

        if (report.IsClean)
        {
            AnsiConsole.MarkupLine(
                "[green]{0} fichier(s) contrôlé(s), aucune valeur réelle retrouvée.[/]",
                report.Files.Count.ToString(CultureInfo.InvariantCulture));
            return 0;
        }

        AnsiConsole.MarkupLine("[red]Des valeurs réelles subsistent : n'envoyez pas ces fichiers.[/]");
        return 2;
    }
}
