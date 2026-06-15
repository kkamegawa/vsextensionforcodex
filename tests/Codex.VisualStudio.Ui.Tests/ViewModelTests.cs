using System.IO;
using System.Reflection;
using System.Runtime.Serialization;
using System.Text.RegularExpressions;
using System.Windows.Input;
using System.Xml.Linq;
using Codex.VisualStudio.Contracts;
using Codex.VisualStudio.Extension;
using Microsoft.VisualStudio.Extensibility.UI;
using AsyncCommand = Codex.VisualStudio.Extension.AsyncCommand;

namespace Codex.VisualStudio.Ui.Tests;

[TestClass]
public sealed class ViewModelTests
{
    private static readonly ApprovalDecision[] AcceptDeclineCancel =
        [ApprovalDecision.Accept, ApprovalDecision.Decline, ApprovalDecision.Cancel];

    private static readonly ApprovalDecision[] AcceptCancel =
        [ApprovalDecision.Accept, ApprovalDecision.Cancel];

    private static readonly ApprovalDecision[] AcceptDecline =
        [ApprovalDecision.Accept, ApprovalDecision.Decline];

    private static readonly ApprovalDecision[] NoDecisions = [];

    private static readonly string[] ExpectedModes = ["Agent", "Chat"];
    [TestMethod]
    public async Task ApprovalViewModel_ResolvesOnlyOnce()
    {
        int calls = 0;
        var viewModel = new ApprovalViewModel(
            new ApprovalRequest
            {
                RequestId = "request-1",
                Risk = ApprovalRiskCategory.Destructive,
                DisplayText = "git reset --hard",
                AvailableDecisions = AcceptDeclineCancel,
            },
            (_, _) =>
            {
                Interlocked.Increment(ref calls);
                return Task.CompletedTask;
            });

        viewModel.AcceptCommand.Execute(null);
        viewModel.DeclineCommand.Execute(null);
        await Task.Delay(100);

        Assert.AreEqual(1, calls);
        Assert.IsTrue(viewModel.IsResolved);
    }

    [TestMethod]
    public void SafeMarkdown_RemovesHtmlAnsiAndControlCharacters()
    {
        var service = new SafeMarkdownService();

        // Contains HTML tag, ANSI escape sequences, and a control character.
        string input = "<script>bad()</script> **safe** \x1b[31mred\x1b[0m \x00";
        string text = service.ToSafeText(input);

        Assert.IsFalse(text.Contains("<script>", StringComparison.Ordinal));
        Assert.IsFalse(text.Contains('\x1b'));
        Assert.IsFalse(text.Contains('\x00'));
        Assert.IsTrue(text.Contains("safe", StringComparison.Ordinal));
    }

    [TestMethod]
    public void ApprovalViewModel_AvailableDecisions_ControlButtonVisibility()
    {
        var request = new ApprovalRequest
        {
            RequestId = "req-2",
            Risk = ApprovalRiskCategory.ReadOnly,
            DisplayText = "read file",
            AvailableDecisions = AcceptCancel,
        };
        var vm = new ApprovalViewModel(request, (_, _) => Task.CompletedTask);

        Assert.IsTrue(vm.ShowAccept);
        Assert.IsFalse(vm.ShowAcceptForTurn);
        Assert.IsFalse(vm.ShowAcceptForThread);
        Assert.IsFalse(vm.ShowAcceptForSession);
        Assert.IsFalse(vm.ShowDecline);
        Assert.IsTrue(vm.ShowCancel);
    }

    [TestMethod]
    public void ApprovalViewModel_NetworkApproval_ParsesHostAndPort()
    {
        var request = new ApprovalRequest
        {
            RequestId = "req-3",
            Risk = ApprovalRiskCategory.Network,
            RiskKey = "network:api.example.com:443",
            DisplayText = "api.example.com",
            AvailableDecisions = AcceptDecline,
        };
        var vm = new ApprovalViewModel(request, (_, _) => Task.CompletedTask);

        Assert.IsTrue(vm.IsNetworkApproval);
        Assert.AreEqual("api.example.com", vm.NetworkHost);
        Assert.AreEqual("443", vm.NetworkPort);
    }

