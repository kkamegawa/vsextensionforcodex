using Microsoft.VisualStudio.Extensibility;
using Microsoft.VisualStudio.Extensibility.Documents;
using Microsoft.VisualStudio.Extensibility.UI;

namespace Codex.VisualStudio.Extension.ToolWindows;

internal sealed class ChatToolWindowContent : RemoteUserControl
{
    public ChatToolWindowContent(OutputChannel? outputChannel, VisualStudioExtensibility extensibility)
        : base(new ChatViewModel(outputChannel, extensibility))
    {
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && DataContext is ChatViewModel vm)
            vm.Dispose();
        base.Dispose(disposing);
    }
}
