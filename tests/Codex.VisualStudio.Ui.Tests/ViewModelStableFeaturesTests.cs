using System.IO;
using System.Text;
using Codex.VisualStudio.Contracts;
using Codex.VisualStudio.Extension;

namespace Codex.VisualStudio.Ui.Tests;

// Separate-conversation review, history scope, conversation text, and the other 0.166 stable features.
public sealed partial class ViewModelTests
{
    private static readonly string[] WorktreeFolders = [@"C:\repo", @"C:\repo-wt"];
    private static readonly string[] OpaqueOptionIds = ["opaque-id"];

    private static async Task<ChatViewModel> CreateJoinedViewModelAsync(FakeWorkerBridge bridge, CodexFeatureTier tier = CodexFeatureTier.Tier166)
    {
        var vm = new ChatViewModel(bridge, autoConnect: false, settingsStore: new MemorySettingsStore(new ExtensionSettings()))
        {
            SelectedThread = new ThreadSummary { Id = "thread-1" },
        };
        await bridge.PublishStateAsync(new WorkerStatus { State = WorkerConnectionState.Ready, ThreadId = "thread-1", FeatureTier = tier });
        await RunCommandAsync(vm.JoinThreadCommand);
        return vm;
    }

    [TestMethod]
    public async Task ReviewNewThread_Started_SwitchesAndJoinsNewConversation()
    {
        var bridge = new FakeWorkerBridge
        {
            StartReviewHandler = (request, _) =>
            {
                Assert.AreEqual(ReviewPlacement.NewThread, request.Placement);
                Assert.AreEqual(ReviewTargetKind.BaseBranch, request.Target.Kind);
                return Task.FromResult(new StartReviewResult
                {
                    ReviewThreadId = "thread-review",
                    TurnId = "turn-review",
                    CreatedThread = new ThreadSummary { Id = "thread-review" },
                });
            },
        };
        using ChatViewModel vm = await CreateJoinedViewModelAsync(bridge);

        await RunComposerAsync(vm, "/review --new-thread base main");
        await WaitForAsync(() => vm.SelectedThread?.Id == "thread-review");

        Assert.IsTrue(vm.IsThreadJoined);
        Assert.IsTrue(vm.Threads.Any(thread => thread.Id == "thread-review"));
    }

    [TestMethod]
    [DataRow(ReviewStartOutcome.NotCreated, false)]
    [DataRow(ReviewStartOutcome.CreatedNotStarted, true)]
    [DataRow(ReviewStartOutcome.Unknown, true)]
    public async Task ReviewNewThread_NotStarted_KeepsOriginalSelection(ReviewStartOutcome outcome, bool created)
    {
        var bridge = new FakeWorkerBridge
        {
            StartReviewHandler = (_, _) => Task.FromResult(new StartReviewResult
            {
                Outcome = outcome,
                CreatedThread = created ? new ThreadSummary { Id = "thread-review" } : null,
                FailureReason = "Reason from the worker.",
            }),
        };
        using ChatViewModel vm = await CreateJoinedViewModelAsync(bridge);

        await RunComposerAsync(vm, "/review --new-thread");
        await WaitForAsync(() => bridge.ReviewCallCount == 1 && vm.Items.Any(item => item.Role == "Error"));

        Assert.AreEqual("thread-1", vm.SelectedThread?.Id);
        Assert.IsTrue(vm.IsThreadJoined);
        Assert.AreEqual(created, vm.Threads.Any(thread => thread.Id == "thread-review"));
        Assert.AreEqual(1, bridge.ReviewCallCount, "Nothing is resent automatically.");
    }

    [TestMethod]
    public async Task ReviewNewThread_LostResponse_IsUnknownAndNotAssumedStarted()
    {
        var bridge = new FakeWorkerBridge
        {
            StartReviewHandler = (_, _) => Task.FromException<StartReviewResult>(new IOException("lost")),
        };
        using ChatViewModel vm = await CreateJoinedViewModelAsync(bridge);

        await RunComposerAsync(vm, "/review --new-thread");
        await WaitForAsync(() => vm.Items.Any(item => item.Text.Contains("outcome is unknown", StringComparison.Ordinal)));

        Assert.AreEqual("thread-1", vm.SelectedThread?.Id);
    }