    [TestMethod]
    public void ApprovalViewModel_PolicyBlocked_NoButtonsAvailable()
    {
        var request = new ApprovalRequest
        {
            RequestId = "req-4",
            Risk = ApprovalRiskCategory.Destructive,
            DisplayText = "rm -rf /",
            IsPolicyBlocked = true,
            PolicyBlockReason = "Destructive commands are blocked by policy.",
            AvailableDecisions = NoDecisions,
        };
        var vm = new ApprovalViewModel(request, (_, _) => Task.CompletedTask);

        Assert.IsFalse(vm.ShowAccept);
        Assert.IsFalse(vm.ShowAcceptForSession);
        Assert.IsFalse(vm.ShowDecline);
        Assert.IsFalse(vm.ShowCancel);
        Assert.IsFalse(vm.CanResolve);
    }

    [TestMethod]
    public void ChatItemViewModel_ReasoningItem_StartsCollapsed()
    {
        var item = new ChatItemViewModel("Reasoning", "some reasoning text", ConversationEventKind.ReasoningSummaryDelta);

        Assert.IsTrue(item.IsCollapsed);
        Assert.IsTrue(item.IsReasoningItem);
        Assert.IsFalse(item.IsCommandItem);
        Assert.AreEqual("▶ Reasoning", item.CollapseButtonText);
    }

    [TestMethod]
    public async Task ChatItemViewModel_ToggleCollapse_ChangesState()
    {
        var item = new ChatItemViewModel("Reasoning", "text", ConversationEventKind.ReasoningSummaryDelta);
        Assert.IsTrue(item.IsCollapsed);

        item.ToggleCollapseCommand.Execute(null);
        await Task.Delay(50);

        Assert.IsFalse(item.IsCollapsed);
        Assert.AreEqual("▼ Reasoning", item.CollapseButtonText);
    }

    [TestMethod]
    public void ChatItemViewModel_ParsePlanSteps_ExtractsStepTitles()
    {
        string json = """{"steps":[{"title":"Step 1"},{"title":"Step 2"},{"title":"Step 3"}]}""";
        IReadOnlyList<string> steps = ChatItemViewModel.ParsePlanSteps(json);

        Assert.AreEqual(3, steps.Count);
        Assert.AreEqual("Step 1", steps[0]);
        Assert.AreEqual("Step 3", steps[2]);
    }

    [TestMethod]
    public void ChatItemViewModel_ParsePlanSteps_ReturnEmptyOnMalformed()
    {
        IReadOnlyList<string> steps = ChatItemViewModel.ParsePlanSteps("not-json");
        Assert.AreEqual(0, steps.Count);
    }

    [TestMethod]
    public void ChatItemViewModel_UpdatePlanSteps_PrefixesBullet()
    {
        var item = new ChatItemViewModel("Plan", string.Empty, ConversationEventKind.PlanUpdated);
        item.UpdatePlanSteps(["Do A", "Do B"]);

        Assert.AreEqual(2, item.PlanSteps.Count);
        Assert.IsTrue(item.PlanSteps[0].StartsWith('•'));
    }

    [TestMethod]
    public void ChatItemViewModel_CommandItem_KindFlags()
    {
        var item = new ChatItemViewModel("Command", "output", ConversationEventKind.CommandOutputDelta);

        Assert.IsTrue(item.IsCommandItem);
        Assert.IsFalse(item.IsReasoningItem);
        Assert.IsFalse(item.IsDiffItem);
        Assert.IsFalse(item.IsPlanItem);
        Assert.IsFalse(item.IsCollapsed);
    }

    [TestMethod]
    public void AccountPanelViewModel_MapsAccountStatesToDisplayText()
    {
        var viewModel = new AccountPanelViewModel();

        viewModel.Update(new AccountStatus { State = AccountState.SignedOut });
        Assert.AreEqual("Not signed in", viewModel.DisplayText);
        Assert.IsTrue(viewModel.ShowSignIn);
        Assert.IsTrue(viewModel.ShowAction);
        Assert.AreEqual("Sign in", viewModel.ActionText);

        viewModel.Update(new AccountStatus { State = AccountState.SigningIn });
        Assert.AreEqual("Signing in...", viewModel.DisplayText);
        Assert.IsFalse(viewModel.ShowSignIn);
        Assert.IsFalse(viewModel.ShowAction);

        viewModel.Update(new AccountStatus { State = AccountState.SignedIn, PlanType = "plus" });
        Assert.AreEqual("Signed in \u00b7 plus", viewModel.DisplayText);
        Assert.IsFalse(viewModel.ShowSignIn);
        Assert.IsTrue(viewModel.ShowAction);
        Assert.IsTrue(viewModel.IsSignedIn);
        Assert.AreEqual("Sign out", viewModel.ActionText);

        viewModel.Update(new AccountStatus { State = AccountState.Unavailable });
        Assert.AreEqual("Account status unavailable", viewModel.DisplayText);
        Assert.IsTrue(viewModel.ShowSignIn);
        Assert.IsTrue(viewModel.ShowAction);
        Assert.AreEqual("Sign in", viewModel.ActionText);

        viewModel.Update(new AccountStatus { State = AccountState.Unavailable, Message = "Could not open the default browser." });
        Assert.AreEqual("Account status unavailable \u00b7 Could not open the default browser.", viewModel.DisplayText);
    }

