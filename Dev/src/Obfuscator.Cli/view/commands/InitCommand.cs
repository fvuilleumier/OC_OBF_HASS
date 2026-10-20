using System.ComponentModel;
using Spectre.Console;
using Spectre.Console.Cli;
using Obfuscator.Core.Model.Mapping;
using Obfuscator.Core.Model.Policy;
using Obfuscator.Core.Presenter;

namespace Obfuscator.Cli.View.Commands;

/// <summary>
/// Crée un projet d'obfuscation dans un dossier.
/// </summary>
internal sealed class InitCommand : Command<InitCommand.Settings>
{
    /// <summary>
    /// Paramètres de la commande d'initialisation.
    /// </summary>
    internal sealed class Settings : CommandSettings
    {
        /// <summary>Dossier à initialiser.</summary>
        [CommandArgument(0, "[dossier]")]
        [Description("Dossier du projet à créer. Dossier courant par défaut.")]
        public string? Path { get; init; }

        /// <summary>Profil de départ.</summary>
        [CommandOption("-p|--profile")]
        [Description("Profil de départ : network, appconfig ou notes.")]
        [DefaultValue("network")]
        public string Profile { get; init; } = "network";

        /// <summary>Chiffrement du coffre.</summary>
        [CommandOption("--vault")]
        [Description("Chiffrement du coffre : low (bas, par défaut) — l'application l'ouvre sur tout poste où elle est installée ; high (élevé) — seul votre compte Windows, sur ce poste, peut l'ouvrir.")]
        [DefaultValue(VaultProtection.Low)]
        public VaultProtection Vault { get; init; } = VaultProtection.Low;
    }

    /// <summary>
    /// Exécute l'initialisation.
    /// </summary>
    /// <param name="context">Contexte d'exécution de la commande</param>
    /// <param name="settings">Paramètres saisis</param>
    /// <param name="cancellationToken">Jeton d'annulation transmis par l'hôte</param>
    /// <returns>Code de sortie du processus</returns>
    protected override int Execute(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);

        string root = System.IO.Path.GetFullPath(settings.Path ?? Directory.GetCurrentDirectory());
        ObfuscationProject project = ObfuscationProject.Create(root, VaultProtectors.CreateDefault(), settings.Profile, settings.Vault);

        AnsiConsole.MarkupLine($"[green]Projet créé[/] dans [bold]{root.EscapeMarkup()}[/]");
        AnsiConsole.MarkupLine($"  profil        : {settings.Profile.EscapeMarkup()}");
        AnsiConsole.MarkupLine($"  préfixe       : {project.Format.Prefix}{project.Format.Nonce}");
        AnsiConsole.MarkupLine($"  coffre        : {VaultPresenter.Label(settings.Vault)}");
        AnsiConsole.MarkupLine($"  réglages      : {project.SettingsPath.EscapeMarkup()}");
        AnsiConsole.MarkupLine($"  vocabulaire   : {project.VocabularyPath.EscapeMarkup()}");
        AnsiConsole.WriteLine();

        // Le coffre contient les valeurs réelles : le rappeler à la création
        // vaut mieux que de compter sur la lecture d'une documentation.
        AnsiConsole.MarkupLine(
            "[yellow]Le coffre {0} contient les valeurs réelles, secrets compris.[/]",
            System.IO.Path.GetFileName(project.MappingPath));
        AnsiConsole.MarkupLine(
            "[yellow]En {0}, {1} : il ne doit jamais être partagé ni commité.[/]",
            VaultPresenter.Label(settings.Vault),
            VaultPresenter.Reach(settings.Vault));

        return 0;
    }
}