    [TestMethod]
    public async Task ReviewNewThread_WorkerRpcUnavailable_IsReportedAsNotSent()
    {
        var bridge = new FakeWorkerBridge
        {
            StartReviewHandler = (_, _) => Task.FromException<StartReviewResult>(new WorkerRpcUnavailableException()),
        };
        using ChatViewModel vm = await CreateJoinedViewModelAsync(bridge);

        await RunComposerAsync(vm, "/review --new-thread");
        await WaitForAsync(() => vm.Items.Any(item => item.Text.Contains("was not sent", StringComparison.Ordinal)));

        Assert.AreEqual("thread-1", vm.SelectedThread?.Id);
        Assert.IsFalse(vm.Items.Any(item => item.Text.Contains("outcome is unknown", StringComparison.Ordinal)));
        Assert.IsFalse(vm.Threads.Any(thread => thread.Id != "thread-1"));
    }

    [TestMethod]
    public async Task ReviewNewThread_PreDispatchRejection_IsReportedAsNotSent()
    {
        var bridge = new FakeWorkerBridge
        {
            StartReviewHandler = (_, _) => Task.FromException<StartReviewResult>(
                new StreamJsonRpc.RemoteInvocationException("Owner changed.", WorkerErrorCodes.PreDispatchRejected, errorData: null)),
        };
        using ChatViewModel vm = await CreateJoinedViewModelAsync(bridge);

        await RunComposerAsync(vm, "/review --new-thread");
        await WaitForAsync(() => vm.Items.Any(item => item.Text.Contains("rejected before it was sent", StringComparison.Ordinal)));

        Assert.IsFalse(vm.Items.Any(item => item.Text.Contains("outcome is unknown", StringComparison.Ordinal)));
    }

    [TestMethod]
    public async Task ReviewNewThread_SecondStartIsBlockedWhileRunning()
    {
        var release = new TaskCompletionSource<StartReviewResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var bridge = new FakeWorkerBridge
        {
            StartReviewHandler = async (_, cancellationToken) => await release.Task.WaitAsync(cancellationToken),
        };
        using ChatViewModel vm = await CreateJoinedViewModelAsync(bridge);

        Task first = RunComposerAsync(vm, "/review --new-thread");
        await WaitForAsync(() => bridge.ReviewCallCount == 1);
        await RunComposerAsync(vm, "/review --new-thread");
        await WaitForAsync(() => vm.Items.Any(item => item.Text.Contains("already starting", StringComparison.Ordinal)));

        release.SetResult(new StartReviewResult { Outcome = ReviewStartOutcome.NotCreated });
        await first;
        Assert.AreEqual(1, bridge.ReviewCallCount);
    }

    [TestMethod]
    public async Task HistoryScope_WorktreeScopeNeedsTwoLocalWorktrees()
    {
        var bridge = new FakeWorkerBridge
        {
            ThreadListScopesResult = new ThreadListScopesResult { CurrentFolder = @"C:\repo", WorktreeFolders = [@"C:\repo"] },
        };
        using ChatViewModel vm = await CreateJoinedViewModelAsync(bridge);

        await RunCommandAsync(vm.ToggleHistoryCommand);
        await WaitForAsync(() => vm.ThreadListScopes.Count == 2);

        Assert.IsFalse(vm.ThreadListScopes.Any(scope => scope.Id == ChatViewModel.RepositoryWorktreesScopeId));
    }

