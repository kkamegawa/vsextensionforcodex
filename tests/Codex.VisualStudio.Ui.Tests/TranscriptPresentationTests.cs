using System.IO;
using System.Xml.Linq;
using Codex.VisualStudio.Extension;

namespace Codex.VisualStudio.Ui.Tests;

[TestClass]
public sealed class TranscriptPresentationTests
{
    private const string ResourceName = "Codex.VisualStudio.Extension.ToolWindows.ChatToolWindowContent.xaml";
    private const string ToolWindowBackgroundBrushKey = "ToolWindowBackgroundBrushKey";
    private const string ToolWindowButtonDownActiveGlyphBrushKey = "ToolWindowButtonDownActiveGlyphBrushKey";
    private const string ToolWindowButtonDownBrushKey = "ToolWindowButtonDownBrushKey";
    private const string ToolWindowButtonHoverActiveBrushKey = "ToolWindowButtonHoverActiveBrushKey";
    private const string ToolWindowButtonHoverActiveGlyphBrushKey = "ToolWindowButtonHoverActiveGlyphBrushKey";
    private const string ToolWindowCodeBlockBackgroundBrushKey = "ToolWindowCodeBlockBackgroundBrushKey";
    private const string ToolWindowTextBrushKey = "ToolWindowTextBrushKey";
    private static readonly XNamespace Presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";

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

    [TestMethod]
    public void Xaml_CommandOutputUsesAccessibleStandardExpanderWithTwoWayState()
    {
        XDocument document = LoadXaml();
        XElement expander = document
            .Descendants(Presentation + "Expander")
            .Single(element => element.Attribute("IsExpanded")?.Value?.Contains(
                nameof(ChatItemViewModel.IsCommandOutputExpanded), StringComparison.Ordinal) == true);

        Assert.AreEqual(
            "{Binding IsCommandOutputExpanded, Mode=TwoWay}",
            expander.Attribute("IsExpanded")?.Value);
        Assert.AreEqual(
            "{Binding CommandOutputAutomationName}",
            expander.Attributes().Single(attribute => attribute.Name.LocalName == "AutomationProperties.Name").Value);
        Assert.AreEqual(
            "{Binding CommandOutputAutomationHelpText}",
            expander.Attributes().Single(attribute => attribute.Name.LocalName == "AutomationProperties.HelpText").Value);
        AssertDynamicThemeKey(expander, "Background", ToolWindowCodeBlockBackgroundBrushKey);
        AssertDynamicThemeKey(expander, "Foreground", ToolWindowTextBrushKey);
        Assert.AreEqual("{StaticResource CommandOutputExpanderStyle}", expander.Attribute("Style")?.Value);

        XElement expanderStyle = document
            .Descendants(Presentation + "Style")
            .Single(element => element.Attribute(X + "Key")?.Value == "CommandOutputExpanderStyle");
        XElement visibilityTrigger = expanderStyle
            .Descendants(Presentation + "DataTrigger")
            .Single(element => element.Attribute("Binding")?.Value == "{Binding IsCommandOutputCollapsible}");
        Assert.AreEqual("True", visibilityTrigger.Attribute("Value")?.Value);
        Assert.AreEqual(
            "Visible",
            visibilityTrigger.Elements(Presentation + "Setter")
                .Single(element => element.Attribute("Property")?.Value == "Visibility")
                .Attribute("Value")?.Value);

        XElement header = expander
            .Element(Presentation + "Expander.Header")!
            .Element(Presentation + "TextBlock")!;
        Assert.AreEqual("{Binding CommandOutputExpansionLabel}", header.Attribute("Text")?.Value);
        Assert.AreEqual(
            "{StaticResource CommandOutputExpanderHeaderTextStyle}",
            header.Attribute("Style")?.Value);
        Assert.IsNull(
            header.Attribute("Foreground"),
            "The header label must inherit the ToggleButton foreground for every interaction state.");
    }

