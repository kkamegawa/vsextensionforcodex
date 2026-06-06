using System.Runtime.Versioning;
using Microsoft.Extensions.DependencyInjection;
using VSX = Microsoft.VisualStudio.Extensibility;

[assembly: SupportedOSPlatform("windows10.0.22621")]

namespace Codex.VisualStudio.Extension;

[VSX.VisualStudioContribution]
internal sealed class CodexExtension : VSX.Extension
{
    public override VSX.ExtensionConfiguration ExtensionConfiguration => new()
    {
        Metadata = new(
            id: "CodexForVisualStudio.kkamegawa",
            version: ExtensionAssemblyVersion,
            publisherName: "kkamegawa",
            displayName: "Codex for Visual Studio",
            description: "AI coding assistant powered by OpenAI Codex."),
    };

    protected override void InitializeServices(IServiceCollection serviceCollection)
    {
        base.InitializeServices(serviceCollection);
    }
}
