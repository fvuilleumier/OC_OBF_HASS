using Obfuscator.Core.Model.Policy;
using Obfuscator.Core.Presenter;

namespace Obfuscator.Cli.View.Commands;

/// <summary>
/// Publie la source vers la cible si rien n'attend l'utilisateur : ni fuite,
/// ni défaut d'isolement, ni fichier refusé ou bloqué, ni élément à trancher.
/// Sinon, ou avec <c>--dry-run</c>, rien n'est écrit. Aucune opération git :
/// l'application qui pilote s'en charge.
/// </summary>
internal sealed class PublishCommand : AutomationCommand<WritingAutomationSettings>
{
    /// <inheritdoc />
    protected override string Name => ExchangeAutomation.PublishCommand;

    /// <inheritdoc />
    protected override AutomationReport Run(ObfuscationProject project, WritingAutomationSettings settings)
    {
        return ExchangeAutomation.Publish(project, settings.DryRun, new ShellRecycler(), null);
    }
}
