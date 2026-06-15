using Microsoft.VisualStudio.Extensibility;
using Microsoft.VisualStudio.Extensibility.Documents;
using Microsoft.VisualStudio.Extensibility.ToolWindows;
using Microsoft.VisualStudio.RpcContracts.RemoteUI;

namespace Codex.VisualStudio.Extension.ToolWindows;

[VisualStudioContribution]
internal sealed class CodexToolWindow : ToolWindow
{
    private OutputChannel? outputChannel;
    private ChatToolWindowContent? content;

    public CodexToolWindow(VisualStudioExtensibility extensibility) : base(extensibility)
    {
        Title = "Codex";
    }

    public override ToolWindowConfiguration ToolWindowConfiguration => new()
    {
        Placement = ToolWindowPlacement.DocumentWell,
    };

    public override async Task InitializeAsync(CancellationToken cancellationToken)
    {
        ExtensionDiagnostics.Write("Codex tool window initialization starting");
        outputChannel = await this.Extensibility.Views().Output
            .CreateOutputChannelAsync("Codex", cancellationToken)
            .ConfigureAwait(false);
        ExtensionDiagnostics.Write("Codex Output channel created");
    }

    public override Task<IRemoteUserControl> GetContentAsync(CancellationToken cancellationToken)
    {
        ExtensionDiagnostics.Write("Codex tool window content requested");
        content ??= new ChatToolWindowContent(outputChannel, this.Extensibility);
        return Task.FromResult<IRemoteUserControl>(content);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            content?.Dispose();
            outputChannel?.Dispose();
        }
        base.Dispose(disposing);
    }
}