    [TestMethod]
    public void ChatToolWindowXaml_UsesTopLevelAccountBindings()
    {
        const string resourceName = "Codex.VisualStudio.Extension.ToolWindows.ChatToolWindowContent.xaml";
        using Stream? stream = typeof(ChatViewModel).Assembly.GetManifestResourceStream(resourceName);
        Assert.IsNotNull(stream, $"Embedded resource '{resourceName}' not found.");
        using var reader = new StreamReader(stream);
        string xaml = reader.ReadToEnd();

        Assert.IsTrue(xaml.Contains("{Binding AccountActionText}", StringComparison.Ordinal));
        Assert.IsTrue(xaml.Contains("{Binding ShowAccountAction,", StringComparison.Ordinal));
        Assert.IsTrue(xaml.Contains("{Binding StatusDetailText}", StringComparison.Ordinal));
        Assert.IsFalse(xaml.Contains("{Binding Account.", StringComparison.Ordinal));
    }

    [TestMethod]
    public void ChatToolWindowXaml_ComposerBindsCtrlEnterToSendCommand()
    {
        // Regression: the redesigned composer uses an icon Send button (not IsDefault), so the
        // only keyboard send affordance is a Ctrl+Enter KeyBinding, while plain Enter keeps
        // inserting a newline (AcceptsReturn="True"). KeyBinding honours SendCommand.CanExecute,
        // so it is a no-op on empty/disabled input. Lock the gesture wiring so it cannot
        // silently disappear again.
        const string resourceName = "Codex.VisualStudio.Extension.ToolWindows.ChatToolWindowContent.xaml";
        using Stream? stream = typeof(ChatViewModel).Assembly.GetManifestResourceStream(resourceName);
        Assert.IsNotNull(stream, $"Embedded resource '{resourceName}' not found.");
        XDocument doc = XDocument.Load(stream);
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";

        // Locate the composer specifically (the TextBox bound to ComposerText), so the
        // assertions can't be satisfied by some other TextBox/KeyBinding elsewhere.
        XElement? composer = doc
            .Descendants(presentation + "TextBox")
            .SingleOrDefault(tb => (tb.Attribute("Text")?.Value ?? string.Empty)
                .Contains("ComposerText", StringComparison.Ordinal));
        Assert.IsNotNull(composer, "Could not find the composer TextBox bound to ComposerText.");

        // Plain Enter must keep inserting a newline.
        Assert.AreEqual(
            "True",
            composer!.Attribute("AcceptsReturn")?.Value,
            "Composer TextBox must keep AcceptsReturn=\"True\" so plain Enter inserts a newline.");

        // Ctrl+Enter (scoped to the composer via TextBox.InputBindings) invokes SendCommand.
        // Attribute values are matched case-sensitively (XAML is case-sensitive).
        XElement? keyBinding = composer
            .Element(presentation + "TextBox.InputBindings")?
            .Elements(presentation + "KeyBinding")
            .SingleOrDefault(kb => kb.Attribute("Key")?.Value == "Return" && kb.Attribute("Modifiers")?.Value == "Control");
        Assert.IsNotNull(keyBinding, "Composer TextBox.InputBindings must contain a Ctrl+Enter (Key=Return, Modifiers=Control) KeyBinding.");
        Assert.AreEqual(
            "{Binding SendCommand}",
            keyBinding!.Attribute("Command")?.Value,
            "The Ctrl+Enter KeyBinding must invoke SendCommand.");
    }

    // Remote UI replicates only [DataMember] properties of [DataContract] types into the
    // VS-side data context proxy; a type without the attributes serializes as an empty
    // object and every binding to it fails silently (blank text, empty button content).
    private static readonly Type[] RemoteUiContextTypes =
        [typeof(ChatViewModel), typeof(ChatItemViewModel), typeof(ApprovalViewModel), typeof(SuggestionChip), typeof(WorkerStatus), typeof(ThreadSummary)];

