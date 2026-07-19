using System.Xml.Linq;
using System.IO;
using Codex.VisualStudio.Extension;

namespace Codex.VisualStudio.Ui.Tests;

[TestClass]
public sealed class SlashCommandPresentationTests
{
    private const string ResourceName = "Codex.VisualStudio.Extension.ToolWindows.ChatToolWindowContent.xaml";
    private static readonly XNamespace Presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";

    [TestMethod]
    public void ShowSuggestions_OpensInlineListAndSelectsFirstAvailableCommand()
    {
        var viewModel = new SlashCommandPresentationViewModel();

        viewModel.ShowSuggestions(
        [
            new SlashCommandSuggestionDescriptor("/disabled", "Unavailable", IsAvailable: false),
            new SlashCommandSuggestionDescriptor("/review", "Review changes"),
        ]);

        Assert.IsTrue(viewModel.IsSuggestionOpen);
        Assert.AreEqual(2, viewModel.Suggestions.Count);
        Assert.AreSame(viewModel.Suggestions[1], viewModel.SelectedSuggestion);
        Assert.AreEqual("2 slash commands available.", viewModel.StatusAnnouncement);
    }

    [TestMethod]
    public async Task NavigationCommands_WrapAndSkipUnavailableCommands()
    {
        var viewModel = new SlashCommandPresentationViewModel();
        viewModel.ShowSuggestions(
        [
            new SlashCommandSuggestionDescriptor("/review", "Review changes"),
            new SlashCommandSuggestionDescriptor("/disabled", "Unavailable", IsAvailable: false),
            new SlashCommandSuggestionDescriptor("/status", "Show status"),
        ]);

        viewModel.MovePreviousCommand.Execute(null);
        await WaitForAsync(() => viewModel.SelectedSuggestion?.CommandName == "/status");

        viewModel.MoveNextCommand.Execute(null);
        await WaitForAsync(() => viewModel.SelectedSuggestion?.CommandName == "/review");
    }

    [TestMethod]
    public async Task AcceptSuggestion_UsesCommandChipWithoutWritingCommandIntoArgumentText()
    {
        var selected = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var viewModel = new SlashCommandPresentationViewModel();
        viewModel.Configure(
            suggestion =>
            {
                selected.TrySetResult(suggestion.CommandName);
                return Task.CompletedTask;
            },
            _ => Task.FromResult(false));
        viewModel.ShowSuggestions(
        [
            new SlashCommandSuggestionDescriptor(
                "/review",
                "Review changes",
                "Additional review instructions",
                ShowArgumentInput: true),
        ]);

        viewModel.AcceptSuggestionCommand.Execute(null);

        Assert.AreEqual("/review", await selected.Task.WaitAsync(TimeSpan.FromSeconds(2)));
        await WaitForAsync(() => viewModel.HasActiveCommand);
        Assert.IsFalse(viewModel.IsSuggestionOpen);
        Assert.AreEqual("/review", viewModel.ActiveCommand?.CommandName);
        Assert.AreEqual(string.Empty, viewModel.ArgumentText);
        Assert.IsNotNull(viewModel.ExecuteKeyCommand);
        Assert.IsNotNull(viewModel.ClearKeyCommand);
    }

    [TestMethod]
    public async Task FixedOptionAndSuccessfulExecution_ClearOnlyAfterCallbackSucceeds()
    {
        var submitted = new TaskCompletionSource<SlashCommandSubmission>(TaskCreationOptions.RunContinuationsAsynchronously);
        var viewModel = new SlashCommandPresentationViewModel();
        viewModel.Configure(
            _ => Task.CompletedTask,
            submission =>
            {
                submitted.TrySetResult(submission);
                viewModel.ShowStatus("Reasoning effort set to High.");
                return Task.FromResult(true);
            });
        viewModel.ShowSuggestions(
        [
            new SlashCommandSuggestionDescriptor(
                "/reasoning",
                "Choose reasoning effort",
                Options:
                [
                    new SlashCommandOptionDescriptor("low", "Low"),
                    new SlashCommandOptionDescriptor("high", "High"),
                ]),
        ]);
        viewModel.AcceptSuggestionCommand.Execute(null);
        await WaitForAsync(() => viewModel.HasActiveCommand);

        viewModel.Options[1].UseCommand.Execute(null);
        await WaitForAsync(() => viewModel.SelectedOption?.Value == "high");
        StringAssert.Contains(viewModel.Options[1].AutomationName, "selected");
        viewModel.ExecuteCommand.Execute(null);

        SlashCommandSubmission result = await submitted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.AreEqual("/reasoning", result.CommandName);
        Assert.AreEqual("high", result.OptionValue);
        await WaitForAsync(() => !viewModel.HasActiveCommand);
        Assert.AreEqual(0, viewModel.Options.Count);
        Assert.AreEqual(string.Empty, viewModel.ArgumentText);
        Assert.AreEqual("Reasoning effort set to High.", viewModel.StatusAnnouncement);
    }

