using System.IO;
using System.Xml.Linq;
using Codex.VisualStudio.Extension;

namespace Codex.VisualStudio.Ui.Tests;

[TestClass]
public sealed class InteractionThemePresentationTests
{
    private const string ResourceName = "Codex.VisualStudio.Extension.ToolWindows.ChatToolWindowContent.xaml";
    private const string ToolWindowBackgroundBrushKey = "ToolWindowBackgroundBrushKey";
    private const string ToolWindowTextBrushKey = "ToolWindowTextBrushKey";
    private static readonly XNamespace Presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";
    private static readonly string[] QuestionStepCommands =
    [
        "{Binding UserInput.BackCommand}",
        "{Binding UserInput.NextCommand}",
        "{Binding UserInput.SubmitCommand}",
        "{Binding UserInput.CancelCommand}",
    ];

    [TestMethod]
    public void Xaml_PendingInteractionCardPairsItsSurfaceAndTextThemeColors()
    {
        XDocument document = LoadXaml();
        XElement rowStyle = document
            .Descendants(Presentation + "Style")
            .Single(element => element.Attribute(X + "Key")?.Value == "MessageRowStyle");
        XElement rowForeground = rowStyle
            .Elements(Presentation + "Setter")
            .Single(element => element.Attribute("Property")?.Value == "Foreground");
        AssertThemeKey(rowForeground, "Value", ToolWindowTextBrushKey);

        XElement interactions = document
            .Descendants(Presentation + "ListBox")
            .Single(element => element.Attribute("ItemsSource")?.Value == "{Binding PendingInteractions}");
        XElement card = interactions
            .Element(Presentation + "ListBox.ItemTemplate")!
            .Element(Presentation + "DataTemplate")!
            .Element(Presentation + "Border")!;

        AssertThemeKey(card, "Background", ToolWindowBackgroundBrushKey);
        AssertThemeKey(card, "TextElement.Foreground", ToolWindowTextBrushKey);
        AssertThemeKey(card, "BorderBrush", "ToolWindowBorderBrushKey");

        Assert.IsTrue(
            card.Descendants(Presentation + "TextBlock").Any(),
            "The card must contain its textual interaction content beneath the themed foreground scope.");
    }

    [TestMethod]
    public void Xaml_ChoiceControlsUseVisualStudioThemeTemplates()
    {
        XDocument document = LoadXaml();
        XElement radioStyle = GetImplicitStyle(document, "RadioButton");
        XElement checkBoxStyle = GetImplicitStyle(document, "CheckBox");

        Assert.AreEqual(
            "{StaticResource {x:Static styles:VsResourceKeys.ThemedDialogRadioButtonStyleKey}}",
            radioStyle.Attribute("BasedOn")?.Value);
        Assert.AreEqual(
            "{StaticResource {x:Static styles:VsResourceKeys.CheckBoxStyleKey}}",
            checkBoxStyle.Attribute("BasedOn")?.Value);
        AssertThemeKey(
            radioStyle.Elements(Presentation + "Setter")
                .Single(element => element.Attribute("Property")?.Value == "Foreground"),
            "Value",
            ToolWindowTextBrushKey);
        AssertThemeKey(
            checkBoxStyle.Elements(Presentation + "Setter")
                .Single(element => element.Attribute("Property")?.Value == "Foreground"),
            "Value",
            ToolWindowTextBrushKey);

        XElement interactions = document
            .Descendants(Presentation + "ListBox")
            .Single(element => element.Attribute("ItemsSource")?.Value == "{Binding PendingInteractions}");
        Assert.IsTrue(interactions.Descendants(Presentation + "RadioButton").Any(),
            "Question choices must retain the VS radio-button template for themed states and keyboard focus.");
        Assert.IsTrue(interactions.Descendants(Presentation + "CheckBox").Any(),
            "Permission choices must retain the VS checkbox template, including mixed-state glyph support.");
    }

