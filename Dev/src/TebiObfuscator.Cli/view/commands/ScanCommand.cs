using System.ComponentModel;
using System.Globalization;
using Spectre.Console;
using Spectre.Console.Cli;
using TebiObfuscator.Core.Model.Policy;
using TebiObfuscator.Core.Presenter;

namespace TebiObfuscator.Cli.View.Commands;

/// <summary>
/// Relève ce qui serait masqué, sans rien écrire.
/// </summary>
internal sealed class ScanCommand : Command<ScanCommand.Settings>
{
    /// <summary>
    /// Paramètres de la commande de relevé.
    /// </summary>
    internal sealed class Settings : CommandSettings
    {
        /// <summary>Fichier ou dossier à analyser.</summary>
        [CommandArgument(0, "[chemin]")]
        [Description("Fichier ou dossier à analyser. Dossier « in » du projet par défaut.")]
        public string? Path { get; init; }

        /// <summary>Afficher les valeurs réelles plutôt que masquées.</summary>
        [CommandOption("--show-values")]
        [Description("Affiche les valeurs en clair. À n'utiliser que devant un écran de confiance.")]
        public bool ShowValues { get; init; }
    }

    /// <summary>
    /// Exécute le relevé.
    /// </summary>
    /// <param name="context">Contexte d'exécution de la commande</param>
    /// <param name="settings">Paramètres saisis</param>
    /// <param name="cancellationToken">Jeton d'annulation transmis par l'hôte</param>
    /// <returns>Code de sortie du processus</returns>
    protected override int Execute(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);

        ObfuscationProject project = ProjectLocator.Open(settings.Path);
        string target = settings.Path ?? project.InputPath;

        IReadOnlyList<string> files = new TextFileCollector { SkipProducedFiles = true }.Collect(target);
        if (files.Count == 0)
        {
            AnsiConsole.MarkupLine("[yellow]Aucun fichier texte à analyser.[/]");
            return 0;
        }

        ScanPresenter presenter = new(project);
        ScanReport report = presenter.Scan(files);
        presenter.SaveReport(report);

        ConsoleRenderer.WriteFindings(report, settings.ShowValues);
        ConsoleRenderer.WriteCandidates(report);

        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine(
            "Relevé écrit dans [bold]{0}[/] (valeurs masquées).",
            project.FindingsPath.EscapeMarkup());
        AnsiConsole.MarkupLine(
            "{0} fichier(s), {1} valeur(s) distincte(s).",
            report.FileCount.ToString(CultureInfo.InvariantCulture),
            report.Findings.Count.ToString(CultureInfo.InvariantCulture));

        return 0;
    }
}