    [TestMethod]
    public void Xaml_CommandOutputExpanderHeaderUsesPairedVsThemeStatesAndKeyboardFocus()
    {
        XDocument document = LoadXaml();
        XElement expanderStyle = document
            .Descendants(Presentation + "Style")
            .Single(element => element.Attribute(X + "Key")?.Value == "CommandOutputExpanderStyle");
        XElement expanderTemplate = expanderStyle
            .Descendants(Presentation + "ControlTemplate")
            .Single(element => element.Attribute("TargetType")?.Value == "Expander");
        XElement headerToggle = expanderTemplate
            .Descendants(Presentation + "ToggleButton")
            .Single(element => element.Attribute(X + "Name")?.Value == "HeaderSite");

        Assert.AreEqual(
            "{Binding IsExpanded, Mode=TwoWay, RelativeSource={RelativeSource TemplatedParent}}",
            headerToggle.Attribute("IsChecked")?.Value);
        Assert.IsNull(
            headerToggle.Attribute("Foreground"),
            "The header toggle must inherit its normal foreground so template state triggers can override it.");

        XElement toggleTemplate = headerToggle
            .Descendants(Presentation + "ControlTemplate")
            .Single(element => element.Attribute("TargetType")?.Value == "ToggleButton");
        XElement glyph = toggleTemplate
            .Descendants(Presentation + "Path")
            .Single(element => element.Attribute(X + "Name")?.Value == "HeaderGlyph");
        Assert.AreEqual("{TemplateBinding Foreground}", glyph.Attribute("Fill")?.Value);

        XElement contentPresenter = toggleTemplate
            .Descendants(Presentation + "ContentPresenter")
            .Single();
        Assert.AreEqual(
            "{TemplateBinding Foreground}",
            contentPresenter.Attributes()
                .Single(attribute => attribute.Name.LocalName == "TextElement.Foreground")
                .Value);

        XElement[] triggers = toggleTemplate
            .Descendants(Presentation + "Trigger")
            .ToArray();
        AssertTriggerThemePair(
            triggers.Single(element => element.Attribute("Property")?.Value == "IsMouseOver"),
            ToolWindowButtonHoverActiveBrushKey,
            ToolWindowButtonHoverActiveGlyphBrushKey);
        AssertTriggerThemePair(
            triggers.Single(element => element.Attribute("Property")?.Value == "IsPressed"),
            ToolWindowButtonDownBrushKey,
            ToolWindowButtonDownActiveGlyphBrushKey);

        XElement checkedTrigger = triggers
            .Single(element => element.Attribute("Property")?.Value == "IsChecked");
        Assert.IsFalse(
            checkedTrigger.Elements(Presentation + "Setter").Any(element =>
                element.Attribute("Property")?.Value is "Background" or "Foreground"),
            "An expanded command must retain the normal themed header colors instead of looking selected.");
        Assert.AreEqual(
            "HeaderGlyph",
            checkedTrigger.Elements(Presentation + "Setter")
                .Single(element => element.Attribute("Property")?.Value == "Data")
                .Attribute("TargetName")?.Value);

        XElement focusTrigger = triggers
            .Single(element => element.Attribute("Property")?.Value == "IsKeyboardFocused");
        Assert.IsFalse(
            focusTrigger.Elements(Presentation + "Setter").Any(element =>
                element.Attribute("Property")?.Value == "Background"),
            "Keyboard focus must use the themed focus border without repainting the header surface.");
        XElement focusVisibility = focusTrigger
            .Elements(Presentation + "Setter")
            .Single(element => element.Attribute("TargetName")?.Value == "KeyboardFocusBorder");
        Assert.AreEqual("Visibility", focusVisibility.Attribute("Property")?.Value);
        Assert.AreEqual("Visible", focusVisibility.Attribute("Value")?.Value);

        XElement focusBorder = toggleTemplate
            .Descendants(Presentation + "Border")
            .Single(element => element.Attribute(X + "Name")?.Value == "KeyboardFocusBorder");
        AssertDynamicThemeKey(focusBorder, "BorderBrush", ToolWindowTextBrushKey);
    }

    [TestMethod]
    public void Xaml_CommandOutputSurfacesUseNoWrapAndHorizontalScrolling()
    {
        XDocument document = LoadXaml();
        XElement[] commandTextBlocks = document
            .Descendants(Presentation + "TextBlock")
            .Where(element => element.Attribute("Text")?.Value == "{Binding Text}"
                && element.Attribute("FontFamily")?.Value?.StartsWith("Consolas", StringComparison.Ordinal) == true)
            .ToArray();

        Assert.AreEqual(3, commandTextBlocks.Length);
        foreach (XElement textBlock in commandTextBlocks)
        {
            Assert.AreEqual("NoWrap", textBlock.Attribute("TextWrapping")?.Value);
            XElement scrollViewer = textBlock.Ancestors(Presentation + "ScrollViewer").First();
            Assert.AreEqual("Auto", scrollViewer.Attribute("HorizontalScrollBarVisibility")?.Value);
        }

        XElement expandedOutput = commandTextBlocks
            .Select(element => element.Ancestors(Presentation + "ScrollViewer").First())
            .Single(element => element.Attributes().Any(attribute =>
                attribute.Name.LocalName == "AutomationProperties.Name"
                && attribute.Value == "Buffered command output"));
        Assert.AreEqual("320", expandedOutput.Attribute("MaxHeight")?.Value);
        Assert.AreEqual("Auto", expandedOutput.Attribute("VerticalScrollBarVisibility")?.Value);

        XElement preview = commandTextBlocks
            .Select(element => element.Ancestors(Presentation + "ScrollViewer").First())
            .Single(element => element.Attributes().Any(attribute =>
                attribute.Name.LocalName == "AutomationProperties.Name"
                && attribute.Value == "Command output preview"));
        Assert.AreEqual("Disabled", preview.Attribute("VerticalScrollBarVisibility")?.Value);
        Assert.IsTrue(preview.Descendants(Presentation + "DataTrigger").Any(element =>
            element.Attribute("Binding")?.Value == "{Binding IsCommandOutputExpanded}"
            && element.Attribute("Value")?.Value == "True"));
    }

    private static void AssertDynamicThemeKey(XElement element, string attributeName, string expectedKey)
    {
        string? value = element.Attribute(attributeName)?.Value;
        Assert.IsNotNull(value);
        StringAssert.StartsWith(value, "{DynamicResource ");
        StringAssert.Contains(value, expectedKey);
    }

    private static void AssertTriggerThemePair(
        XElement trigger,
        string expectedBackgroundKey,
        string expectedForegroundKey)
    {
        XElement backgroundSetter = trigger
            .Elements(Presentation + "Setter")
            .Single(element => element.Attribute("Property")?.Value == "Background");
        Assert.AreEqual("HeaderBackground", backgroundSetter.Attribute("TargetName")?.Value);
        AssertDynamicThemeKey(backgroundSetter, "Value", expectedBackgroundKey);

        XElement foregroundSetter = trigger
            .Elements(Presentation + "Setter")
            .Single(element => element.Attribute("Property")?.Value == "Foreground");
        AssertDynamicThemeKey(foregroundSetter, "Value", expectedForegroundKey);
    }

    private static XDocument LoadXaml()
    {
        using Stream? stream = typeof(ChatViewModel).Assembly.GetManifestResourceStream(ResourceName);
        Assert.IsNotNull(stream, $"Embedded resource '{ResourceName}' not found.");
        return XDocument.Load(stream);
    }
}