    [TestMethod]
    public void Xaml_CommandOutputHeaderHasNormalThemeForegroundAndKeepsStateColorPairs()
    {
        XDocument document = LoadXaml();
        XElement expanderStyle = document
            .Descendants(Presentation + "Style")
            .Single(element => element.Attribute(X + "Key")?.Value == "CommandOutputExpanderStyle");
        AssertThemeKey(
            expanderStyle.Elements(Presentation + "Setter")
                .Single(element => element.Attribute("Property")?.Value == "Foreground"),
            "Value",
            ToolWindowTextBrushKey);

        XElement expanderTemplate = expanderStyle
            .Elements(Presentation + "Setter")
            .Single(element => element.Attribute("Property")?.Value == "Template")
            .Element(Presentation + "Setter.Value")!
            .Element(Presentation + "ControlTemplate")!;
        XElement headerSite = expanderTemplate
            .Descendants(Presentation + "ToggleButton")
            .Single(element => element.Attribute(X + "Name")?.Value == "HeaderSite");
        Assert.IsNull(headerSite.Attribute("Foreground"),
            "A local foreground would override the VS ToggleButton template state triggers.");
        XElement headerStyleForegroundSetter = headerSite
            .Element(Presentation + "ToggleButton.Style")!
            .Element(Presentation + "Style")!
            .Elements(Presentation + "Setter")
            .Single(element => element.Attribute("Property")?.Value == "Foreground");
        AssertThemeKey(headerStyleForegroundSetter, "Value", ToolWindowTextBrushKey);

        XElement headerContentPresenter = headerSite
            .Descendants(Presentation + "ContentPresenter")
            .Single(element => element.Attribute("TextElement.Foreground") is not null);
        Assert.AreEqual(
            "{TemplateBinding Foreground}",
            headerContentPresenter.Attribute("TextElement.Foreground")?.Value);

        XElement headerToggleTemplate = headerSite
            .Element(Presentation + "ToggleButton.Template")!
            .Element(Presentation + "ControlTemplate")!;
        AssertTriggerThemePair(
            headerToggleTemplate,
            "IsMouseOver",
            "ToolWindowButtonHoverActiveBrushKey",
            "ToolWindowButtonHoverActiveGlyphBrushKey");
        AssertTriggerThemePair(
            headerToggleTemplate,
            "IsPressed",
            "ToolWindowButtonDownBrushKey",
            "ToolWindowButtonDownActiveGlyphBrushKey");
    }

    [TestMethod]
    public void Xaml_PlanModeIsPersistentTwoWayAccessibleAndWrappedWithComposerActions()
    {
        XDocument document = LoadXaml();
        XElement planMode = document
            .Descendants(Presentation + "CheckBox")
            .Single(element => element.Attribute("Content")?.Value == "Plan mode");

        Assert.AreEqual("{Binding PlanModeEnabled, Mode=TwoWay}", planMode.Attribute("IsChecked")?.Value);
        Assert.AreEqual("Plan mode", planMode.Attributes().Single(
            attribute => attribute.Name.LocalName == "AutomationProperties.Name").Value);
        Assert.AreEqual("{Binding PlanModeHelpText}", planMode.Attributes().Single(
            attribute => attribute.Name.LocalName == "AutomationProperties.HelpText").Value);
        Assert.AreEqual("{Binding PlanModeHelpText}", planMode.Attribute("ToolTip")?.Value);
        Assert.AreEqual("{Binding IsPlanModeAvailable}", planMode.Attribute("IsEnabled")?.Value);
        Assert.AreEqual("True", planMode.Attributes().Single(
            attribute => attribute.Name.LocalName == "ToolTipService.ShowOnDisabled").Value,
            "The reason Plan mode is unavailable must stay discoverable while the checkbox is disabled.");
        Assert.IsNotNull(planMode.Ancestors(Presentation + "WrapPanel").FirstOrDefault(),
            "Plan mode must wrap with the other composer controls at narrow widths.");
    }

