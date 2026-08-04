using System.IO;
using System.Xml.Linq;
using Codex.VisualStudio.Extension;

namespace Codex.VisualStudio.Ui.Tests;

[TestClass]
public sealed class SkillSuggestionPresentationTests
{
    private const string ResourceName = "Codex.VisualStudio.Extension.ToolWindows.ChatToolWindowContent.xaml";
    private static readonly XNamespace Presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";

    [TestMethod]
    public async Task SkillSuggestions_NavigateWrapAcceptAndRetainRawName()
    {
        var accepted = new TaskCompletionSource<SkillSuggestionViewModel>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var viewModel = new SkillSuggestionPresentationViewModel();
        viewModel.Configure(suggestion =>
        {
            accepted.TrySetResult(suggestion);
            return Task.CompletedTask;
        });
        viewModel.ShowSuggestions(
        [
            new SkillSuggestionDescriptor("review-diff", "Review Diff", "repo", "Review the diff.", true),
            new SkillSuggestionDescriptor("write-tests", "Write Tests", "repo", "Draft unit tests.", true),
        ]);

        Assert.IsTrue(viewModel.IsSuggestionOpen);
        Assert.AreSame(viewModel.Suggestions[0], viewModel.SelectedSuggestion);
        Assert.IsNotNull(viewModel.MovePreviousKeyCommand);

        viewModel.MovePreviousCommand.Execute(null);
        await WaitForAsync(() => viewModel.SelectedSuggestion?.Name == "write-tests");
        viewModel.AcceptSuggestionCommand.Execute(null);

        SkillSuggestionViewModel result = await accepted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.AreEqual("write-tests", result.Name);
        Assert.IsFalse(viewModel.IsSuggestionOpen);
        Assert.IsNull(viewModel.AcceptSuggestionKeyCommand);
        StringAssert.Contains(viewModel.StatusAnnouncement, "Write Tests");
    }

    [TestMethod]
    public void SkillSuggestions_SanitizeRemoteUiDisplayText()
    {
        var viewModel = new SkillSuggestionPresentationViewModel();

        viewModel.ShowSuggestions(
        [
            new SkillSuggestionDescriptor(
                "unsafe-skill",
                "<b>unsafe-skill</b>",
                "repo",
                "<script>alert(1)</script>",
                true),
        ]);

        Assert.IsFalse(viewModel.Suggestions[0].DisplayName.Contains('<'));
        Assert.IsFalse(viewModel.Suggestions[0].ShortDescription.Contains('<'));
        Assert.AreEqual("unsafe-skill", viewModel.Suggestions[0].DisplayName);
    }

    [TestMethod]
    public void SkillSuggestions_MarksDisplayNameDisabledWithoutMutatingRawName()
    {
        var viewModel = new SkillSuggestionPresentationViewModel();

        viewModel.ShowSuggestions(
        [
            new SkillSuggestionDescriptor("legacy-formatter", "Legacy Formatter", "repo", "Kept for reference.", false),
        ]);

        Assert.AreEqual("legacy-formatter", viewModel.Suggestions[0].Name);
        StringAssert.Contains(viewModel.Suggestions[0].DisplayName, "(disabled)");
        Assert.IsFalse(viewModel.Suggestions[0].Enabled);
    }

    [TestMethod]
    public void Xaml_SkillSuggestionsAreInlineBoundedVirtualizedAndKeyboardAccessible()
    {
        XDocument document = LoadXaml();
        XElement list = document
            .Descendants(Presentation + "ListBox")
            .Single(element => element.Attribute("ItemsSource")?.Value == "{Binding SkillSuggestions.Suggestions}");

        Assert.IsNull(list.Ancestors(Presentation + "Popup").FirstOrDefault());
        Assert.AreEqual("198", list.Attribute("MaxHeight")?.Value);
        Assert.AreEqual("True", list.Attribute("VirtualizingPanel.IsVirtualizing")?.Value);
        Assert.AreEqual("Recycling", list.Attribute("VirtualizingPanel.VirtualizationMode")?.Value);
        Assert.AreEqual("{StaticResource SelectableRowStyle}", list.Attribute("ItemContainerStyle")?.Value);

        XElement rowText = list
            .Descendants(Presentation + "TextBlock")
            .Single(element => element.Attribute("Text")?.Value == "{Binding DisplayName}");
        Assert.AreEqual("{x:Null}", rowText.Attribute("Style")?.Value);
    }

