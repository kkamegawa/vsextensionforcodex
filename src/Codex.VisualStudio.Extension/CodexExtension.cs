using System.Runtime.Versioning;
using Microsoft.Extensions.DependencyInjection;
using VSX = Microsoft.VisualStudio.Extensibility;

[assembly: SupportedOSPlatform("windows10.0.22621")]

namespace Codex.VisualStudio.Extension;

internal static class ExtensionIdentity
{
    // Marketplace identity: Name.Publisher.GUID. Unrelated to the %APPDATA%
    // Kkamegawa.CodexForVisualStudio settings folder, which must stay unchanged.
    public const string Id = "CodexForVisualStudio.KazushiKamegawa.9527382d-b5f4-455c-97bd-a8da43e7c835";
    public const string PublisherName = "kazushikamegawa";
    public const string DisplayName = "Codex for Visual Studio";
    public const string Description = "AI coding assistant powered by OpenAI Codex.";
    public const string MoreInfo = "https://github.com/kkamegawa/vsextensionforcodex";

    // Relative paths inside the VSIX. Bundled documents are English only; both files are staged by
    // the StageVsixAssets target in the project file.
    public const string License = "LICENSE.txt";
    public const string Icon = "icon.png";

    public static string[] Tags { get; } =
        ["codex", "openai", "ai", "chat", "agent", "assistant", "productivity"];

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
            // Comes from the assembly version, which the release workflow sets from the git tag.
            version: ExtensionAssemblyVersion,
            publisherName: ExtensionIdentity.PublisherName,
            displayName: ExtensionIdentity.DisplayName,
            description: ExtensionIdentity.Description)
        {
            MoreInfo = ExtensionIdentity.MoreInfo,
            License = ExtensionIdentity.License,
            Icon = ExtensionIdentity.Icon,
            Tags = ExtensionIdentity.Tags,
        },
    };

    protected override void InitializeServices(IServiceCollection serviceCollection)
    {
        base.InitializeServices(serviceCollection);
    }
}
