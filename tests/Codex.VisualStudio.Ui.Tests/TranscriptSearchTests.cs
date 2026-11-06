using Codex.VisualStudio.Contracts;
using Codex.VisualStudio.Extension;
using System.IO;

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
        Assert.AreEqual("2 matching conversation items found across 2 items.", vm.TranscriptSearchStatusText);
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
        StringAssert.Contains(vm.TranscriptSearchStatusText, "No partial results are shown");
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
