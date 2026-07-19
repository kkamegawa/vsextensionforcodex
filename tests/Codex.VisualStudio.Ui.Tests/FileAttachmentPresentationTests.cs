using System.IO;
using System.Runtime.Serialization;
using System.Xml.Linq;
using Codex.VisualStudio.Extension;

namespace Codex.VisualStudio.Ui.Tests;

[TestClass]
public sealed class FileAttachmentPresentationTests
{
    private const string ResourceName = "Codex.VisualStudio.Extension.ToolWindows.ChatToolWindowContent.xaml";
    private static readonly XNamespace Presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";

    [TestMethod]
    public async Task AttachmentChip_SanitizesDisplayAndInvokesRemovalWithoutSerializingFullPath()
    {
        string fullPath = Path.Combine("C:\\repo", "**Program.cs**");
        var removed = new TaskCompletionSource<AttachmentChipViewModel>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var chip = new AttachmentChipViewModel(
            fullPath,
            new SafeMarkdownService(),
            attachment =>
            {
                removed.TrySetResult(attachment);
                return Task.CompletedTask;
            });

        Assert.AreEqual("Program.cs", chip.DisplayName);
        Assert.AreEqual(fullPath, chip.FullPath);
        Assert.IsFalse(HasDataMember(nameof(AttachmentChipViewModel.FullPath), typeof(AttachmentChipViewModel)));
        StringAssert.Contains(chip.AutomationName, "Program.cs");

        chip.RemoveCommand.Execute(null);

        Assert.AreSame(chip, await removed.Task.WaitAsync(TimeSpan.FromSeconds(2)));
    }

    [TestMethod]
    public async Task FileSuggestions_NavigateWrapAcceptAndRetainTrustedFullPath()
    {
        var accepted = new TaskCompletionSource<FileSuggestionViewModel>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var viewModel = new FileSuggestionPresentationViewModel();
        viewModel.Configure(suggestion =>
        {
            accepted.TrySetResult(suggestion);
            return Task.CompletedTask;
        });
        viewModel.ShowSuggestions(
        [
            new FileSuggestionDescriptor("C:\\repo\\src\\First.cs", "First.cs", "src\\First.cs"),
            new FileSuggestionDescriptor("C:\\repo\\tests\\Second.cs", "Second.cs", "tests\\Second.cs"),
        ]);

        Assert.IsTrue(viewModel.IsSuggestionOpen);
        Assert.AreSame(viewModel.Suggestions[0], viewModel.SelectedSuggestion);
        Assert.IsNotNull(viewModel.MovePreviousKeyCommand);

        viewModel.MovePreviousCommand.Execute(null);
        await WaitForAsync(() => viewModel.SelectedSuggestion?.DisplayName == "Second.cs");
        viewModel.AcceptSuggestionCommand.Execute(null);

        FileSuggestionViewModel result = await accepted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.AreEqual("C:\\repo\\tests\\Second.cs", result.FullPath);
        Assert.IsFalse(viewModel.IsSuggestionOpen);
        Assert.IsNull(viewModel.AcceptSuggestionKeyCommand);
        StringAssert.Contains(viewModel.StatusAnnouncement, "Second.cs");
        Assert.IsFalse(HasDataMember(nameof(FileSuggestionViewModel.FullPath), typeof(FileSuggestionViewModel)));
    }

    [TestMethod]
    public void FileSuggestions_SanitizeRemoteUiDisplayText()
    {
        var viewModel = new FileSuggestionPresentationViewModel();

        viewModel.ShowSuggestions(
        [
            new FileSuggestionDescriptor(
                "C:\\repo\\unsafe.cs",
                "<b>unsafe.cs</b>",
                "\u001b[31msrc\\unsafe.cs\u001b[0m"),
        ]);

        Assert.AreEqual("unsafe.cs", viewModel.Suggestions[0].DisplayName);
        Assert.IsFalse(viewModel.Suggestions[0].RelativePath.Contains('\u001b'));
    }

    [TestMethod]
    public void Xaml_FileSuggestionsAreInlineBoundedVirtualizedAndKeyboardAccessible()
    {
        XDocument document = LoadXaml();
        XElement list = document
            .Descendants(Presentation + "ListBox")
            .Single(element => element.Attribute("ItemsSource")?.Value == "{Binding FileSuggestions.Suggestions}");

        Assert.IsNull(list.Ancestors(Presentation + "Popup").FirstOrDefault());
        Assert.AreEqual("198", list.Attribute("MaxHeight")?.Value);
        Assert.AreEqual("True", list.Attribute("VirtualizingPanel.IsVirtualizing")?.Value);
        Assert.AreEqual("Recycling", list.Attribute("VirtualizingPanel.VirtualizationMode")?.Value);

        XElement composer = document
            .Descendants(Presentation + "TextBox")
            .Single(element => (element.Attribute("Text")?.Value ?? string.Empty)
                .Contains("ComposerText", StringComparison.Ordinal));
        XElement composerScope = composer
            .Ancestors(Presentation + "Grid")
            .First(element => element.Element(Presentation + "Grid.InputBindings") is not null);
        XElement[] bindings = composerScope
            .Element(Presentation + "Grid.InputBindings")!
            .Elements(Presentation + "KeyBinding")
            .ToArray();

        AssertKeyBinding(bindings, "Up", "{Binding FileSuggestions.MovePreviousKeyCommand}");
        AssertKeyBinding(bindings, "Down", "{Binding FileSuggestions.MoveNextKeyCommand}");
        AssertKeyBinding(bindings, "Return", "{Binding FileSuggestions.AcceptSuggestionKeyCommand}");
        AssertKeyBinding(bindings, "Tab", "{Binding FileSuggestions.AcceptSuggestionKeyCommand}");
        AssertKeyBinding(bindings, "Escape", "{Binding FileSuggestions.DismissSuggestionsKeyCommand}");
    }

    [TestMethod]
    public void Xaml_AttachmentChipsUsePairedVsThemeBrushesAndAccessibleRemoval()
    {
        XDocument document = LoadXaml();
        XElement chips = document
            .Descendants(Presentation + "ItemsControl")
            .Single(element => element.Attribute("ItemsSource")?.Value == "{Binding PendingAttachments}");
        XElement chip = chips.Descendants(Presentation + "Border")
            .Single(element => element.Attribute("CornerRadius")?.Value == "12");

        Assert.IsTrue(chip.Attribute("Background")?.Value.Contains(
            "ToolWindowButtonDownBrushKey",
            StringComparison.Ordinal) == true);
        XElement label = chip.Descendants(Presentation + "TextBlock")
            .Single(element => element.Attribute("Text")?.Value == "{Binding DisplayName}");
        Assert.IsTrue(label.Attribute("Foreground")?.Value.Contains(
            "ToolWindowButtonDownActiveGlyphBrushKey",
            StringComparison.Ordinal) == true);

        XElement remove = chip.Descendants(Presentation + "Button").Single();
        Assert.AreEqual("{Binding RemoveCommand}", remove.Attribute("Command")?.Value);
        Assert.AreEqual(
            "{Binding AutomationName}",
            remove.Attribute(XName.Get("AutomationProperties.Name"))?.Value);
    }

    private static bool HasDataMember(string propertyName, Type type)
        => type.GetProperty(propertyName)!
            .GetCustomAttributes(typeof(DataMemberAttribute), inherit: false)
            .Length > 0;

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
