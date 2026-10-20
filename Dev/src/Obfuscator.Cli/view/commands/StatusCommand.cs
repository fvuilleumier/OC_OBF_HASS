using Obfuscator.Core.Model.Policy;
using Obfuscator.Core.Presenter;

namespace Obfuscator.Cli.View.Commands;

/// <summary>
/// Donne l'état des échanges : ce que l'IA a changé, ce que la source a changé
/// depuis le dernier échange. Lecture seule.
/// </summary>
internal sealed class StatusCommand : AutomationCommand<AutomationSettings>
{
    /// <inheritdoc />
    protected override string Name => ExchangeAutomation.StatusCommand;

    /// <inheritdoc />
    protected override AutomationReport Run(ObfuscationProject project, AutomationSettings settings)
    {
        return ExchangeAutomation.Status(project);
    }
}
