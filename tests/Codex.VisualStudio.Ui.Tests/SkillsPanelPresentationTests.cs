using System.IO;
using System.Xml.Linq;
using Codex.VisualStudio.Contracts;
using Codex.VisualStudio.Extension;

namespace Codex.VisualStudio.Ui.Tests;

[TestClass]
public sealed class SkillsPanelPresentationTests
{
    private static readonly string[] SkillsPanelCommands =
    [
        "{Binding RefreshSkillsCommand}",
        "{Binding CloseSkillsCommand}",
    ];

    private readonly SafeMarkdownService markdown = new();

    [TestMethod]
    public void Update_PopulatesSkillsAndReportsCount()
    {
        var panel = new SkillsPanelPresentation();
        var result = new ListSkillsResult
        {
            Skills =
            [
                new SkillInfo { Name = "review-diff", DisplayName = "Review Diff", Scope = "repo", Enabled = true, Path = "/repo/.codex/skills/review-diff", ShortDescription = "Review the diff." },
            ],
        };

        panel.Update(result, markdown);

        Assert.IsTrue(panel.HasData);
        Assert.HasCount(1, panel.Skills);
        Assert.AreEqual("Review Diff", panel.Skills[0].DisplayName);
        Assert.AreEqual("Repository", panel.Skills[0].ScopeLabel);
        Assert.AreEqual("1 skill available.", panel.StatusText);
    }

    [TestMethod]
    public void Update_MergesInPlaceAndPreservesInstanceIdentity()
    {
        var panel = new SkillsPanelPresentation();
        panel.Update(
            new ListSkillsResult
            {
                Skills = [new SkillInfo { Name = "review-diff", Scope = "repo", Enabled = true, Path = "/repo/.codex/skills/review-diff", ShortDescription = "Old description." }],
            },
            markdown);

        SkillPresentation firstInstance = panel.Skills[0];

        panel.Update(
            new ListSkillsResult
            {
                Skills = [new SkillInfo { Name = "review-diff", Scope = "repo", Enabled = true, Path = "/repo/.codex/skills/review-diff", ShortDescription = "New description." }],
            },
            markdown);

        Assert.HasCount(1, panel.Skills);
        Assert.AreSame(firstInstance, panel.Skills[0]);
        Assert.AreEqual("New description.", panel.Skills[0].ShortDescription);
    }

    [TestMethod]
    public void Update_ReordersExistingEntriesToMatchServerOrder()
    {
        var panel = new SkillsPanelPresentation();
        panel.Update(
            new ListSkillsResult
            {
                Skills =
                [
                    new SkillInfo { Name = "a", Scope = "repo", Enabled = true, Path = "/repo/.codex/skills/a" },
                    new SkillInfo { Name = "b", Scope = "repo", Enabled = true, Path = "/repo/.codex/skills/b" },
                ],
            },
            markdown);

        panel.Update(
            new ListSkillsResult
            {
                Skills =
                [
                    new SkillInfo { Name = "b", Scope = "repo", Enabled = true, Path = "/repo/.codex/skills/b" },
                    new SkillInfo { Name = "a", Scope = "repo", Enabled = true, Path = "/repo/.codex/skills/a" },
                ],
            },
            markdown);

        string[] expectedOrder = ["b", "a"];
        CollectionAssert.AreEqual(expectedOrder, panel.Skills.Select(skill => skill.Name).ToArray());
    }

    [TestMethod]
    public void Update_RemovesEntriesNoLongerReturnedByTheServer()
    {
        var panel = new SkillsPanelPresentation();
        panel.Update(
            new ListSkillsResult
            {
                Skills = [new SkillInfo { Name = "review-diff", Scope = "repo", Enabled = true, Path = "/repo/.codex/skills/review-diff" }],
            },
            markdown);

        panel.Update(new ListSkillsResult { Skills = [] }, markdown);

        Assert.IsEmpty(panel.Skills);
        Assert.IsFalse(panel.HasData);
        Assert.AreEqual("No skills found.", panel.StatusText);
    }

