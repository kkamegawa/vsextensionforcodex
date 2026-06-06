using Microsoft.VisualStudio.Extensibility;
using Microsoft.VisualStudio.Extensibility.Commands;
using Codex.VisualStudio.Extension.ToolWindows;

namespace Codex.VisualStudio.Extension.Commands;

[VisualStudioContribution]
internal sealed class ShowCodexWindowCommand : Command
{
    public ShowCodexWindowCommand(VisualStudioExtensibility extensibility) : base(extensibility)
    {
    }

    public override CommandConfiguration CommandConfiguration => new("%ShowCodexWindowCommand.DisplayName%")
    {
        Placements = [CommandPlacement.KnownPlacements.ViewOtherWindowsMenu],
    };

    public override async Task ExecuteCommandAsync(IClientContext context, CancellationToken cancellationToken)
    {
        await Extensibility.Shell().ShowToolWindowAsync<CodexToolWindow>(activate: true, cancellationToken);
    }
}
