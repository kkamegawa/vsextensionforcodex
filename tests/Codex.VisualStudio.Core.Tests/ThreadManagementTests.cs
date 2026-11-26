using System.Text.Json;
using System.IO.Pipelines;
using StreamJsonRpc;
using Codex.AppServer.Protocol;
using Codex.VisualStudio.Contracts;
using Codex.VisualStudio.Worker;

namespace Codex.VisualStudio.Core.Tests;

public sealed partial class CodexSessionServiceTests
{
    [TestMethod]
    public async Task OwnerScopedRequestPayloadsRoundTripThroughWorkerJsonRpcFormatter()
    {
        // Production WorkerBridge constructs JsonRpc(Stream), which uses the Newtonsoft-based
        // JsonMessageFormatter by default. Exercise the same formatter over in-memory pipes.
        var clientToWorker = new Pipe();
        var workerToClient = new Pipe();
        using var workerRpc = new JsonRpc(new HeaderDelimitedMessageHandler(
            workerToClient.Writer, clientToWorker.Reader, new JsonMessageFormatter()));
        using var extensionRpc = new JsonRpc(new HeaderDelimitedMessageHandler(
            clientToWorker.Writer, workerToClient.Reader, new JsonMessageFormatter()));
        var echo = new ThreadRequestContractEcho();
        workerRpc.AddLocalRpcTarget(echo);
        workerRpc.StartListening();
        extensionRpc.StartListening();

        var mutation = new ThreadMutationRequest
        {
            ThreadId = "thread-round-trip",
            BeforeTurnId = "turn-round-trip",
            UserConfirmed = true,
            StatePartitionFingerprint = "partition-round-trip",
            OwnerGeneration = 31,
            ConnectionGeneration = 47,
        };
        ThreadMutationRequest restoredMutation = await extensionRpc.InvokeWithParameterObjectAsync<ThreadMutationRequest>(
            "test/echoThreadMutation", mutation, CancellationToken.None);
        Assert.AreEqual(mutation.ThreadId, restoredMutation.ThreadId);
        Assert.AreEqual(mutation.BeforeTurnId, restoredMutation.BeforeTurnId);
        Assert.IsTrue(restoredMutation.UserConfirmed);
        Assert.AreEqual(mutation.StatePartitionFingerprint, restoredMutation.StatePartitionFingerprint);
        Assert.AreEqual(mutation.OwnerGeneration, restoredMutation.OwnerGeneration);
        Assert.AreEqual(mutation.ConnectionGeneration, restoredMutation.ConnectionGeneration);

        var search = new ThreadSearchRequest
        {
            SearchTerm = "needle round trip",
            RequestId = "9dd43d64f8fb4bf9bb8e1e5f07d8d511",
            StatePartitionFingerprint = "partition-search-round-trip",
            OwnerGeneration = 37,
            ConnectionGeneration = 53,
        };
        ThreadSearchRequest restoredSearch = await extensionRpc.InvokeWithParameterObjectAsync<ThreadSearchRequest>(
            "test/echoThreadSearch", search, CancellationToken.None);
        Assert.AreEqual(search.SearchTerm, restoredSearch.SearchTerm);
        Assert.AreEqual(search.RequestId, restoredSearch.RequestId);
        Assert.AreEqual(search.StatePartitionFingerprint, restoredSearch.StatePartitionFingerprint);
        Assert.AreEqual(search.OwnerGeneration, restoredSearch.OwnerGeneration);
        Assert.AreEqual(search.ConnectionGeneration, restoredSearch.ConnectionGeneration);

        var listThreads = new ListThreadsRequest
        {
            Cursor = "cursor-round-trip",
            Archived = true,
            SearchTerm = "search-round-trip",
            SectionId = "section-round-trip",
            WorkingDirectories = ["C:\\workspace-round-trip"],
            StatePartitionFingerprint = "partition-list-round-trip",
            OwnerGeneration = 41,
            ConnectionGeneration = 59,
        };
        ListThreadsRequest restoredListThreads = await extensionRpc.InvokeWithParameterObjectAsync<ListThreadsRequest>(
            "test/echoListThreads", listThreads, CancellationToken.None);
        Assert.AreEqual(listThreads.Cursor, restoredListThreads.Cursor);
        Assert.IsTrue(restoredListThreads.Archived);
        Assert.AreEqual(listThreads.SearchTerm, restoredListThreads.SearchTerm);
        Assert.AreEqual(listThreads.SectionId, restoredListThreads.SectionId);
        CollectionAssert.AreEqual(listThreads.WorkingDirectories!.ToArray(), restoredListThreads.WorkingDirectories!.ToArray());
        Assert.AreEqual(listThreads.StatePartitionFingerprint, restoredListThreads.StatePartitionFingerprint);

        var startTurn = new StartTurnRequest
        {
            ThreadId = "thread-turn-round-trip",
            Text = "turn payload survives",
            StatePartitionFingerprint = "partition-turn-round-trip",
            OwnerGeneration = 43,
            ConnectionGeneration = 61,
        };
        StartTurnRequest restoredStartTurn = await extensionRpc.InvokeWithParameterObjectAsync<StartTurnRequest>(
            "test/echoStartTurn", startTurn, CancellationToken.None);
        Assert.AreEqual(startTurn.ThreadId, restoredStartTurn.ThreadId);
        Assert.AreEqual(startTurn.Text, restoredStartTurn.Text);
        Assert.AreEqual(startTurn.StatePartitionFingerprint, restoredStartTurn.StatePartitionFingerprint);

        var steerTurn = new SteerTurnRequest
        {
            ThreadId = "thread-steer-round-trip",
            ExpectedTurnId = "turn-steer-round-trip",
            Text = "steer payload survives",
        };
        SteerTurnRequest restoredSteerTurn = await extensionRpc.InvokeWithParameterObjectAsync<SteerTurnRequest>(
            "test/echoSteerTurn", steerTurn, CancellationToken.None);
        Assert.AreEqual(steerTurn.ExpectedTurnId, restoredSteerTurn.ExpectedTurnId);
        Assert.AreEqual(steerTurn.Text, restoredSteerTurn.Text);

        var interruptTurn = new InterruptTurnRequest
        {
            ThreadId = "thread-interrupt-round-trip",
            TurnId = "turn-interrupt-round-trip",
        };
        InterruptTurnRequest restoredInterrupt = await extensionRpc.InvokeWithParameterObjectAsync<InterruptTurnRequest>(
            "test/echoInterruptTurn", interruptTurn, CancellationToken.None);
        Assert.AreEqual(interruptTurn.TurnId, restoredInterrupt.TurnId);

        var readState = new UpdateThreadReadStateRequest
        {
            ThreadId = "thread-read-state-round-trip",
            ExpectedRevision = "revision-round-trip",
            MarkRead = true,
        };
        UpdateThreadReadStateRequest restoredReadState = await extensionRpc.InvokeWithParameterObjectAsync<UpdateThreadReadStateRequest>(
            "test/echoReadState", readState, CancellationToken.None);
        Assert.AreEqual(readState.ExpectedRevision, restoredReadState.ExpectedRevision);
        Assert.IsTrue(restoredReadState.MarkRead);

        var fork = new ForkThreadRequest
        {
            ThreadId = "thread-fork-round-trip",
            CreateLinkedFork = true,
            ListLinkedForks = true,
            Cursor = "fork-cursor-round-trip",
        };
        ForkThreadRequest restoredFork = await extensionRpc.InvokeWithParameterObjectAsync<ForkThreadRequest>(
            "test/echoFork", fork, CancellationToken.None);
        Assert.IsTrue(restoredFork.CreateLinkedFork);
        Assert.IsTrue(restoredFork.ListLinkedForks);
        Assert.AreEqual(fork.Cursor, restoredFork.Cursor);

        var switchWorkspace = new SwitchWorkspaceRequest
        {
            SessionId = "session-round-trip",
            AccountId = "account-round-trip",
            UserConfirmed = true,
        };
        SwitchWorkspaceRequest restoredWorkspace = await extensionRpc.InvokeWithParameterObjectAsync<SwitchWorkspaceRequest>(
            "test/echoWorkspaceSwitch", switchWorkspace, CancellationToken.None);
        Assert.AreEqual(switchWorkspace.SessionId, restoredWorkspace.SessionId);
        Assert.AreEqual(switchWorkspace.AccountId, restoredWorkspace.AccountId);
        Assert.IsTrue(restoredWorkspace.UserConfirmed);

        var listScopes = new GetThreadListScopesRequest
        {
            StatePartitionFingerprint = "partition-list-scopes-round-trip",
            OwnerGeneration = 67,
            ConnectionGeneration = 71,
        };
        GetThreadListScopesRequest restoredListScopes = await extensionRpc.InvokeWithParameterObjectAsync<GetThreadListScopesRequest>(
            "test/echoThreadListScopes", listScopes, CancellationToken.None);
        Assert.AreEqual(listScopes.StatePartitionFingerprint, restoredListScopes.StatePartitionFingerprint);
        Assert.AreEqual(listScopes.OwnerGeneration, restoredListScopes.OwnerGeneration);
        Assert.AreEqual(listScopes.ConnectionGeneration, restoredListScopes.ConnectionGeneration);
        Assert.AreEqual(10, echo.CallCount);

    }
    [TestMethod]
    public async Task ThreadArchiveRequiresExplicitConfirmationAndSendsMutationOnce()
    {
        var connection = new RecordingConnection();
        await using var service = CreateService();
        await service.InitializeAsync(connection, Options(), CancellationToken.None);

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => service.ArchiveThreadAsync(
            new ThreadMutationRequest { ThreadId = "thread-1", UserConfirmed = false }, CancellationToken.None));
        Assert.IsFalse(connection.Requests.Any(item => item.Method == "thread/archive"));