    [TestMethod]
    public void RemoteUiContextTypes_AreDataContracts()
    {
        foreach (Type type in RemoteUiContextTypes)
        {
            Assert.IsNotNull(
                type.GetCustomAttribute<DataContractAttribute>(),
                $"{type.Name} is bound by ChatToolWindowContent.xaml and must be [DataContract] for Remote UI.");
        }
    }

    [TestMethod]
    public void ChatToolWindowXaml_EveryBindingRoot_IsSerializableDataMember()
    {
        const string resourceName = "Codex.VisualStudio.Extension.ToolWindows.ChatToolWindowContent.xaml";
        using Stream? stream = typeof(ChatViewModel).Assembly.GetManifestResourceStream(resourceName);
        Assert.IsNotNull(stream, $"Embedded resource '{resourceName}' not found.");
        using var reader = new StreamReader(stream);
        string xaml = reader.ReadToEnd();

        var dataMemberNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (Type type in RemoteUiContextTypes)
        {
            foreach (PropertyInfo property in type.GetProperties(BindingFlags.Instance | BindingFlags.Public))
            {
                if (property.GetCustomAttribute<DataMemberAttribute>() is not null)
                    dataMemberNames.Add(property.Name);
            }
        }

        // First path segment of every {Binding Foo...} expression ({Binding} alone has no name).
        foreach (Match match in Regex.Matches(xaml, @"\{Binding\s+([A-Za-z_]\w*)"))
        {
            string root = match.Groups[1].Value;
            Assert.IsTrue(
                dataMemberNames.Contains(root),
                $"XAML binds '{root}' but no Remote UI context type exposes it as a [DataMember] property.");
        }
    }

    [TestMethod]
    public void AsyncCommand_CanExecute_IsResolvableViaPublicReflection()
    {
        // NotificationsDispatcher.HandleNotifyPropertyChanged resolves the changed property
        // with sender.GetType().GetProperty(name) and THROWS when the lookup fails. An
        // explicit interface implementation of IAsyncCommand.CanExecute is invisible to that
        // lookup; the resulting ArgumentException froze the worker RPC dispatch loop.
        var command = new AsyncCommand(() => Task.CompletedTask);

        PropertyInfo? property = command.GetType().GetProperty("CanExecute");
        Assert.IsNotNull(property, "AsyncCommand.CanExecute must be a public property.");
        Assert.AreEqual(typeof(bool), property!.PropertyType);
        Assert.IsTrue((bool)property.GetValue(command)!);
    }

    [TestMethod]
    public void AsyncCommand_RaiseCanExecuteChanged_RaisesPropertyChangedForCanExecute()
    {
        bool gate = false;
        var command = new AsyncCommand(() => Task.CompletedTask, () => gate);
        string? raisedProperty = null;
        command.PropertyChanged += (_, e) => raisedProperty = e.PropertyName;

        gate = true;
        command.RaiseCanExecuteChanged();

        Assert.AreEqual("CanExecute", raisedProperty);
        Assert.IsTrue(command.CanExecute);
    }

    [TestMethod]
    public async Task SuggestionChip_UseCommand_InvokesCallbackWithText()
    {
        string? captured = null;
        var chip = new SuggestionChip("Write unit tests for this file", text =>
        {
            captured = text;
            return Task.CompletedTask;
        });

        chip.UseCommand.Execute(null);
        await Task.Delay(50);

        Assert.AreEqual("Write unit tests for this file", chip.Text);
        Assert.AreEqual("Write unit tests for this file", captured);
    }

    [TestMethod]
    public void ChatViewModel_NewInstance_SeedsWelcomeState()
    {
        // Construction fires a fire-and-forget ConnectAsync; the worker exe is absent from the
        // test output so it fails fast and is caught (no process, no hang). We only assert the
        // synchronously-seeded welcome-state members here.
        using var vm = new ChatViewModel();

        Assert.IsTrue(vm.IsThreadEmpty);
        Assert.IsTrue(vm.IsComposerEmpty);
        Assert.IsFalse(vm.IsHistoryOpen);
        Assert.AreEqual(3, vm.Suggestions.Count);
        Assert.IsTrue(vm.Models.Count > 0);
        Assert.AreEqual(vm.Models[0], vm.SelectedModel);
        CollectionAssert.AreEqual(ExpectedModes, vm.Modes);
        Assert.AreEqual("Agent", vm.SelectedMode);
    }

