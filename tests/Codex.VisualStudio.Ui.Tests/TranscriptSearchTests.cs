using Codex.VisualStudio.Contracts;
using Codex.VisualStudio.Extension;
using System.IO;
using System.Reflection;

namespace Codex.VisualStudio.Ui.Tests;

public sealed partial class ViewModelTests
{
    [TestMethod]
    public async Task TranscriptSearch_ReadsEveryPageAndPublishesCompleteResults()
    {
        int pageCount = 0;
        var bridge = new FakeWorkerBridge
        {
            ListThreadItemsHandler = (request, _) =>
            {
                pageCount++;
                return Task.FromResult(request.Cursor is null
                    ? new ThreadItemsPage
                    {
                        Items = [new ThreadHistoryItem { Id = "recent", TurnId = "turn-2", Type = "agentMessage", Text = "match in recent" }],
                        NextCursor = "older",
                    }
                    : new ThreadItemsPage
                    {
                        Items = [new ThreadHistoryItem { Id = "old", TurnId = "turn-1", Type = "userMessage", Text = "match in older" }],
                    });
            },
        };
        using ChatViewModel vm = await CreateJoinedViewModelAsync(bridge);
        ChatItemViewModel[] originalTranscript = vm.Items.ToArray();
        int pagesBeforeSearch = pageCount;
        vm.TranscriptSearchText = "match";

        await RunCommandAsync(vm.SearchTranscriptCommand);

        Assert.AreEqual(2, pageCount - pagesBeforeSearch);
        Assert.AreEqual(2, vm.TranscriptSearchResults.Count);
        Assert.AreEqual("old", vm.TranscriptSearchResults[0].ItemId);
        Assert.AreEqual("recent", vm.TranscriptSearchResults[1].ItemId);
        Assert.AreEqual("match in older", vm.TranscriptSearchResults[0].Text.TrimEnd());
        Assert.AreEqual("match in recent", vm.TranscriptSearchResults[1].Text.TrimEnd());
        Assert.AreSame(vm.TranscriptSearchResults[0], vm.SelectedTranscriptSearchResult);
        Assert.AreEqual(LocalizedStrings.Format("TranscriptSearch.Matches", 2, 2), vm.TranscriptSearchStatusText);
        CollectionAssert.AreEqual(originalTranscript, vm.Items.ToArray(), "Searching leaves the transcript collection and selection untouched.");
    }

    [TestMethod]
    public async Task TranscriptSearch_FindsLiteralCommandOutputAndCode()
    {
        var bridge = new FakeWorkerBridge
        {
            ListThreadItemsHandler = (_, _) => Task.FromResult(new ThreadItemsPage
            {
                Items =
                [
                    new ThreadHistoryItem
                    {
                        Id = "command",
                        TurnId = "turn-1",
                        Type = "commandExecution",
                        Text = "rg ForkThreadAsync\n\u001b[32mTask<ForkThreadResult> ForkThreadAsync\u001b[0m in C:\\Users\\x\\.codex",
                    },
                ],
            }),
        };
        using ChatViewModel vm = await CreateJoinedViewModelAsync(bridge);

        vm.TranscriptSearchText = "Task<ForkThreadResult>";
        await RunCommandAsync(vm.SearchTranscriptCommand);
        Assert.AreEqual(1, vm.TranscriptSearchResults.Count);
        StringAssert.Contains(vm.TranscriptSearchResults[0].Text, "Task<ForkThreadResult> ForkThreadAsync");
        Assert.IsFalse(vm.TranscriptSearchResults[0].Text.Contains('\u001b'), "Results show literal text without ANSI escapes.");

        vm.TranscriptSearchText = "C:\\Users\\x\\.codex";
        await RunCommandAsync(vm.SearchTranscriptCommand);
        Assert.AreEqual(1, vm.TranscriptSearchResults.Count);
    }