    [TestMethod]
    public void Update_ShowsLoadErrorsWithSanitizedFields()
    {
        var panel = new SkillsPanelPresentation();
        var result = new ListSkillsResult
        {
            Errors = [new SkillLoadError { Cwd = "/repo", Path = "/repo/.codex/skills/broken/SKILL.md", Message = "<script>alert(1)</script> invalid YAML" }],
        };

        panel.Update(result, markdown);

        Assert.IsTrue(panel.HasErrors);
        Assert.HasCount(1, panel.Errors);
        Assert.IsFalse(panel.Errors[0].Message.Contains('<'));
        StringAssert.Contains(panel.Errors[0].Message, "invalid YAML");
    }

    [TestMethod]
    public void Update_SetsIsTruncatedFromResult()
    {
        var panel = new SkillsPanelPresentation();

        panel.Update(new ListSkillsResult { Skills = [], IsTruncated = true }, markdown);

        Assert.IsTrue(panel.IsTruncated);
    }

    [TestMethod]
    public void Update_ShowsUnavailableStatusWhenUnsupported()
    {
        var panel = new SkillsPanelPresentation();
        panel.Update(
            new ListSkillsResult
            {
                Skills = [new SkillInfo { Name = "review-diff", Scope = "repo", Enabled = true, Path = "/repo/.codex/skills/review-diff" }],
            },
            markdown);

        panel.Update(new ListSkillsResult { IsSupported = false, UnavailableReason = "Skills are not supported by this app-server." }, markdown);

        Assert.IsFalse(panel.HasData);
        Assert.IsEmpty(panel.Skills);
        Assert.AreEqual("Skills are not supported by this app-server.", panel.StatusText);
    }

    [TestMethod]
    [DataRow("repo", true)]
    [DataRow("user", true)]
    [DataRow("system", false)]
    [DataRow("admin", false)]
    public void SkillPresentation_MarksSystemAndAdminScopeAsNonToggleable(string scope, bool expectedCanToggle)
    {
        var panel = new SkillsPanelPresentation();
        panel.Update(
            new ListSkillsResult
            {
                Skills = [new SkillInfo { Name = "policy-skill", Scope = scope, Enabled = true, Path = $"/{scope}/.codex/skills/policy-skill" }],
            },
            markdown);

        Assert.AreEqual(expectedCanToggle, panel.Skills[0].CanToggle);
    }

    [TestMethod]
    public void SkillPresentation_MarksDisabledSkillDisplayNameWithoutMutatingRawName()
    {
        var panel = new SkillsPanelPresentation();
        panel.Update(
            new ListSkillsResult
            {
                Skills = [new SkillInfo { Name = "legacy-formatter", DisplayName = "Legacy Formatter", Scope = "repo", Enabled = false, Path = "/repo/.codex/skills/legacy-formatter" }],
            },
            markdown);

        Assert.AreEqual("legacy-formatter", panel.Skills[0].Name);
        StringAssert.Contains(panel.Skills[0].DisplayName, "(disabled)");
        Assert.IsFalse(panel.Skills[0].IsEnabled);
    }

    [TestMethod]
    public void Clear_ResetsToUnavailableState()
    {
        var panel = new SkillsPanelPresentation();
        panel.Update(
            new ListSkillsResult
            {
                Skills = [new SkillInfo { Name = "review-diff", Scope = "repo", Enabled = true, Path = "/repo/.codex/skills/review-diff" }],
            },
            markdown);

        panel.Clear();

        Assert.IsFalse(panel.HasData);
        Assert.IsEmpty(panel.Skills);
        Assert.IsEmpty(panel.Errors);
        Assert.AreEqual("Skills are not available.", panel.StatusText);
    }