    [TestMethod]
    public async Task FailedExecution_PreservesChipOptionAndArgument()
    {
        var executed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var viewModel = new SlashCommandPresentationViewModel();
        viewModel.Configure(
            _ => Task.CompletedTask,
            _ =>
            {
                viewModel.ShowFailure("Review could not be started.");
                executed.TrySetResult();
                return Task.FromResult(false);
            });
        viewModel.ShowSuggestions(
        [
            new SlashCommandSuggestionDescriptor(
                "/review",
                "Review changes",
                "Additional review instructions",
                ShowArgumentInput: true,
                Options: [new SlashCommandOptionDescriptor("uncommitted", "Uncommitted changes")]),
        ]);
        viewModel.AcceptSuggestionCommand.Execute(null);
        await WaitForAsync(() => viewModel.HasActiveCommand);
        viewModel.ArgumentText = "Focus on threading.";
        viewModel.Options[0].UseCommand.Execute(null);
        await WaitForAsync(() => viewModel.SelectedOption is not null);

        viewModel.ExecuteCommand.Execute(null);
        await executed.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.IsTrue(viewModel.HasActiveCommand);
        Assert.AreEqual("Focus on threading.", viewModel.ArgumentText);
        Assert.AreEqual("uncommitted", viewModel.SelectedOption?.Value);
        Assert.AreEqual("Review could not be started.", viewModel.StatusAnnouncement);
    }

    [TestMethod]
    public void TypingArgument_DoesNotEchoTextPropertyChangedAcrossRemoteUi()
    {
        var viewModel = new SlashCommandPresentationViewModel();
        var raised = new List<string?>();
        viewModel.PropertyChanged += (_, args) => raised.Add(args.PropertyName);

        viewModel.ArgumentText = "multi-line\ninstructions";

        Assert.AreEqual("multi-line\ninstructions", viewModel.ArgumentText);
        Assert.IsFalse(raised.Contains(nameof(SlashCommandPresentationViewModel.ArgumentText)));
    }

    [TestMethod]
    public void DynamicPresentationText_IsSanitizedBeforeRemoteUiDisplay()
    {
        var viewModel = new SlashCommandPresentationViewModel();

        viewModel.ShowSuggestions(
        [
            new SlashCommandSuggestionDescriptor(
                "/model",
                "<b>Select</b> a model",
                Options: [new SlashCommandOptionDescriptor("raw", "<i>Fast</i>")]),
        ]);
        viewModel.ShowFailure("\u001b[31mFailed\u001b[0m");

        Assert.IsFalse(viewModel.Suggestions[0].Description.Contains('<'));
        Assert.IsFalse(viewModel.StatusAnnouncement.Contains('\u001b'));
    }