    [TestMethod]
    public async Task HistoryScope_FixesFoldersForPagingAndDoesNotCancelConversationsOutsideIt()
    {
        var bridge = new FakeWorkerBridge
        {
            ThreadListScopesResult = new ThreadListScopesResult { CurrentFolder = @"C:\repo", WorktreeFolders = WorktreeFolders },
            ListThreadsHandler = (cursor, _) => Task.FromResult(new ThreadPage
            {
                Threads = [new ThreadSummary { Id = cursor ?? "page-1" }],
                NextCursor = cursor is null ? "page-2" : null,
            }),
        };
        using ChatViewModel vm = await CreateJoinedViewModelAsync(bridge);
        await RunCommandAsync(vm.ToggleHistoryCommand);
        await WaitForAsync(() => vm.ThreadListScopes.Count == 3);

        vm.SelectedThreadListScopeId = ChatViewModel.RepositoryWorktreesScopeId;
        await WaitForAsync(() => vm.Threads.Any(thread => thread.Id == "page-1"));
        await RunCommandAsync(vm.LoadMoreCommand);
        await WaitForAsync(() => vm.Threads.Any(thread => thread.Id == "page-2"));

        CollectionAssert.AreEqual(WorktreeFolders, bridge.ListedWorkingDirectories[^1]!.ToArray());
        CollectionAssert.AreEqual(WorktreeFolders, bridge.ListedWorkingDirectories[^2]!.ToArray());
        Assert.IsTrue(vm.Threads.Any(thread => thread.Id == "thread-1"), "The open conversation is not treated as deleted.");
    }

    [TestMethod]
    public async Task HistoryScope_LateResponseForOldScopeIsDropped()
    {
        var firstPage = new TaskCompletionSource<ThreadPage>(TaskCreationOptions.RunContinuationsAsynchronously);
        int calls = 0;
        var bridge = new FakeWorkerBridge
        {
            ThreadListScopesResult = new ThreadListScopesResult { CurrentFolder = @"C:\repo" },
            ListThreadsHandler = async (_, cancellationToken) => ++calls == 1
                ? await firstPage.Task.WaitAsync(cancellationToken)
                : new ThreadPage { Threads = [new ThreadSummary { Id = "current-folder" }] },
        };
        using ChatViewModel vm = await CreateJoinedViewModelAsync(bridge);
        await RunCommandAsync(vm.ToggleHistoryCommand);
        await WaitForAsync(() => vm.ThreadListScopes.Count == 2);

        vm.SelectedThreadListScopeId = ChatViewModel.CurrentFolderScopeId;
        await WaitForAsync(() => calls == 1);
        vm.SelectedThreadListScopeId = ChatViewModel.AllConversationsScopeId;
        firstPage.SetResult(new ThreadPage { Threads = [new ThreadSummary { Id = "stale" }] });
        await WaitForAsync(() => vm.Threads.Any(thread => thread.Id == "current-folder"));

        Assert.IsFalse(vm.Threads.Any(thread => thread.Id == "stale"));
        Assert.IsNull(bridge.ListedWorkingDirectories[^1]);
    }

    [TestMethod]
    public void QuestionMarkdown_RendersSafelyAndKeepsOptionId()
    {
        var markdown = new SafeMarkdownService();
        var question = new UserInputQuestionViewModel(
            new UserInputQuestion
            {
                Id = "q",
                Header = "Header",
                Question = "# Pick\n\n- **one**\n\n<script>alert(1)</script>\n\n```\ncode\n```",
                Options = [new UserInputOption { OptionId = "opaque-id", Label = "Yes", Description = "Use `this` path" }],
            },
            markdown);

        Assert.IsTrue(question.QuestionBlocks.Any(block => block.IsHeading));
        Assert.IsTrue(question.QuestionBlocks.Any(block => block.IsListItem));
        Assert.IsTrue(question.QuestionBlocks.Any(block => block.IsCodeBlock));
        Assert.IsFalse(question.Question.Contains("<script>", StringComparison.OrdinalIgnoreCase));
        question.Options[0].IsSelected = true;
        CollectionAssert.AreEqual(OpaqueOptionIds, question.CreateAnswer()!.OptionIds!.ToArray());
    }