    [TestMethod]
    public void Xaml_SkillSuggestionKeyBindingsUseDedicatedScope()
    {
        XDocument document = LoadXaml();
        XElement composer = document
            .Descendants(Presentation + "TextBox")
            .Single(element => (element.Attribute("Text")?.Value ?? string.Empty)
                .Contains("ComposerText", StringComparison.Ordinal));

        // The scope must be an ancestor Grid whose bindings reference SkillSuggestions -- neither
        // the composer-wide file scope nor the TextBox's own slash scope may carry it, since a
        // duplicate Key+Modifier pair in one InputBindings collection is what separate scopes
        // exist to avoid.
        XElement skillScope = composer
            .Ancestors(Presentation + "Grid")
            .First(element => element.Element(Presentation + "Grid.InputBindings")?
                .Elements(Presentation + "KeyBinding")
                .Any(binding => (binding.Attribute("Command")?.Value ?? string.Empty)
                    .Contains("SkillSuggestions", StringComparison.Ordinal)) == true);
        XElement[] skillBindings = skillScope
            .Element(Presentation + "Grid.InputBindings")!
            .Elements(Presentation + "KeyBinding")
            .ToArray();

        Assert.IsFalse(skillBindings.Any(binding => (binding.Attribute("Command")?.Value ?? string.Empty)
            .Contains("FileSuggestions", StringComparison.Ordinal)));
        Assert.IsFalse(skillBindings.Any(binding => (binding.Attribute("Command")?.Value ?? string.Empty)
            .Contains("SlashCommands", StringComparison.Ordinal)));
        AssertKeyBinding(skillBindings, "Up", "{Binding SkillSuggestions.MovePreviousKeyCommand}");
        AssertKeyBinding(skillBindings, "Down", "{Binding SkillSuggestions.MoveNextKeyCommand}");
        AssertKeyBinding(skillBindings, "Return", "{Binding SkillSuggestions.AcceptSuggestionKeyCommand}");
        AssertKeyBinding(skillBindings, "Tab", "{Binding SkillSuggestions.AcceptSuggestionKeyCommand}");
        AssertKeyBinding(skillBindings, "Escape", "{Binding SkillSuggestions.DismissSuggestionsKeyCommand}");

        // The TextBox itself must still carry only the slash scope's bindings, not the skill
        // scope's -- proving the skill KeyBindings live on the intermediate ancestor, not here.
        XElement[] textBoxBindings = composer
            .Element(Presentation + "TextBox.InputBindings")!
            .Elements(Presentation + "KeyBinding")
            .ToArray();
        Assert.IsFalse(textBoxBindings.Any(binding => (binding.Attribute("Command")?.Value ?? string.Empty)
            .Contains("SkillSuggestions", StringComparison.Ordinal)));
    }

    private static void AssertKeyBinding(XElement[] bindings, string key, string command)
    {
        Assert.IsNotNull(bindings.SingleOrDefault(element =>
            element.Attribute("Key")?.Value == key
            && element.Attribute("Command")?.Value == command));
    }

    private static XDocument LoadXaml()
    {
        using Stream? stream = typeof(ChatViewModel).Assembly.GetManifestResourceStream(ResourceName);
        Assert.IsNotNull(stream, $"Embedded resource '{ResourceName}' not found.");
        return XDocument.Load(stream);
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(2);
        while (!condition())
        {
            if (DateTime.UtcNow >= deadline)
            {
                Assert.Fail("Timed out waiting for the presentation state to update.");
            }

            await Task.Delay(10);
        }
    }
}
