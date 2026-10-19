using System.ComponentModel;
using Spectre.Console;
using Spectre.Console.Cli;
using TebiObfuscator.Core.Model.Policy;
using TebiObfuscator.Core.Presenter;

namespace TebiObfuscator.Cli.View.Commands;

/// <summary>
/// Produit les fichiers obfusqués, leur légende et le rapport de vérification.
/// </summary>
internal sealed class ObfuscateCommand : Command<ObfuscateCommand.Settings>
{
    /// <summary>
    /// Paramètres de la commande d'obfuscation.
    /// </summary>
    internal sealed class Settings : CommandSettings
    {
        /// <summary>Fichier ou dossier à traiter.</summary>
        [CommandArgument(0, "[chemin]")]
        [Description("Fichier ou dossier à obfusquer. Dossier « in » du projet par défaut.")]
        public string? Path { get; init; }

        /// <summary>Dossier de sortie.</summary>
        [CommandOption("-o|--output")]
        [Description("Dossier de sortie. Dossier « out » du projet par défaut.")]
        public string? Output { get; init; }
    }

    /// <summary>
    /// Exécute l'obfuscation.
    /// </summary>
    /// <param name="context">Contexte d'exécution de la commande</param>
    /// <param name="settings">Paramètres saisis</param>
    /// <param name="cancellationToken">Jeton d'annulation transmis par l'hôte</param>
    /// <returns>Code de sortie : 0 si la vérification passe, 2 sinon</returns>
    protected override int Execute(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);

        ObfuscationProject project = ProjectLocator.Open(settings.Path);
        string target = settings.Path ?? project.InputPath;
        string output = settings.Output ?? project.OutputPath;

        IReadOnlyList<string> files = new TextFileCollector { SkipProducedFiles = true }.Collect(target);
        if (files.Count == 0)
        {
            AnsiConsole.MarkupLine("[yellow]Aucun fichier texte à traiter.[/]");
            return 0;
        }

        string sourceRoot = Directory.Exists(target) ? target : Path.GetDirectoryName(Path.GetFullPath(target))!;
        ObfuscationBatchReport report = new ObfuscatePresenter(project).Run(files, sourceRoot, output);

        ConsoleRenderer.WriteObfuscationReport(report);

        // Une fuite interdit toute écriture : le dire, plutôt que de laisser
        // croire que les sorties de « out » sont celles de cette exécution.
        if (report.Written)
        {
            AnsiConsole.MarkupLine("Sorties écrites dans [bold]{0}[/]", Path.GetFullPath(output).EscapeMarkup());
        }
        else
        {
            AnsiConsole.MarkupLine("[red]Rien n'a été écrit[/] : ni les sorties, ni le coffre. Masquez la valeur en cause, puis relancez.");
        }

        // Un code de sortie distinct permet d'enchaîner la commande dans un
        // script sans avoir à analyser l'affichage.
        return report.IsClean ? 0 : 2;
    }
}