    [TestMethod]
    public void Xaml_SlashSuggestionsAreInlineBoundedAndKeyboardAccessible()
    {
        XDocument document = LoadXaml();
        XElement list = document
            .Descendants(Presentation + "ListBox")
            .Single(element => element.Attribute("ItemsSource")?.Value == "{Binding SlashCommands.Suggestions}");

        Assert.IsNull(
            list.Ancestors(Presentation + "Popup").FirstOrDefault(),
            "Slash-command suggestions must remain inline rather than using a Popup.");
        Assert.AreEqual("198", list.Attribute("MaxHeight")?.Value);
        Assert.AreEqual("Auto", list.Attribute("ScrollViewer.VerticalScrollBarVisibility")?.Value);

        XElement composer = document
            .Descendants(Presentation + "TextBox")
            .Single(element => (element.Attribute("Text")?.Value ?? string.Empty)
                .Contains("ComposerText", StringComparison.Ordinal));
        XElement[] bindings = composer
            .Element(Presentation + "TextBox.InputBindings")!
            .Elements(Presentation + "KeyBinding")
            .ToArray();

        AssertKeyBinding(bindings, "Up", null, "{Binding SlashCommands.MovePreviousKeyCommand}");
        AssertKeyBinding(bindings, "Down", null, "{Binding SlashCommands.MoveNextKeyCommand}");
        AssertKeyBinding(bindings, "Return", null, "{Binding SlashCommands.AcceptSuggestionKeyCommand}");
        AssertKeyBinding(bindings, "Tab", null, "{Binding SlashCommands.AcceptSuggestionKeyCommand}");
        AssertKeyBinding(bindings, "Escape", null, "{Binding SlashCommands.DismissSuggestionsKeyCommand}");
        AssertKeyBinding(bindings, "Return", "Control", "{Binding SendCommand}");
        Assert.AreEqual("True", composer.Attribute("AcceptsReturn")?.Value);
    }

    private static void AssertKeyBinding(
        IEnumerable<XElement> bindings,
        string key,
        string? modifiers,
        string command)
    {
        int matchCount = bindings.Count(binding =>
            binding.Attribute("Key")?.Value == key
            && binding.Attribute("Modifiers")?.Value == modifiers
            && binding.Attribute("Command")?.Value == command);
        string failureReason = matchCount == 0
            ? "the binding is missing"
            : $"the binding is duplicated ({matchCount} matches)";

        Assert.AreEqual(
            1,
            matchCount,
            $"Expected exactly one KeyBinding for Key='{key}', Modifiers='{modifiers ?? "<none>"}', "
                + $"Command='{command}', but {failureReason}.");
    }

    [TestMethod]
    public void Xaml_SlashSuggestionRowsUsePairedVsThemeBrushes()
    {
        XDocument document = LoadXaml();
        XName xKey = XName.Get("Key", "http://schemas.microsoft.com/winfx/2006/xaml");
        XElement rowStyle = document
            .Descendants(Presentation + "Style")
            .Single(element => element.Attribute(xKey)?.Value == "SelectableRowStyle");

        XElement hoverTrigger = rowStyle
            .Descendants(Presentation + "Trigger")
            .Single(element => element.Attribute("Property")?.Value == "IsMouseOver");
        XElement selectedTrigger = rowStyle
            .Descendants(Presentation + "Trigger")
            .Single(element => element.Attribute("Property")?.Value == "IsSelected");

        AssertThemeBrushPair(
            hoverTrigger,
            "ToolWindowButtonHoverActiveBrushKey",
            "ToolWindowButtonHoverActiveGlyphBrushKey");
        AssertThemeBrushPair(
            selectedTrigger,
            "ToolWindowButtonDownBrushKey",
            "ToolWindowButtonDownActiveGlyphBrushKey");

        XElement list = document
            .Descendants(Presentation + "ListBox")
            .Single(element => element.Attribute("ItemsSource")?.Value == "{Binding SlashCommands.Suggestions}");
        XElement[] suggestionText = list
            .Descendants(Presentation + "TextBlock")
            .Where(element => element.Attribute("Text")?.Value is "{Binding CommandName}" or "{Binding Description}")
            .ToArray();

        Assert.HasCount(2, suggestionText);
        Assert.IsTrue(suggestionText.All(element => element.Attribute("Style")?.Value == "{x:Null}"));

        XElement suggestionButtonStyle = document
            .Descendants(Presentation + "Style")
            .Single(element => element.Attribute(xKey)?.Value == "SlashSuggestionButtonStyle");
        XElement normalForeground = suggestionButtonStyle
            .Elements(Presentation + "Setter")
            .Single(element => element.Attribute("Property")?.Value == "Foreground");
        Assert.IsTrue(normalForeground.Attribute("Value")?.Value?.Contains(
            "ToolWindowTextBrushKey",
            StringComparison.Ordinal) == true);

        XElement[] buttonStateTriggers = suggestionButtonStyle
            .Descendants(Presentation + "DataTrigger")
            .ToArray();
        XElement buttonHoverTrigger = buttonStateTriggers
            .Single(element => element.Attribute("Binding")?.Value?.Contains("IsMouseOver", StringComparison.Ordinal) == true);
        XElement buttonSelectedTrigger = buttonStateTriggers
            .Single(element => element.Attribute("Binding")?.Value?.Contains("IsSelected", StringComparison.Ordinal) == true);
        Assert.AreEqual("True", buttonHoverTrigger.Attribute("Value")?.Value);
        Assert.AreEqual("True", buttonSelectedTrigger.Attribute("Value")?.Value);
        Assert.IsTrue(buttonHoverTrigger.Attribute("Binding")?.Value?.Contains(
            "RelativeSource AncestorType={x:Type ListBoxItem}",
            StringComparison.Ordinal) == true);
        Assert.IsTrue(buttonSelectedTrigger.Attribute("Binding")?.Value?.Contains(
            "RelativeSource AncestorType={x:Type ListBoxItem}",
            StringComparison.Ordinal) == true);
        Assert.IsTrue(Array.IndexOf(buttonStateTriggers, buttonSelectedTrigger)
            > Array.IndexOf(buttonStateTriggers, buttonHoverTrigger));
        AssertThemeForeground(buttonHoverTrigger, "ToolWindowButtonHoverActiveGlyphBrushKey");
        AssertThemeForeground(buttonSelectedTrigger, "ToolWindowButtonDownActiveGlyphBrushKey");

        XElement description = suggestionText
            .Single(element => element.Attribute("Text")?.Value == "{Binding Description}");
        Assert.IsNull(description.Attribute("Opacity"));
    }