    [TestMethod]
    public void ItemTiming_HandlesMissingAndUnrepresentableValues()
    {
        Assert.AreEqual(string.Empty, ChatItemViewModel.FormatTiming(null, null));
        Assert.AreEqual(string.Empty, ChatItemViewModel.FormatTiming(long.MaxValue, null));
        StringAssert.StartsWith(ChatItemViewModel.FormatTiming(1_000, null), "Started ");
        StringAssert.EndsWith(ChatItemViewModel.FormatTiming(1_000, 3_500), " · 2.5s");

        var item = new ChatItemViewModel("Codex", "text", ConversationEventKind.ItemCompleted);
        item.ApplyTiming(1_000, null);
        item.ApplyTiming(null, 2_000);
        Assert.IsTrue(item.HasTiming);
        StringAssert.Contains(item.TimingText, "Started");
        StringAssert.Contains(item.TimingText, "Completed");
    }

    [TestMethod]
    public async Task ConversationText_ReadsEveryPageOldestFirstInReadOnlySnapshot()
    {
        var documents = new RecordingConversationTextDocuments();
        var bridge = new FakeWorkerBridge
        {
            ListThreadItemsHandler = (request, _) => Task.FromResult(request.Cursor is null
                ? new ThreadItemsPage { Items = [new ThreadHistoryItem { Id = "2", Type = "agentMessage", Text = "新しい" }], NextCursor = "older" }
                : new ThreadItemsPage { Items = [new ThreadHistoryItem { Id = "1", Type = "userMessage", Text = "古い" }] }),
        };
        using ChatViewModel vm = await CreateJoinedViewModelAsync(bridge);
        vm.ConversationTextDocuments = documents;

        await RunCommandAsync(vm.OpenConversationTextCommand);
        await WaitForAsync(() => documents.Path is not null);

        string text = await File.ReadAllTextAsync(documents.Path!, Encoding.UTF8);
        Assert.IsTrue(text.IndexOf("古い", StringComparison.Ordinal) < text.IndexOf("新しい", StringComparison.Ordinal));
        Assert.IsFalse(Path.GetFileName(documents.Path!).Contains("thread", StringComparison.OrdinalIgnoreCase));
        documents.Closed!(documents.Path!);
        Assert.IsFalse(File.Exists(documents.Path!), "Closing the tab removes the snapshot.");
    }

    [TestMethod]
    public async Task ConversationText_FailedPageOpensNothing()
    {
        var documents = new RecordingConversationTextDocuments();
        var bridge = new FakeWorkerBridge
        {
            ListThreadItemsHandler = (request, _) => request.Cursor is null
                ? Task.FromResult(new ThreadItemsPage { Items = [new ThreadHistoryItem { Id = "2", Type = "agentMessage" }], NextCursor = "older" })
                : Task.FromException<ThreadItemsPage>(new IOException("lost")),
        };
        using ChatViewModel vm = await CreateJoinedViewModelAsync(bridge);
        vm.ConversationTextDocuments = documents;

        await RunCommandAsync(vm.OpenConversationTextCommand);
        await WaitForAsync(() => vm.ConversationTextStatusText.Contains("could not be opened", StringComparison.Ordinal));

        Assert.IsNull(documents.Path);
    }

