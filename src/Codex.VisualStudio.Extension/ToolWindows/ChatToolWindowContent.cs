using Microsoft.VisualStudio.Extensibility.Documents;
using Microsoft.VisualStudio.Extensibility.UI;

namespace Codex.VisualStudio.Extension.ToolWindows;

internal sealed class ChatToolWindowContent : RemoteUserControl
{
    public ChatToolWindowContent(OutputChannel? outputChannel) : base(new ChatViewModel(outputChannel))
    {
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && DataContext is ChatViewModel vm)
            vm.Dispose();
        base.Dispose(disposing);
    }
}