    [TestMethod]
    public void ChatToolWindowXaml_SkillsPanel_HasKeyboardAutomationAndThemeContracts()
    {
        const string resourceName = "Codex.VisualStudio.Extension.ToolWindows.ChatToolWindowContent.xaml";
        using Stream? stream = typeof(ChatViewModel).Assembly.GetManifestResourceStream(resourceName);
        Assert.IsNotNull(stream);
        XDocument document = XDocument.Load(stream);
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";

        XElement popup = document.Descendants(presentation + "Popup")
            .Single(value => value.Attribute("IsOpen")?.Value == "{Binding IsSkillsOpen, Mode=TwoWay}");
        XElement host = popup.Parent ?? throw new AssertFailedException("Skills popup must have a host element.");
        XElement border = popup.Descendants(presentation + "Border").First();
        Assert.AreEqual("True", popup.Attribute("Focusable")?.Value);
        Assert.AreEqual("True", border.Attribute("FocusManager.IsFocusScope")?.Value);
        Assert.AreEqual("{Binding ElementName=SkillsCloseButton}", border.Attribute("FocusManager.FocusedElement")?.Value);
        Assert.AreEqual("Cycle", border.Attribute("KeyboardNavigation.TabNavigation")?.Value);
        Assert.IsNotNull(border.Attribute("AutomationProperties.Name"));
        Assert.IsTrue(host.Elements(presentation + "Grid.InputBindings")
            .Descendants(presentation + "KeyBinding")
            .Any(value => value.Attribute("Key")?.Value == "Escape"
                && value.Attribute("Command")?.Value == "{Binding CloseSkillsCommand}"));
        Assert.IsTrue(border.Descendants(presentation + "KeyBinding")
            .Any(value => value.Attribute("Key")?.Value == "Escape"
                && value.Attribute("Command")?.Value == "{Binding CloseSkillsCommand}"));
        StringAssert.Contains(border.Attribute("Background")?.Value, "EnvironmentColors.ToolWindowBackgroundBrushKey");
        StringAssert.Contains(border.Attribute("BorderBrush")?.Value, "EnvironmentColors.ToolWindowBorderBrushKey");
        XElement list = border.Descendants(presentation + "ListBox")
            .Single(value => value.Attribute("ItemsSource")?.Value == "{Binding SkillsPanel.Skills}");

        // Panel-chrome buttons only: excludes the per-row toggle button living inside the
        // ListBox's DataTemplate, whose DataContext is a SkillPresentation, not the panel itself.
        // (The whole document's root element is itself a <DataTemplate>, so an Ancestors("DataTemplate")
        // check would exclude every button in the file -- comparing against this specific list's
        // own descendants is what actually isolates the per-row template content.)
        HashSet<XElement> rowButtons = list.Descendants(presentation + "Button").ToHashSet();
        CollectionAssert.AreEquivalent(
            SkillsPanelCommands,
            border.Descendants(presentation + "Button")
                .Where(value => !rowButtons.Contains(value))
                .Select(value => value.Attribute("Command")?.Value)
                .Where(value => value is not null)
                .Cast<string>()
                .ToArray());

        XElement toggleButton = list
            .Descendants(presentation + "Button")
            .Single(value => value.Attribute("Command")?.Value == "{Binding ToggleCommand}");
        Assert.AreEqual("{Binding ToggleButtonText}", toggleButton.Attribute("Content")?.Value);
        Assert.AreEqual("True", list.Attribute("VirtualizingPanel.IsVirtualizing")?.Value);
        Assert.AreEqual("Recycling", list.Attribute("VirtualizingPanel.VirtualizationMode")?.Value);
        Assert.IsNotNull(list.Attribute("MaxHeight"));

        string[] essentialTextValues =
        [
            "{Binding ScopeLabel}",
            "{Binding ShortDescription}",
            "{Binding Path}",
            "Showing the first skills and errors; the full catalog is larger than this listing can display.",
        ];
        foreach (string textValue in essentialTextValues)
        {
            XElement text = border.Descendants(presentation + "TextBlock")
                .Single(value => value.Attribute("Text")?.Value == textValue);
            Assert.IsNull(text.Attribute("Opacity"), $"Essential text '{textValue}' must remain opaque in High Contrast.");
            StringAssert.Contains(
                text.Attribute("Foreground")?.Value,
                "EnvironmentColors.ToolWindowTextBrushKey",
                $"Essential text '{textValue}' must use the Visual Studio tool-window foreground.");
        }
    }
}