    [TestMethod]
    public void Xaml_QuestionCardShowsOneQuestionAtATimeAndKeepsActionsReachable()
    {
        XDocument document = LoadXaml();
        XElement interactions = document
            .Descendants(Presentation + "ListBox")
            .Single(element => element.Attribute("ItemsSource")?.Value == "{Binding PendingInteractions}");
        Assert.AreEqual("Pixel", interactions.Attributes().Single(
            attribute => attribute.Name.LocalName == "VirtualizingPanel.ScrollUnit").Value,
            "Item scrolling cannot reach the actions of a card taller than the bounded surface.");

        Assert.IsTrue(interactions.Descendants(Presentation + "ItemsControl")
            .Any(element => element.Attribute("ItemsSource")?.Value == "{Binding UserInput.CurrentQuestions}"));
        Assert.IsFalse(interactions.Descendants(Presentation + "ItemsControl")
            .Any(element => element.Attribute("ItemsSource")?.Value == "{Binding UserInput.Questions}"),
            "Rendering every question at once pushes Submit out of view.");
        Assert.IsTrue(interactions.Descendants(Presentation + "TextBlock")
            .Any(element => element.Attribute("Text")?.Value == "{Binding UserInput.StepText}"));
        string[] commands = interactions.Descendants(Presentation + "Button")
            .Select(element => element.Attribute("Command")?.Value ?? string.Empty)
            .ToArray();
        CollectionAssert.IsSubsetOf(QuestionStepCommands, commands);
    }

    [TestMethod]
    public void Xaml_ProposedPlanUsesBlockRenderingInsteadOfRawText()
    {
        XDocument document = LoadXaml();
        XElement rawPlanText = document
            .Descendants(Presentation + "TextBlock")
            .Single(element => element.Attribute("Text")?.Value == "{Binding PlanDeltaText}");
        StringAssert.Contains(rawPlanText.Attribute("Visibility")?.Value, "ShowPlanDeltaText");
        Assert.IsTrue(document.Descendants(Presentation + "TextBlock")
            .Any(element => element.Attribute("Text")?.Value == "{Binding DisplayRole}"));
    }

    private static XElement GetImplicitStyle(XDocument document, string targetType)
        => document
            .Descendants(Presentation + "Style")
            .Single(element => element.Attribute("TargetType")?.Value == targetType
                && element.Attribute(X + "Key") is null);

    private static void AssertThemeKey(XElement element, string attributeName, string expectedKey)
    {
        string? value = element.Attribute(attributeName)?.Value;
        Assert.IsTrue(value?.StartsWith("{DynamicResource ", StringComparison.Ordinal) == true,
            $"{attributeName} must remain dynamic so it follows Visual Studio theme changes.");
        StringAssert.Contains(value!, expectedKey);
    }

    private static void AssertTriggerThemePair(
        XElement controlTemplate,
        string triggerProperty,
        string expectedBackgroundKey,
        string expectedForegroundKey)
    {
        XElement trigger = controlTemplate
            .Elements(Presentation + "ControlTemplate.Triggers")
            .Elements(Presentation + "Trigger")
            .Single(element => element.Attribute("Property")?.Value == triggerProperty);
        AssertThemeKey(
            trigger.Elements(Presentation + "Setter")
                .Single(element => element.Attribute("Property")?.Value == "Background"),
            "Value",
            expectedBackgroundKey);
        AssertThemeKey(
            trigger.Elements(Presentation + "Setter")
                .Single(element => element.Attribute("Property")?.Value == "Foreground"),
            "Value",
            expectedForegroundKey);
    }

    private static XDocument LoadXaml()
    {
        using Stream? stream = typeof(ChatViewModel).Assembly.GetManifestResourceStream(ResourceName);
        Assert.IsNotNull(stream, $"Embedded resource '{ResourceName}' not found.");
        return XDocument.Load(stream);
    }
}