    [TestMethod]
    [DataRow("my-app", "my_app")]
    [DataRow("123", "_123")]
    [DataRow("---", "App")]
    [DataRow("valid_Name9", "valid_Name9")]
    public void ProjectScaffolder_GetProjectName_ReturnsIdentifierLikeName(string folderName, string expected)
    {
        MethodInfo? getProjectName = typeof(ProjectScaffolder).GetMethod(
            "GetProjectName",
            BindingFlags.NonPublic | BindingFlags.Static);

        Assert.IsNotNull(getProjectName);
        string rootDirectory = Path.Combine(Path.GetTempPath(), folderName);
        Assert.AreEqual(expected, getProjectName.Invoke(null, [rootDirectory]));
    }

    [TestMethod]
    public void ChatViewModel_IsThreadEmpty_TracksItems()
    {
        using var vm = new ChatViewModel();
        Assert.IsTrue(vm.IsThreadEmpty);

        vm.Items.Add(new ChatItemViewModel("You", "hello", ConversationEventKind.ItemStarted));
        Assert.IsFalse(vm.IsThreadEmpty);

        vm.Items.Clear();
        Assert.IsTrue(vm.IsThreadEmpty);
    }

    [TestMethod]
    public async Task ChatViewModel_ToggleHistoryCommand_TogglesIsHistoryOpen()
    {
        using var vm = new ChatViewModel();
        Assert.IsFalse(vm.IsHistoryOpen);

        vm.ToggleHistoryCommand.Execute(null);
        await Task.Delay(50);
        Assert.IsTrue(vm.IsHistoryOpen);

        vm.ToggleHistoryCommand.Execute(null);
        await Task.Delay(50);
        Assert.IsFalse(vm.IsHistoryOpen);
    }

    [TestMethod]
    public void ChatViewModel_TypingComposerText_DoesNotEchoComposerTextPropertyChanged()
    {
        // Regression: the binding-driven (user-typing) setter must NOT raise PropertyChanged for
        // ComposerText. In Remote UI that notification is echoed back to the TextBox across the
        // process boundary and resets the caret to position 0 on every keystroke. IsComposerEmpty
        // must still update so the "Ask Codex" placeholder hides.
        using var vm = new ChatViewModel();
        var raised = new List<string?>();
        vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        vm.ComposerText = "hello";

        Assert.AreEqual("hello", vm.ComposerText);
        Assert.IsFalse(
            raised.Contains("ComposerText"),
            "Typing must not echo ComposerText PropertyChanged (would reset the caret in Remote UI).");
        Assert.IsTrue(raised.Contains("IsComposerEmpty"), "Placeholder visibility must still update.");
        Assert.IsFalse(vm.IsComposerEmpty);
    }

    [TestMethod]
    public async Task ChatViewModel_SuggestionChip_RaisesComposerTextPropertyChanged()
    {
        // The programmatic path (suggestion chip / clear-after-send) DOES notify ComposerText so
        // the TextBox reflects the new value; the caret-reset concern does not apply off the
        // typing path.
        using var vm = new ChatViewModel();
        var raised = new List<string?>();
        vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        vm.Suggestions[0].UseCommand.Execute(null);
        await Task.Delay(50);

        Assert.AreEqual(vm.Suggestions[0].Text, vm.ComposerText);
        Assert.IsTrue(
            raised.Contains("ComposerText"),
            "Programmatic composer updates must notify so the TextBox reflects the new value.");
    }

    [TestMethod]
    public void DataMemberCommands_ImplementIAsyncCommand()
    {
        // Remote UI rejects plain ICommand values at serialization time
        // ("ICommand is not supported, please implement IAsyncCommand instead").
        foreach (Type type in RemoteUiContextTypes)
        {
            foreach (PropertyInfo property in type.GetProperties(BindingFlags.Instance | BindingFlags.Public))
            {
                if (property.GetCustomAttribute<DataMemberAttribute>() is null)
                    continue;
                if (!typeof(ICommand).IsAssignableFrom(property.PropertyType))
                    continue;

                Assert.IsTrue(
                    typeof(IAsyncCommand).IsAssignableFrom(property.PropertyType),
                    $"{type.Name}.{property.Name} is a serialized command and must implement IAsyncCommand for Remote UI.");
            }
        }
    }
}
