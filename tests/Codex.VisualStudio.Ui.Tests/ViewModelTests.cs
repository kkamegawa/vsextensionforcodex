using System.IO;
using System.Reflection;
using System.Runtime.Serialization;
using System.Text.RegularExpressions;
using System.Windows.Input;
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
        Assert.IsTrue(xaml.Contains("{Binding AccountDisplayText}", StringComparison.Ordinal));
        Assert.IsFalse(xaml.Contains("{Binding Account.", StringComparison.Ordinal));
    }

    [TestMethod]
    public void ChatToolWindowXaml_ComposerBindsCtrlEnterToSendCommand()
    {
        // Issue #5: Ctrl+Enter must submit the composer message via the same SendCommand
        // path as the Send/Steer button, while plain Enter keeps inserting a newline
        // (AcceptsReturn="True"). KeyBinding honours SendCommand.CanExecute, so it is a
        // no-op on empty/disabled input.
        const string resourceName = "Codex.VisualStudio.Extension.ToolWindows.ChatToolWindowContent.xaml";
        using Stream? stream = typeof(ChatViewModel).Assembly.GetManifestResourceStream(resourceName);
        Assert.IsNotNull(stream, $"Embedded resource '{resourceName}' not found.");
        using var reader = new StreamReader(stream);
        string xaml = reader.ReadToEnd();

        Assert.IsTrue(
            Regex.IsMatch(
                xaml,
                @"<KeyBinding\s+Gesture=""Ctrl\+Enter""\s+Command=""\{Binding\s+SendCommand\}""",
                RegexOptions.IgnoreCase),
            "Composer TextBox must bind Ctrl+Enter to SendCommand via a KeyBinding.");
        Assert.IsTrue(
            xaml.Contains("AcceptsReturn=\"True\"", StringComparison.Ordinal),
            "Composer TextBox must keep AcceptsReturn=\"True\" so plain Enter inserts a newline.");
    }

    // Remote UI replicates only [DataMember] properties of [DataContract] types into the
    // VS-side data context proxy; a type without the attributes serializes as an empty
    // object and every binding to it fails silently (blank text, empty button content).
    private static readonly Type[] RemoteUiContextTypes =
        [typeof(ChatViewModel), typeof(ChatItemViewModel), typeof(ApprovalViewModel), typeof(WorkerStatus), typeof(ThreadSummary)];

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