        ThreadMutationResult result = await service.ArchiveThreadAsync(
            new ThreadMutationRequest { ThreadId = "thread-1", UserConfirmed = true }, CancellationToken.None);

        Assert.IsTrue(result.Succeeded);
        Assert.AreEqual(1, connection.Requests.Count(item => item.Method == "thread/archive"), "A mutation is sent once and never retried.");
    }

    [TestMethod]
    public async Task ThreadDeleteAndRevertRequireConfirmationAndPolicyApproval()
    {
        var connection = new RecordingConnection();
        await using var service = new CodexSessionService(
            new DenyThreadMutationPolicy(), new SecretRedactor(), null, null, null, new NullSkillCatalogStore());
        await service.InitializeAsync(connection, Options(), CancellationToken.None);

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => service.DeleteThreadAsync(
            new ThreadMutationRequest { ThreadId = "thread-1", UserConfirmed = false }, CancellationToken.None));
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => service.RevertThreadAsync(
            new ThreadMutationRequest { ThreadId = "thread-1", BeforeTurnId = "turn-1", UserConfirmed = false }, CancellationToken.None));
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => service.DeleteThreadAsync(
            new ThreadMutationRequest { ThreadId = "thread-1", UserConfirmed = true }, CancellationToken.None));
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => service.RevertThreadAsync(
            new ThreadMutationRequest { ThreadId = "thread-1", BeforeTurnId = "turn-1", UserConfirmed = true }, CancellationToken.None));

        Assert.IsFalse(connection.Requests.Any(item => item.Method is "thread/delete" or "thread/revert"));
    }

    [TestMethod]
    public async Task ThreadMutationReturnsUnknownWhenTransportFailsAfterDispatch()
    {
        var connection = new RecordingConnection
        {
            AsyncHandler = (method, _, _) => method == "thread/delete"
                ? throw new JsonRpcConnectionClosedException("connection closed after dispatch")
                : Task.FromResult(JsonSerializer.SerializeToElement(new { })),
        };
        await using var service = CreateService();
        await service.InitializeAsync(connection, Options(), CancellationToken.None);

        ThreadMutationResult result = await service.DeleteThreadAsync(
            new ThreadMutationRequest { ThreadId = "thread-1", UserConfirmed = true }, CancellationToken.None);

        Assert.IsFalse(result.Succeeded);
        Assert.IsTrue(result.OutcomeUnknown);
        Assert.AreEqual(1, connection.Requests.Count(item => item.Method == "thread/delete"));
    }
    [TestMethod]
    public async Task ThreadDeleteIsBlockedWhileItsTurnIsRunning()
    {
        var connection = new RecordingConnection
        {
            Handler = (method, _) => method == "thread/resume"
                ? JsonSerializer.SerializeToElement(new { thread = new { id = "thread-1" } })
                : JsonSerializer.SerializeToElement(new { }),
        };
        await using var service = CreateService();
        await service.InitializeAsync(connection, Options(), CancellationToken.None);
        await service.ResumeThreadAsync("thread-1", CancellationToken.None);
        await connection.EmitNotificationAsync("turn/started", new { threadId = "thread-1", turn = new { id = "turn-1" } });

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => service.DeleteThreadAsync(
            new ThreadMutationRequest { ThreadId = "thread-1", UserConfirmed = true }, CancellationToken.None));
        Assert.IsFalse(connection.Requests.Any(item => item.Method == "thread/delete"));
    }

    [TestMethod]
    public async Task ThreadSearch171StreamsOnlyMatchingRequestPartialsAndReturnsAuthoritativeFinalPage()
    {
        const string requestId = "a8db303c852145c99822c70c8d5d10a1";
        RecordingConnection? connection = null;
        connection = new RecordingConnection
        {
            AsyncHandler = async (method, _, _) =>
            {
                if (method == "thread/search/stream")
                {
                    await connection!.EmitNotificationAsync("thread/search/partial", new
                    {
                        requestId,
                        data = new[] { new { thread = new { id = "provisional" }, snippet = "provisional" } },
                    });
                    await connection.EmitNotificationAsync("thread/search/partial", new
                    {
                        requestId = "stale-search",
                        data = new[] { new { thread = new { id = "stale" }, snippet = "stale" } },
                    });
                    return JsonSerializer.SerializeToElement(new
                    {
                        data = new[] { new { thread = new { id = "authoritative" }, snippet = "final" } },
                        nextCursor = (string?)null,
                    });
                }

                return JsonSerializer.SerializeToElement(new { });
            },
        };
        await using var service = CreateService();
        var partials = new List<ThreadSearchPartialEvent>();
        service.ThreadSearchPartial += (value, _) =>
        {
            partials.Add(value);
            return Task.CompletedTask;
        };
        await service.InitializeAsync(connection, Options(experimentalApi: true), CancellationToken.None);

        ThreadSearchResult result = await service.SearchThreadsAsync(
            new ThreadSearchRequest { SearchTerm = "needle", RequestId = requestId }, CancellationToken.None);

        Assert.AreEqual(1, partials.Count);
        Assert.AreEqual(requestId, partials[0].RequestId);
        Assert.AreEqual("provisional", partials[0].Results.Single().Thread.Id);
        Assert.AreEqual("authoritative", result.Results.Single().Thread.Id);
        Assert.AreEqual("thread/search/stream", connection.Requests.Single(item => item.Method == "thread/search/stream").Method);
        Assert.AreEqual(requestId, connection.LastCorrelatedRequestId);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => service.SearchThreadsAsync(
            new ThreadSearchRequest { SearchTerm = "another", RequestId = requestId }, CancellationToken.None));
        Assert.AreEqual(1, connection.Requests.Count(item => item.Method == "thread/search/stream"));
    }

    [TestMethod]
    public async Task ThreadSearchCancellationSendsCorrelatedCancelOnTheSameConnection()
    {
        const string requestId = "9b67a9d0246640269cb701ec2d9a790c";
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        RecordingConnection connection = null!;
        connection = new RecordingConnection
        {
            AsyncHandler = async (method, _, token) =>
            {
                if (method == "thread/search/stream")
                {
                    started.TrySetResult();
                    await Task.Delay(Timeout.InfiniteTimeSpan, token);
                }

                return JsonSerializer.SerializeToElement(new { });
            },
        };
        await using var service = CreateService();
        await service.InitializeAsync(connection, Options(experimentalApi: true), CancellationToken.None);
        using var cancellation = new CancellationTokenSource();
        Task<ThreadSearchResult> search = service.SearchThreadsAsync(
            new ThreadSearchRequest { SearchTerm = "needle", RequestId = requestId }, cancellation.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(async () => await search);
        Assert.AreEqual(1, connection.Requests.Count(item => item.Method == "thread/search/stream"));
        Assert.AreEqual(1, connection.Requests.Count(item => item.Method == "thread/search/stream/cancel"));
        Assert.AreEqual(requestId, JsonDocument.Parse(JsonSerializer.Serialize(connection.Requests.Single(item => item.Method == "thread/search/stream/cancel").Parameters)).RootElement.GetProperty("requestId").GetString());
    }

    [TestMethod]
    public async Task ThreadSearchCancelsStreamAfterRemoteFailure()
    {
        var connection = new RecordingConnection
        {
            AsyncHandler = (method, _, _) => method == "thread/search/stream"
                ? throw new JsonRpcRemoteException(-32000, "search failed")
                : Task.FromResult(JsonSerializer.SerializeToElement(new { })),
        };
        await using var service = CreateService();
        await service.InitializeAsync(connection, Options(experimentalApi: true), CancellationToken.None);

        await Assert.ThrowsExactlyAsync<JsonRpcRemoteException>(() => service.SearchThreadsAsync(
            new ThreadSearchRequest { SearchTerm = "needle", RequestId = Guid.NewGuid().ToString("N") }, CancellationToken.None));

        Assert.AreEqual(1, connection.Requests.Count(item => item.Method == "thread/search/stream/cancel"));
    }
    [TestMethod]
    public async Task ThreadSearchTimeoutSendsCancelAndUnsubscribesPartials()
    {
        const string requestId = "8b00ec166b7f4becb9cfa11f75cc8aa7";
        RecordingConnection? connection = null;
        connection = new RecordingConnection
        {
            AsyncHandler = async (method, _, _) =>
            {
                if (method == "thread/search/stream")
                {
                    await connection!.EmitNotificationAsync("thread/search/partial", new
                    {
                        requestId,
                        data = new[] { new { thread = new { id = "before-timeout" }, snippet = "partial" } },
                    });
                    throw new OperationCanceledException("The correlated search request timed out.");
                }

                return JsonSerializer.SerializeToElement(new { });
            },
        };
        await using var service = CreateService();
        int partialCount = 0;
        service.ThreadSearchPartial += (_, _) =>
        {
            partialCount++;
            return Task.CompletedTask;
        };
        await service.InitializeAsync(connection, Options(experimentalApi: true), CancellationToken.None);

        await Assert.ThrowsExactlyAsync<OperationCanceledException>(() => service.SearchThreadsAsync(
            new ThreadSearchRequest { SearchTerm = "needle", RequestId = requestId }, CancellationToken.None));
        Assert.AreEqual(1, connection.Requests.Count(item => item.Method == "thread/search/stream/cancel"));
        Assert.AreEqual(1, partialCount, "The partial published before timeout should be delivered once.");
        await connection.EmitNotificationAsync("thread/search/partial", new
        {
            requestId,
            data = new[] { new { thread = new { id = "after-timeout" }, snippet = "late" } },
        });
        Assert.AreEqual(1, partialCount, "Timeout cleanup must detach the partial handler.");
    }
    [TestMethod]
    public async Task ThreadSearchReportsUnsupportedWhenExperimentalApiWasNotNegotiated()
    {
        var connection = new RecordingConnection();
        await using var service = CreateService();
        await service.InitializeAsync(connection, Options(experimentalApi: false), CancellationToken.None);

        ThreadSearchResult result = await service.SearchThreadsAsync(
            new ThreadSearchRequest { SearchTerm = "needle", RequestId = Guid.NewGuid().ToString("N") }, CancellationToken.None);

        Assert.IsFalse(result.IsSupported);
        Assert.IsFalse(connection.Requests.Any(item => item.Method is "thread/search" or "thread/search/stream"));
    }

    [TestMethod]
    public async Task ThreadSearchReportsUnsupportedOnlyForMethodNotFound()
    {
        var connection = new RecordingConnection
        {
            AsyncHandler = (method, _, _) => method is "thread/search" or "thread/search/stream"
                ? Task.FromException<JsonElement>(new JsonRpcRemoteException(-32601, "The requested search method is unavailable."))
                : Task.FromResult(JsonSerializer.SerializeToElement(new { })),
        };
        await using var service = CreateService();
        await service.InitializeAsync(connection, Options(experimentalApi: true), CancellationToken.None);

        ThreadSearchResult result = await service.SearchThreadsAsync(
            new ThreadSearchRequest { SearchTerm = "needle", RequestId = Guid.NewGuid().ToString("N") }, CancellationToken.None);

        Assert.IsFalse(result.IsSupported);
        Assert.AreEqual(1, connection.Requests.Count(item => item.Method is "thread/search" or "thread/search/stream"));
    }

    [TestMethod]
    public async Task ThreadSearchDoesNotTreatOtherRemoteErrorsAsUnsupported()
    {
        var connection = new RecordingConnection
        {
            AsyncHandler = (method, _, _) => method is "thread/search" or "thread/search/stream"
                ? Task.FromException<JsonElement>(new JsonRpcRemoteException(-32602, "Invalid search request."))
                : Task.FromResult(JsonSerializer.SerializeToElement(new { })),
        };
        await using var service = CreateService();
        await service.InitializeAsync(connection, Options(experimentalApi: true), CancellationToken.None);

        await Assert.ThrowsExactlyAsync<JsonRpcRemoteException>(() => service.SearchThreadsAsync(
            new ThreadSearchRequest { SearchTerm = "needle", RequestId = Guid.NewGuid().ToString("N") }, CancellationToken.None));
    }

    [TestMethod]
    public async Task ThreadSectionsArePagedAndSectionMetadataIsReadFromThread()
    {
        var connection = new RecordingConnection
        {
            Handler = (method, parameters) => method switch
            {
                "threadSection/list" when JsonSerializer.SerializeToElement(parameters).GetProperty("cursor").ValueKind == JsonValueKind.Null
                    => JsonSerializer.SerializeToElement(new { data = new[] { new { id = "section-1", name = "Inbox" } }, nextCursor = "next" }),
                "threadSection/list" => JsonSerializer.SerializeToElement(new { data = new[] { new { id = "section-2", name = "Done" } }, nextCursor = (string?)null }),
                "thread/list" => JsonSerializer.SerializeToElement(new
                {
                    data = new[] { new { id = "thread-1", section = new { id = "section-1", name = "Inbox" } } },
                    nextCursor = (string?)null,
                }),
                _ => JsonSerializer.SerializeToElement(new { }),
            },
        };
        await using var service = CreateService();
        await service.InitializeAsync(connection, Options(), CancellationToken.None);

        ThreadSectionsResult sections = await service.ListThreadSectionsAsync(CancellationToken.None);
        ThreadPage page = await service.ListThreadsAsync(null, CancellationToken.None);

        CollectionAssert.AreEqual(new[] { "section-1", "section-2" }, sections.Sections.Select(item => item.Id).ToArray());
        Assert.AreEqual(2, connection.Requests.Count(item => item.Method == "threadSection/list"));
        Assert.AreEqual("section-1", page.Threads.Single().SectionId);
        Assert.AreEqual("Inbox", page.Threads.Single().SectionName);
    }

    [TestMethod]
    public async Task ThreadListRejectsOversizedSectionAndSearchParameters()
    {
        var connection = new RecordingConnection();
        await using var service = CreateService();
        await service.InitializeAsync(connection, Options(), CancellationToken.None);

        await Assert.ThrowsExactlyAsync<ArgumentException>(() => service.ListThreadsAsync(
            null, null, false, new string('s', 513), null, CancellationToken.None));
        await Assert.ThrowsExactlyAsync<ArgumentException>(() => service.ListThreadsAsync(
            null, null, false, null, new string('s', 257), CancellationToken.None));
        Assert.IsFalse(connection.Requests.Any(item => item.Method == "thread/list"));
    }

    [TestMethod]
    public async Task ThreadHistoryNotificationsBoundThreadAndSectionIds()
    {
        var connection = new RecordingConnection();
        await using var service = CreateService();
        await service.InitializeAsync(connection, Options(), CancellationToken.None);
        ThreadHistoryChangedEvent? observed = null;
        service.ThreadHistoryChanged += (value, _) =>
        {
            observed = value;
            return Task.CompletedTask;
        };

        await connection.EmitNotificationAsync("thread/section/changed", new
        {
            threadId = new string('t', 257),
            sectionId = "section-1",
        });
        Assert.IsNull(observed);

        await connection.EmitNotificationAsync("thread/section/changed", new
        {
            threadId = "thread-1",
            sectionId = new string('s', 257),
        });
        Assert.AreEqual("thread-1", observed?.ThreadId);
        Assert.IsNull(observed?.SectionId);
    }
    [TestMethod]
    public async Task ThreadDeletePreservesSanitizedServerRejectionWithoutRetry()
    {
        var connection = new RecordingConnection
        {
            AsyncHandler = (method, _, _) => method == "thread/delete"
                ? Task.FromException<JsonElement>(new JsonRpcRemoteException(-32600, "Delete blocked because linked child threads remain."))
                : Task.FromResult(JsonSerializer.SerializeToElement(new { })),
        };
        await using var service = CreateService();
        await service.InitializeAsync(connection, Options(), CancellationToken.None);

        ThreadMutationResult result = await service.DeleteThreadAsync(
            new ThreadMutationRequest { ThreadId = "thread-parent", UserConfirmed = true }, CancellationToken.None);

        Assert.IsFalse(result.Succeeded);
        StringAssert.Contains(result.Message!, "linked child threads remain");
        Assert.AreEqual(1, connection.Requests.Count(item => item.Method == "thread/delete"));
    }
    private sealed class ThreadRequestContractEcho
    {
        public int CallCount { get; private set; }

        [JsonRpcMethod("test/echoThreadMutation", UseSingleObjectParameterDeserialization = true)]
        public ThreadMutationRequest EchoMutation(ThreadMutationRequest request)
        {
            CallCount++;
            return request;
        }

        [JsonRpcMethod("test/echoThreadSearch", UseSingleObjectParameterDeserialization = true)]
        public ThreadSearchRequest EchoSearch(ThreadSearchRequest request)
        {
            CallCount++;
            return request;
        }

        [JsonRpcMethod("test/echoListThreads", UseSingleObjectParameterDeserialization = true)]
        public ListThreadsRequest EchoListThreads(ListThreadsRequest request)
        {
            CallCount++;
            return request;
        }

        [JsonRpcMethod("test/echoStartTurn", UseSingleObjectParameterDeserialization = true)]
        public StartTurnRequest EchoStartTurn(StartTurnRequest request)
        {
            CallCount++;
            return request;
        }

        [JsonRpcMethod("test/echoSteerTurn", UseSingleObjectParameterDeserialization = true)]
        public SteerTurnRequest EchoSteerTurn(SteerTurnRequest request)
        {
            CallCount++;
            return request;
        }

        [JsonRpcMethod("test/echoInterruptTurn", UseSingleObjectParameterDeserialization = true)]
        public InterruptTurnRequest EchoInterruptTurn(InterruptTurnRequest request)
        {
            CallCount++;
            return request;
        }

        [JsonRpcMethod("test/echoReadState", UseSingleObjectParameterDeserialization = true)]
        public UpdateThreadReadStateRequest EchoReadState(UpdateThreadReadStateRequest request)
        {
            CallCount++;
            return request;
        }

        [JsonRpcMethod("test/echoFork", UseSingleObjectParameterDeserialization = true)]
        public ForkThreadRequest EchoFork(ForkThreadRequest request)
        {
            CallCount++;
            return request;
        }

        [JsonRpcMethod("test/echoWorkspaceSwitch", UseSingleObjectParameterDeserialization = true)]
        public SwitchWorkspaceRequest EchoWorkspaceSwitch(SwitchWorkspaceRequest request)
        {
            CallCount++;
            return request;
        }

        [JsonRpcMethod("test/echoThreadListScopes", UseSingleObjectParameterDeserialization = true)]
        public GetThreadListScopesRequest EchoThreadListScopes(GetThreadListScopesRequest request)
        {
            CallCount++;
            return request;
        }

    }
    private sealed class DenyThreadMutationPolicy : IApprovalPolicyEngine
    {
        public ApprovalPolicyResult EvaluateCommand(string? command, string? cwd, string workspaceRoot, string? networkHost, int? networkPort)
            => new(ApprovalRiskCategory.ReadOnly, "test-command", false, null);

        public ApprovalPolicyResult EvaluateFile(string? path, string workspaceRoot)
            => new(ApprovalRiskCategory.ReadOnly, "test-file", false, null);

        public ApprovalPolicyResult EvaluateThreadMutation(string? threadId, string? action)
            => new(ApprovalRiskCategory.Destructive, "denied-thread-mutation", true, "Thread mutation denied by test policy.");
    }
}
