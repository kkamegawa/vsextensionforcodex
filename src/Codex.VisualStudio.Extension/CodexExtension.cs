using System.Runtime.Versioning;
using Microsoft.Extensions.DependencyInjection;
using VSX = Microsoft.VisualStudio.Extensibility;

[assembly: SupportedOSPlatform("windows10.0.22621")]

namespace Codex.VisualStudio.Extension;

internal static class ExtensionIdentity
{
    public const string Id = "Kkamegawa.CodexForVisualStudio";
    public const string PublisherName = "kazushikamegawa";
    public const string DisplayName = "Codex for Visual Studio";
    public const string Description = "AI coding assistant powered by OpenAI Codex.";

    public static Version AssemblyVersion { get; } =
        typeof(ExtensionIdentity).Assembly.GetName().Version ?? new Version(0, 0, 0, 0);
}

[VSX.VisualStudioContribution]
internal sealed class CodexExtension : VSX.Extension
{
    public override VSX.ExtensionConfiguration ExtensionConfiguration => new()
    {
        Metadata = new(
            id: ExtensionIdentity.Id,
            version: ExtensionAssemblyVersion,
            publisherName: ExtensionIdentity.PublisherName,
            displayName: ExtensionIdentity.DisplayName,
            description: ExtensionIdentity.Description),
    };

    protected override void InitializeServices(IServiceCollection serviceCollection)
    {
        base.InitializeServices(serviceCollection);
    }
}