    [TestMethod]
    public async Task TranscriptSearch_UsesDisplayApprovedHistoryText()
    {
        var bridge = new FakeWorkerBridge
        {
            ListThreadItemsHandler = (_, _) => Task.FromResult(new ThreadItemsPage
            {
                Items =
                [
                    new ThreadHistoryItem
                    {
                        Id = "hidden-answer",
                        TurnId = "turn-1",
                        Type = "agentMessage",
                        Text = "private phrase hidden by moderation",
                        AgentMessageDisplayDecision = new AgentMessageDisplayDecision
                        {
                            Mode = AgentMessageDisplayMode.Hide,
                            DisplayText = "This response was hidden.",
                        },
                    },
                ],
            }),
        };
        using ChatViewModel vm = await CreateJoinedViewModelAsync(bridge);

        vm.TranscriptSearchText = "private phrase";
        await RunCommandAsync(vm.SearchTranscriptCommand);
        Assert.AreEqual(0, vm.TranscriptSearchResults.Count, "Hidden answer text must not be searchable or returned.");

        vm.TranscriptSearchText = "This response was hidden";
        await RunCommandAsync(vm.SearchTranscriptCommand);
        Assert.AreEqual(1, vm.TranscriptSearchResults.Count);
        Assert.AreEqual("This response was hidden.", vm.TranscriptSearchResults.Single().Text.TrimEnd());
    }

    [TestMethod]
    public async Task TranscriptSearch_UsesDisplayApprovedLiveText()
    {
        var bridge = new FakeWorkerBridge();
        using ChatViewModel vm = await CreateJoinedViewModelAsync(bridge, CodexFeatureTier.Tier170);
        string threadId = vm.SelectedThread!.Id;
        var liveMessages = new (string ItemId, string RawText, AgentMessageDisplayDecision Decision, string VisibleText)[]
        {
            ("hidden-live", "private hidden phrase", new AgentMessageDisplayDecision { Mode = AgentMessageDisplayMode.Hide, DisplayText = "hidden safe display" }, "hidden safe display"),
            ("blocked-live", "private blocked phrase", new AgentMessageDisplayDecision { Mode = AgentMessageDisplayMode.Block, DisplayText = "blocked safe display" }, "blocked safe display"),
            ("suppressed-live", "private suppressed phrase", new AgentMessageDisplayDecision { Mode = AgentMessageDisplayMode.Suppress, DisplayText = null! },
                new AgentMessageDisplayDecision { Mode = AgentMessageDisplayMode.Suppress, DisplayText = null! }.GetDisplayText("private suppressed phrase")!),
            ("shown-live", "public live answer", new AgentMessageDisplayDecision { Mode = AgentMessageDisplayMode.Show, DisplayText = "decoy display text" }, "public live answer"),
        };

        foreach (var message in liveMessages)
        {
            var value = new ConversationEvent
            {
                Kind = ConversationEventKind.ItemCompleted,
                ThreadId = threadId,
                TurnId = "turn-live",
                ItemId = message.ItemId,
                IsAgentMessage = true,
                Text = message.RawText,
                AgentMessageDisplayDecision = message.Decision,
            };
            MethodInfo eventHandler = typeof(ChatViewModel).GetMethod("OnConversationEventAsync", BindingFlags.NonPublic | BindingFlags.Instance)
                ?? throw new InvalidOperationException("Could not find OnConversationEventAsync.");
            await (Task)eventHandler.Invoke(vm, [Notification(value, vm.Status)])!;
        }

        Assert.AreEqual(liveMessages.Length, vm.Items.Count(item => liveMessages.Any(message => message.ItemId == item.ItemId)),
            "The test must exercise live transcript rows that are not part of loaded history.");

        foreach (var message in liveMessages)
        {
            vm.TranscriptSearchText = message.VisibleText;
            await RunCommandAsync(vm.SearchTranscriptCommand);
            Assert.AreEqual(1, vm.TranscriptSearchResults.Count, $"The display-approved text for '{message.ItemId}' should be searchable.");
            Assert.AreEqual(message.ItemId, vm.TranscriptSearchResults.Single().ItemId);
        }

        foreach (var message in liveMessages.Where(static value => value.Decision.Mode != AgentMessageDisplayMode.Show))
        {
            vm.TranscriptSearchText = message.RawText;
            await RunCommandAsync(vm.SearchTranscriptCommand);
            Assert.AreEqual(0, vm.TranscriptSearchResults.Count, "Restricted assistant text must not be searchable from live rows.");
        }

        vm.TranscriptSearchText = "decoy display text";
        await RunCommandAsync(vm.SearchTranscriptCommand);
        Assert.AreEqual(0, vm.TranscriptSearchResults.Count, "Show mode must preserve the approved original text instead of its unused replacement field.");
    }

