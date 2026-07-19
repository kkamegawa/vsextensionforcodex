using System.IO;
using System.Xml.Linq;
using Codex.VisualStudio.Extension;

namespace Codex.VisualStudio.Ui.Tests;

[TestClass]
public sealed class TranscriptPresentationTests
{
    private const string ResourceName = "Codex.VisualStudio.Extension.ToolWindows.ChatToolWindowContent.xaml";
    private const string ToolWindowBackgroundBrushKey = "ToolWindowBackgroundBrushKey";
    private const string ToolWindowTextBrushKey = "ToolWindowTextBrushKey";
    private static readonly XNamespace Presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";

    [TestMethod]
    public void Xaml_TranscriptAuthorUsesPairedVsThemeColorsAtFullOpacity()
    {
        XDocument document = LoadXaml();
        XElement author = document
            .Descendants(Presentation + "TextBlock")
            .Single(element => element.Attribute("Text")?.Value == "{Binding Role}");

        AssertDynamicThemeKey(author, "Foreground", ToolWindowTextBrushKey);
        Assert.IsNull(
            author.Attribute("Opacity"),
            "Author labels are essential context and must remain at full opacity in High Contrast themes.");

        XElement messageCard = author
            .Ancestors(Presentation + "Border")
            .First(element => element.Element(Presentation + "Border.Style") is not null);
        XElement defaultBackground = messageCard
            .Element(Presentation + "Border.Style")!
            .Element(Presentation + "Style")!
            .Elements(Presentation + "Setter")
            .Single(element => element.Attribute("Property")?.Value == "Background");
        AssertDynamicThemeKey(defaultBackground, "Value", ToolWindowBackgroundBrushKey);
    }

    private static void AssertDynamicThemeKey(XElement element, string attributeName, string expectedKey)
    {
        string? value = element.Attribute(attributeName)?.Value;
        Assert.IsNotNull(value);
        StringAssert.StartsWith(value, "{DynamicResource ");
        StringAssert.Contains(value, expectedKey);
    }

    private static XDocument LoadXaml()
    {
        using Stream? stream = typeof(ChatViewModel).Assembly.GetManifestResourceStream(ResourceName);
        Assert.IsNotNull(stream, $"Embedded resource '{ResourceName}' not found.");
        return XDocument.Load(stream);
    }
}