    [TestMethod]
    public void ConversationTextStore_SeparatesRefreshesAndRemovesOnlyOldUnusedFiles()
    {
        string root = Path.Combine(Path.GetTempPath(), "codex-store-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var store = new ConversationTextStore(root);
            var owner = new ArtifactPreviewOwner("partition", 1, 1);
            string first = store.Write(owner, "a");
            string second = store.Write(owner, "b");
            Assert.AreNotEqual(first, second);
            Assert.AreNotEqual(
                Path.GetDirectoryName(first),
                Path.GetDirectoryName(store.Write(new ArtifactPreviewOwner("partition", 2, 1), "c")));

            File.SetLastWriteTimeUtc(first, DateTime.UtcNow.AddHours(-30));
            store.RemoveUnused(DateTimeOffset.UtcNow);
            Assert.IsTrue(File.Exists(first), "A snapshot that is still open stays.");

            var restarted = new ConversationTextStore(root);
            restarted.RemoveUnused(DateTimeOffset.UtcNow);
            Assert.IsFalse(File.Exists(first));
            Assert.IsTrue(File.Exists(second));
            restarted.Remove(second);
            Assert.IsTrue(File.Exists(second), "Another instance's file is not removed by path.");
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [TestMethod]
    public void ConversationTextStore_RemoveUnusedSkipsRedirectedDirectories()
    {
        string testRoot = Path.Combine(Path.GetTempPath(), "codex-store-tests", Guid.NewGuid().ToString("N"));
        string root = Path.Combine(testRoot, "ConversationText");
        string outside = Path.Combine(testRoot, "Outside");
        string? linkedOwnerDirectory = null;
        string? linkedRoot = null;

        try
        {
            Directory.CreateDirectory(root);
            Directory.CreateDirectory(outside);
            var owner = new ArtifactPreviewOwner("partition", 1, 1);
            string generated = new ConversationTextStore(root).Write(owner, "snapshot");
            string ownerDirectory = Path.GetDirectoryName(generated)!;
            File.Delete(generated);
            Directory.Delete(ownerDirectory);

            string outsideOwnerDirectory = Path.Combine(outside, "owner");
            Directory.CreateDirectory(outsideOwnerDirectory);
            string externalFile = Path.Combine(outsideOwnerDirectory, "stale.txt");
            File.WriteAllText(externalFile, "keep");
            File.SetLastWriteTimeUtc(externalFile, DateTime.UtcNow.AddHours(-30));
            linkedOwnerDirectory = ownerDirectory;
            CreateDirectoryLink(linkedOwnerDirectory, outsideOwnerDirectory);

            new ConversationTextStore(root).RemoveUnused(DateTimeOffset.UtcNow);

            Assert.IsTrue(File.Exists(externalFile), "A linked owner directory must never expose external files to cleanup.");
            Directory.Delete(linkedOwnerDirectory);
            linkedOwnerDirectory = null;

            string redirectedRoot = Path.Combine(testRoot, "RedirectedConversationText");
            string rootTarget = Path.Combine(outside, "root-target");
            Directory.CreateDirectory(rootTarget);
            string rootFile = Path.Combine(rootTarget, "stale.txt");
            File.WriteAllText(rootFile, "keep");
            File.SetLastWriteTimeUtc(rootFile, DateTime.UtcNow.AddHours(-30));
            linkedRoot = redirectedRoot;
            CreateDirectoryLink(redirectedRoot, rootTarget);

            new ConversationTextStore(redirectedRoot).RemoveUnused(DateTimeOffset.UtcNow);

            Assert.IsTrue(File.Exists(rootFile), "A redirected store root must never be traversed.");
        }
        finally
        {
            if (linkedOwnerDirectory is not null && Directory.Exists(linkedOwnerDirectory))
            {
                Directory.Delete(linkedOwnerDirectory);
            }

            if (linkedRoot is not null && Directory.Exists(linkedRoot))
            {
                Directory.Delete(linkedRoot);
            }

            if (Directory.Exists(testRoot))
            {
                Directory.Delete(testRoot, recursive: true);
            }
        }
    }

    private static void CreateDirectoryLink(string linkPath, string targetPath)
    {
        try
        {
            Directory.CreateSymbolicLink(linkPath, targetPath);
            return;
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException
            or PlatformNotSupportedException
            or IOException
            or NotSupportedException)
        {
            // Junctions provide the same path redirection case without Developer Mode or elevation.
        }

        using System.Diagnostics.Process process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = Path.Combine(Environment.SystemDirectory, "cmd.exe"),
            ArgumentList = { "/d", "/c", "mklink", "/J", linkPath, targetPath },
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        }) ?? throw new IOException("The directory junction process could not start.");
        process.StandardOutput.ReadToEnd();
        process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0 || !Directory.Exists(linkPath))
        {
            Assert.Inconclusive($"Directory links are unavailable (exit code {process.ExitCode}).");
        }
    }

    [TestMethod]
    public void ThreadUsage_KeepsActualEstimatedAndUnknownDistinct()
    {
        var usage = new UsagePresentation();
        usage.UpdateThreadUsage(new ThreadUsageResult { NativeUsageUsdMicros = 1_234_500, EstimatedUsageUsdMicros = 0, EstimatedUsageCreditsMicros = null });

        StringAssert.Contains(usage.ThreadUsageText, "This conversation: $1.2345");
        StringAssert.Contains(usage.ThreadUsageText, "(estimated): $0.00");
        Assert.IsFalse(usage.ThreadUsageText.Contains("credits", StringComparison.Ordinal), "A missing value never shows as zero.");

        usage.UpdateThreadUsage(new ThreadUsageResult());
        Assert.IsFalse(usage.HasThreadUsage);
        Assert.AreEqual(string.Empty, usage.ThreadUsageText);
    }

    [TestMethod]
    public async Task ThreadUsage_RereadsForAnotherConversationWhileLimitsAreCached()
    {
        var bridge = new FakeWorkerBridge
        {
            ThreadUsageResult = new ThreadUsageResult { EstimatedUsageCreditsMicros = 1_000_000 },
        };
        using ChatViewModel vm = await CreateJoinedViewModelAsync(bridge);
        await bridge.PublishAccountAsync(new AccountStatus { State = AccountState.SignedIn });
        await vm.RefreshUsageAsync(force: true);
        CollectionAssert.Contains(bridge.ThreadUsageRequests, "thread-1");
        int limitReads = bridge.RateLimitCallCount;

        vm.SelectedThread = new ThreadSummary { Id = "thread-2" };
        Assert.IsFalse(vm.Usage.HasThreadUsage, "Changing conversations clears the previous conversation amount immediately.");
        Assert.AreEqual(string.Empty, vm.Usage.ThreadUsageText);
        await vm.RefreshUsageAsync(force: false);

        CollectionAssert.Contains(bridge.ThreadUsageRequests, "thread-2");
        Assert.IsTrue(vm.Usage.HasThreadUsage, "The new conversation amount is shown after its usage read completes.");
        Assert.AreEqual(limitReads, bridge.RateLimitCallCount, "Account limits stay cached.");
    }

    [TestMethod]
    public async Task Doctor_ShowsRemoteProcedureWithoutRunningLocally()
    {
        var bridge = new FakeWorkerBridge { NetworkDiagnosticsResult = new NetworkDiagnosticsResult { RequiresServerRun = true } };
        using ChatViewModel vm = await CreateJoinedViewModelAsync(bridge);

        await RunComposerAsync(vm, "/doctor");
        await WaitForAsync(() => vm.Items.Any(item => item.Text.Contains("app-server host", StringComparison.Ordinal)));
    }

    [TestMethod]
    public async Task Doctor_RequiresTier166()
    {
        var bridge = new FakeWorkerBridge();
        using ChatViewModel vm = await CreateJoinedViewModelAsync(bridge, CodexFeatureTier.Tier165);

        await RunComposerAsync(vm, "/doctor");
        await WaitForAsync(() => vm.Items.Any(item => item.Text.Contains("0.166.0", StringComparison.Ordinal)));
    }

    private sealed class RecordingConversationTextDocuments : IConversationTextDocuments
    {
        public string? Path { get; private set; }

        public Action<string>? Closed { get; private set; }

        public Task OpenAsync(string path, string caption, Action<string> closed, CancellationToken cancellationToken)
        {
            Closed = closed;
            Path = path;
            return Task.CompletedTask;
        }
    }
}
