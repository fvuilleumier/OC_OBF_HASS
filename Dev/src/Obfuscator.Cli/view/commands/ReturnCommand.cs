using Obfuscator.Core.Model.Policy;
using Obfuscator.Core.Presenter;

namespace Obfuscator.Cli.View.Commands;

/// <summary>
/// Rapatrie le travail de l'IA dans la source s'il ne demande aucun choix : ni
/// conflit à trancher, ni fichier que l'échange ne coche pas d'office. Sinon,
/// ou avec <c>--dry-run</c>, rien n'est écrit. Aucune opération git :
/// l'application qui pilote s'en charge.
/// </summary>
internal sealed class ReturnCommand : AutomationCommand<WritingAutomationSettings>
{
    /// <inheritdoc />
    protected override string Name => ExchangeAutomation.ReturnCommand;

    /// <inheritdoc />
    protected override AutomationReport Run(ObfuscationProject project, WritingAutomationSettings settings)
    {
        return ExchangeAutomation.Return(project, settings.DryRun, new ShellRecycler(), null);
    }
}
