using System.ComponentModel;
using System.Text;
using Spectre.Console.Cli;
using Obfuscator.Core.Model.Policy;
using Obfuscator.Core.Presenter;

namespace Obfuscator.Cli.View.Commands;

/// <summary>
/// Paramètres communs aux échanges pilotés par une autre application.
/// </summary>
internal class AutomationSettings : CommandSettings
{
    /// <summary>Dossier du projet, ou l'un de ses sous-dossiers.</summary>
    [CommandArgument(0, "[chemin]")]
    [Description("Dossier du projet, ou l'un de ses sous-dossiers. Dossier courant par défaut.")]
    public string? Path { get; init; }

    /// <summary>Rendre un objet JSON unique sur la sortie standard.</summary>
    [CommandOption("--json")]
    [Description("Écrit un unique objet JSON sur la sortie standard, sans valeur réelle, et rien d'autre.")]
    public bool Json { get; init; }

    /// <summary>Vrai pour une simulation ; l'état n'écrit jamais rien.</summary>
    public virtual bool IsDryRun => false;
}

/// <summary>
/// Paramètres des échanges qui peuvent écrire : publication et rapatriement.
/// </summary>
internal sealed class WritingAutomationSettings : AutomationSettings
{
    /// <summary>N'écrire rien, seulement rapporter.</summary>
    [CommandOption("--dry-run")]
    [Description("N'écrit rien — ni fichiers, ni coffre, ni registre — et rapporte ce qui se passerait.")]
    public bool DryRun { get; init; }

    /// <inheritdoc />
    public override bool IsDryRun => DryRun;
}

/// <summary>
/// Base des commandes pilotées par une autre application : ouvrir le projet,
/// appeler <see cref="ExchangeAutomation"/>, rendre le rapport, et renvoyer son
/// code. Une erreur technique est rapportée comme le reste — en JSON si on le
/// demande — avec le code 1 : l'application qui pilote doit toujours recevoir
/// un rapport lisible.
/// </summary>
/// <typeparam name="TSettings">Paramètres de la commande</typeparam>
internal abstract class AutomationCommand<TSettings> : Command<TSettings>
    where TSettings : AutomationSettings
{
    /// <summary>Nom de la commande, repris dans le rapport.</summary>
    protected abstract string Name { get; }

    /// <summary>
    /// Exécute l'échange sur le projet ouvert.
    /// </summary>
    /// <param name="project">Projet</param>
    /// <param name="settings">Paramètres saisis</param>
    /// <returns>Le rapport</returns>
    protected abstract AutomationReport Run(ObfuscationProject project, TSettings settings);

    /// <summary>
    /// Ouvre le projet, exécute l'échange et rend le rapport.
    /// </summary>
    /// <param name="context">Contexte d'exécution de la commande</param>
    /// <param name="settings">Paramètres saisis</param>
    /// <param name="cancellationToken">Jeton d'annulation transmis par l'hôte</param>
    /// <returns>Le code du rapport</returns>
    protected override int Execute(CommandContext context, TSettings settings, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);

        ObfuscationProject? project = null;
        AutomationReport report;
        try
        {
            project = ProjectLocator.Open(settings.Path);
            report = Run(project, settings);
        }
        catch (Exception exception) when (IsReportable(exception))
        {
            report = ExchangeAutomation.Failure(Name, settings.IsDryRun, project, exception);
        }

        if (settings.Json)
        {
            // Un seul objet, en UTF-8, et rien d'autre sur la sortie standard :
            // l'application qui pilote la lit telle quelle.
            Console.OutputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
            Console.Out.WriteLine(ExchangeAutomation.ToJson(report));
            Console.Out.Flush();
        }
        else
        {
            ConsoleRenderer.WriteAutomation(report);
        }

        return report.ExitCode;
    }

    /// <summary>
    /// Indique si une erreur se rapporte : toutes, sauf celles qui laissent le
    /// processus dans un état où il ne peut plus rien rapporter.
    /// </summary>
    /// <param name="exception">Erreur survenue</param>
    /// <returns><c>true</c> si elle doit devenir un rapport d'erreur</returns>
    private static bool IsReportable(Exception exception)
    {
        return exception is not (OutOfMemoryException or StackOverflowException or AccessViolationException);
    }
}