    [TestMethod]
    public async Task TranscriptSearch_FailedPageDoesNotPublishPartialResults()
    {
        var bridge = new FakeWorkerBridge
        {
            ListThreadItemsHandler = (request, _) => request.Cursor is null
                ? Task.FromResult(new ThreadItemsPage
                {
                    Items = [new ThreadHistoryItem { Id = "recent", Type = "agentMessage", Text = "match" }],
                    NextCursor = "older",
                })
                : Task.FromException<ThreadItemsPage>(new IOException("page unavailable")),
        };
        using ChatViewModel vm = await CreateJoinedViewModelAsync(bridge);
        vm.TranscriptSearchText = "match";

        await RunCommandAsync(vm.SearchTranscriptCommand);

        Assert.AreEqual(0, vm.TranscriptSearchResults.Count);
        Assert.IsNull(vm.SelectedTranscriptSearchResult);
        Assert.IsTrue(
            (vm.TranscriptSearchStatusText.Contains("failed", StringComparison.OrdinalIgnoreCase)
                || vm.TranscriptSearchStatusText.Contains("失敗", StringComparison.Ordinal))
            && (vm.TranscriptSearchStatusText.Contains("No partial results are shown", StringComparison.Ordinal)
                || vm.TranscriptSearchStatusText.Contains("部分的な結果は表示していません", StringComparison.Ordinal)),
            "The status should explain that the search failed and partial results were discarded.");
    }

    [TestMethod]
    public async Task TranscriptSearch_ChangingConversationCancelsAndClearsResults()
    {
        var firstPage = new TaskCompletionSource<ThreadItemsPage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var bridge = new FakeWorkerBridge
        {
            ListThreadItemsHandler = async (request, cancellationToken) =>
            {
                if (request.ThreadId == "thread-1")
                {
                    return await firstPage.Task.WaitAsync(cancellationToken);
                }

                return new ThreadItemsPage { Items = [new ThreadHistoryItem { Id = "other", Type = "agentMessage", Text = "match" }] };
            },
        };
        using ChatViewModel vm = await CreateJoinedViewModelAsync(bridge);
        vm.TranscriptSearchText = "match";
        Task searching = RunCommandAsync(vm.SearchTranscriptCommand);
        await WaitForAsync(() => vm.IsTranscriptSearchLoading);

        vm.SelectedThread = new ThreadSummary { Id = "thread-2" };
        firstPage.SetResult(new ThreadItemsPage { Items = [new ThreadHistoryItem { Id = "stale", Type = "agentMessage", Text = "match" }] });
        await searching;

        Assert.AreEqual(string.Empty, vm.TranscriptSearchText);
        Assert.AreEqual(0, vm.TranscriptSearchResults.Count);
        Assert.IsNull(vm.SelectedTranscriptSearchResult);
    }

    [TestMethod]
    public async Task TranscriptSearch_NewConversationClearsSearchState()
    {
        using ChatViewModel vm = await CreateJoinedViewModelAsync(new FakeWorkerBridge());
        vm.TranscriptSearchText = "match";
        await RunCommandAsync(vm.SearchTranscriptCommand);

        await RunCommandAsync(vm.NewThreadCommand);

        Assert.AreEqual(string.Empty, vm.TranscriptSearchText);
        Assert.AreEqual(0, vm.TranscriptSearchResults.Count);
        Assert.IsNull(vm.SelectedTranscriptSearchResult);
    }
}