    [TestMethod]
    public void ClosedSuggestions_RemoveKeyCommandsSoNormalEditorGesturesContinue()
    {
        var viewModel = new SlashCommandPresentationViewModel();

        Assert.IsNull(viewModel.MovePreviousKeyCommand);
        Assert.IsNull(viewModel.MoveNextKeyCommand);
        Assert.IsNull(viewModel.AcceptSuggestionKeyCommand);
        Assert.IsNull(viewModel.DismissSuggestionsKeyCommand);

        viewModel.ShowSuggestions([new SlashCommandSuggestionDescriptor("/status", "Show status")]);

        Assert.IsNotNull(viewModel.MovePreviousKeyCommand);
        Assert.IsNotNull(viewModel.MoveNextKeyCommand);
        Assert.IsNotNull(viewModel.AcceptSuggestionKeyCommand);
        Assert.IsNotNull(viewModel.DismissSuggestionsKeyCommand);

        viewModel.CloseSuggestions();

        Assert.IsFalse(viewModel.HasStatusAnnouncement);
    }

    [TestMethod]
    public void Xaml_SelectedCommandUsesChipOptionsArgumentsAndLiveStatus()
    {
        XDocument document = LoadXaml();

        Assert.IsNotNull(document
            .Descendants(Presentation + "TextBlock")
            .SingleOrDefault(element => element.Attribute("Text")?.Value == "{Binding SlashCommands.ActiveCommand.CommandName}"));
        Assert.IsNotNull(document
            .Descendants(Presentation + "ItemsControl")
            .SingleOrDefault(element => element.Attribute("ItemsSource")?.Value == "{Binding SlashCommands.Options}"));
        Assert.IsNotNull(document
            .Descendants(Presentation + "TextBlock")
            .SingleOrDefault(element =>
                element.Attribute("Text")?.Value == "Selected"
                && element.Attribute("Visibility")?.Value?.Contains("IsSelected", StringComparison.Ordinal) == true));
        Assert.IsNotNull(document
            .Descendants(Presentation + "TextBox")
            .SingleOrDefault(element => element.Attribute("Text")?.Value?.Contains("SlashCommands.ArgumentText", StringComparison.Ordinal) == true));

        XElement status = document
            .Descendants(Presentation + "TextBlock")
            .Single(element => element.Attribute("Text")?.Value == "{Binding SlashCommands.StatusAnnouncement}");
        XName automationLiveSetting = XName.Get("AutomationProperties.LiveSetting");
        Assert.AreEqual("Polite", status.Attribute(automationLiveSetting)?.Value);

        Assert.IsNotNull(document
            .Descendants(Presentation + "KeyBinding")
            .SingleOrDefault(element =>
                element.Attribute("Key")?.Value == "Return"
                && element.Attribute("Modifiers")?.Value == "Control"
                && element.Attribute("Command")?.Value == "{Binding SlashCommands.ExecuteKeyCommand}"));
        Assert.IsNotNull(document
            .Descendants(Presentation + "KeyBinding")
            .SingleOrDefault(element =>
                element.Attribute("Key")?.Value == "Escape"
                && element.Attribute("Command")?.Value == "{Binding SlashCommands.ClearKeyCommand}"));
    }

