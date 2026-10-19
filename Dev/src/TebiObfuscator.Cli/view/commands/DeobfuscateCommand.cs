using System.ComponentModel;
using Spectre.Console;
using Spectre.Console.Cli;
using TebiObfuscator.Core.Model.Policy;
using TebiObfuscator.Core.Presenter;

namespace TebiObfuscator.Cli.View.Commands;

/// <summary>
/// Restitue les valeurs réelles dans un fichier ou dans le presse-papiers.
/// </summary>
internal sealed class DeobfuscateCommand : Command<DeobfuscateCommand.Settings>
{
    /// <summary>
    /// Paramètres de la commande de restitution.
    /// </summary>
    internal sealed class Settings : CommandSettings
    {
        /// <summary>Fichier contenant des pseudonymes.</summary>
        [CommandArgument(0, "[fichier]")]
        [Description("Fichier à restituer. Sans argument, utilisez --clipboard.")]
        public string? Path { get; init; }

        /// <summary>Fichier de sortie.</summary>
        [CommandOption("-o|--output")]
        [Description("Fichier à produire. « <fichier>.clair » par défaut.")]
        public string? Output { get; init; }

        /// <summary>Traiter le presse-papiers plutôt qu'un fichier.</summary>
        [CommandOption("-c|--clipboard")]
        [Description("Restitue le contenu du presse-papiers et l'y replace.")]
        public bool Clipboard { get; init; }
    }

    /// <summary>
    /// Exécute la restitution.
    /// </summary>
    /// <param name="context">Contexte d'exécution de la commande</param>
    /// <param name="settings">Paramètres saisis</param>
    /// <param name="cancellationToken">Jeton d'annulation transmis par l'hôte</param>
    /// <returns>Code de sortie du processus</returns>
    protected override int Execute(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);

        ObfuscationProject project = ProjectLocator.Open(settings.Path);
        DeobfuscatePresenter presenter = new(project);

        if (settings.Clipboard)
        {
            return RunOnClipboard(presenter);
        }

        if (string.IsNullOrWhiteSpace(settings.Path))
        {
            AnsiConsole.MarkupLine("[red]Indiquez un fichier, ou utilisez --clipboard.[/]");
            return 1;
        }

        string output = settings.Output ?? settings.Path + ".clair";
        DeobfuscationReport report = presenter.RunOnFile(settings.Path, output);

        ConsoleRenderer.WriteDeobfuscationReport(report);
        AnsiConsole.MarkupLine("Résultat écrit dans [bold]{0}[/]", Path.GetFullPath(output).EscapeMarkup());

        return 0;
    }

    /// <summary>
    /// Restitue le presse-papiers et y replace le résultat, pour enchaîner
    /// directement sur un collage dans l'application d'origine.
    /// </summary>
    /// <param name="presenter">Presenter de restitution</param>
    /// <returns>Code de sortie du processus</returns>
    private static int RunOnClipboard(DeobfuscatePresenter presenter)
    {
        string source = ClipboardAccess.Read();
        if (string.IsNullOrWhiteSpace(source))
        {
            AnsiConsole.MarkupLine("[yellow]Le presse-papiers est vide.[/]");
            return 1;
        }

        DeobfuscationReport report = presenter.RunOnText(source);
        ClipboardAccess.Write(report.Text);

        ConsoleRenderer.WriteDeobfuscationReport(report);
        AnsiConsole.MarkupLine("[green]Le presse-papiers contient désormais le texte restitué.[/]");

        return 0;
    }
}