    [TestMethod]
    public void Xaml_ComposerRemainsUsableAtNarrowToolWindowWidths()
    {
        XDocument document = LoadXaml();
        XElement rootGrid = document.Root!.Element(Presentation + "Grid")!;

        Assert.AreEqual("360", rootGrid.Attribute("MinWidth")?.Value);
        XElement actionRow = document
            .Descendants(Presentation + "Grid")
            .Single(element => element.Attribute("Margin")?.Value == "6,0,6,6");
        Assert.IsNotNull(actionRow.Element(Presentation + "Grid.ColumnDefinitions"));
        Assert.IsNotNull(actionRow.Elements(Presentation + "WrapPanel").SingleOrDefault());
    }

    [TestMethod]
    public void Xaml_PermissionsPickerUsesStableValueThemeAndAccessibleWarning()
    {
        XDocument document = LoadXaml();
        XName automationName = XName.Get("AutomationProperties.Name");
        XName automationHelpText = XName.Get("AutomationProperties.HelpText");

        XElement picker = document
            .Descendants(Presentation + "ComboBox")
            .Single(element => element.Attribute(automationName)?.Value == "Permissions");
        Assert.AreEqual("DisplayText", picker.Attribute("DisplayMemberPath")?.Value);
        Assert.AreEqual("Id", picker.Attribute("SelectedValuePath")?.Value);
        StringAssert.Contains(picker.Attribute("SelectedValue")?.Value, "SelectedApprovalModeId");
        Assert.AreEqual("{Binding IsApprovalModeEnabled}", picker.Attribute("IsEnabled")?.Value);
        Assert.AreEqual("{Binding ApprovalModeHelpText}", picker.Attribute(automationHelpText)?.Value);

        XElement warning = document
            .Descendants(Presentation + "TextBlock")
            .Single(element => element.Attribute(automationName)?.Value == "Permission mode warning");
        Assert.AreEqual("{Binding ApprovalModeConfirmationText}", warning.Attribute(automationHelpText)?.Value);
        Assert.AreEqual("Assertive", warning.Attribute(XName.Get("AutomationProperties.LiveSetting"))?.Value);

        XElement confirmation = warning.Ancestors(Presentation + "Border").First();
        StringAssert.Contains(confirmation.Attribute("Background")?.Value, "ToolWindowBackgroundBrushKey");
        StringAssert.Contains(confirmation.Attribute("BorderBrush")?.Value, "ToolWindowBorderBrushKey");
    }

    private static XDocument LoadXaml()
    {
        using Stream? stream = typeof(ChatViewModel).Assembly.GetManifestResourceStream(ResourceName);
        Assert.IsNotNull(stream, $"Embedded resource '{ResourceName}' not found.");
        return XDocument.Load(stream);
    }

    private static void AssertThemeBrushPair(
        XElement trigger,
        string expectedBackgroundKey,
        string expectedForegroundKey)
    {
        Dictionary<string, string?> setters = trigger
            .Elements(Presentation + "Setter")
            .ToDictionary(
                setter => setter.Attribute("Property")?.Value ?? string.Empty,
                setter => setter.Attribute("Value")?.Value);

        Assert.IsTrue(setters["Background"]?.Contains(expectedBackgroundKey, StringComparison.Ordinal) == true);
        Assert.IsTrue(setters["Foreground"]?.Contains(expectedForegroundKey, StringComparison.Ordinal) == true);
    }

    private static void AssertThemeForeground(XElement trigger, string expectedForegroundKey)
    {
        XElement foregroundSetter = trigger
            .Elements(Presentation + "Setter")
            .Single(element => element.Attribute("Property")?.Value == "Foreground");

        Assert.IsTrue(foregroundSetter.Attribute("Value")?.Value?.Contains(
            expectedForegroundKey,
            StringComparison.Ordinal) == true);
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
