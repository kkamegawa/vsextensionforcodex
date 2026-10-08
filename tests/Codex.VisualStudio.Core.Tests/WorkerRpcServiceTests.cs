using System.IO.Pipelines;
using System.Text.Json;
using System.Text.Json.Serialization;
using Codex.AppServer.Protocol;
using Codex.VisualStudio.Contracts;
using Codex.VisualStudio.Worker;
using StreamJsonRpc;

namespace Codex.VisualStudio.Core.Tests;

[TestClass]
public sealed class WorkerRpcServiceTests
{
    [TestMethod]
    public async Task StartTurn_PublishesBusyStatusCarryingTheTurnId()
    {
        // The interrupt button binds to IsTurnActive (Status.TurnId is not null). The worker must
        // publish a Busy status AFTER the turn id is known; otherwise the client only ever receives
        // Busy with TurnId = null and the interrupt button never appears while the turn runs.
        var connection = new StubConnection
        {
            Handler = method => method == "turn/start"
                ? JsonSerializer.SerializeToElement(new { turn = new { id = "turn-1" } })
                : JsonSerializer.SerializeToElement(new { }),
        };

        // The worker owns disposal of the session, so it is not disposed separately here.
        var session = new CodexSessionService(new ApprovalPolicyEngine(new PathAccessPolicy()), new SecretRedactor());
        await using var worker = new WorkerRpcService(new SecretRedactor(), new FakeProcessHost(connection), session);
        await worker.ConnectAsync(Options(), CancellationToken.None);
        await using var client = new ClientChannel(worker);

        await worker.StartTurnAsync(Scoped(worker, new StartTurnRequest { ThreadId = "thread-1", Text = "hello" }), CancellationToken.None);

        WorkerStatus published = await client.TurnIdSeen.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.AreEqual(WorkerConnectionState.Busy, published.State);
        Assert.AreEqual("turn-1", published.TurnId);
    }

    [TestMethod]
    public async Task LocalConnectCancellation_StaysCancellationAndStopsThePartialProcess()
    {
        // A recovery attempt deadline cancels the connect. It must surface as cancellation, not a
        // terminal Degraded status, so the coordinator can retry it as an unresponsive peer.
        using var cancellation = new CancellationTokenSource();
        var host = new FakeProcessHost(new StubConnection())
        {
            OnStartLocal = token =>
            {
                cancellation.Cancel();
                token.ThrowIfCancellationRequested();
                return Task.CompletedTask;
            },
        };
        var session = new CodexSessionService(new ApprovalPolicyEngine(new PathAccessPolicy()), new SecretRedactor());
        await using var worker = new WorkerRpcService(new SecretRedactor(), host, session);

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => worker.ConnectAsync(Options(), cancellation.Token));

        WorkerStatus status = await worker.GetStatusAsync(CancellationToken.None);
        Assert.AreEqual(1, host.Stops);
        Assert.AreEqual(WorkerConnectionState.Degraded, status.State);
        Assert.AreEqual(WorkerRecoveryFailureKind.Cancelled, status.RecoveryFailureKind);
    }

    [TestMethod]
    public async Task ListModels_DelegatesToSession()
    {
        var connection = new StubConnection
        {
            Handler = method => method == "model/list"
                ? JsonSerializer.SerializeToElement(new
                {
                    data = new[] { new { model = "gpt-5-codex", isDefault = true } },
                    nextCursor = (string?)null,
                })
                : JsonSerializer.SerializeToElement(new { }),
        };

        var session = new CodexSessionService(new ApprovalPolicyEngine(new PathAccessPolicy()), new SecretRedactor());
        await using var worker = new WorkerRpcService(new SecretRedactor(), new FakeProcessHost(connection), session);
        await worker.ConnectAsync(Options(), CancellationToken.None);

        ListModelsResult result = await worker.ListModelsAsync(Scoped(worker, new ListModelsRequest()), CancellationToken.None);

        Assert.AreEqual(1, result.Models.Count);
        Assert.AreEqual("gpt-5-codex", result.Models[0].Id);
        Assert.AreEqual("gpt-5-codex", result.DefaultModel);
    }

    [TestMethod]
    public async Task HistoryPagesAreOwnerStampedAndResumeRequiresExplicitConfirmation()
    {
        var connection = new StubConnection
        {
            Handler = method => method switch
            {
                "thread/list" => JsonSerializer.SerializeToElement(new { data = Array.Empty<object>(), nextCursor = (string?)null }),
                "thread/resume" => JsonSerializer.SerializeToElement(new { thread = new { id = "thread-1" } }),
                _ => JsonSerializer.SerializeToElement(new { }),
            },
        };
        var session = new CodexSessionService(new ApprovalPolicyEngine(new PathAccessPolicy()), new SecretRedactor());
        await using var worker = new WorkerRpcService(new SecretRedactor(), new FakeProcessHost(connection), session);
        await worker.ConnectAsync(Options(), CancellationToken.None);

        ThreadPage page = await worker.ListThreadsAsync(Scoped(worker, new ListThreadsRequest()), CancellationToken.None);
        WorkerStatus status = await worker.GetStatusAsync(CancellationToken.None);
        Assert.AreEqual(status.Target!.OwnerGeneration, page.OwnerGeneration);
        Assert.AreEqual(status.Target.Generation, page.ConnectionGeneration);
        Assert.AreEqual(status.Target.StatePartitionFingerprint, page.StatePartitionFingerprint);

        int resumeCallsBefore = connection.Methods.Count(method => method == "thread/resume");
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => worker.ResumeThreadAsync(
            Scoped(worker, new ResumeThreadRequest { ThreadId = "thread-1" }),
            CancellationToken.None));
        Assert.AreEqual(resumeCallsBefore, connection.Methods.Count(method => method == "thread/resume"));

        await worker.ResumeThreadAsync(
            Scoped(worker, new ResumeThreadRequest { ThreadId = "thread-1", UserConfirmed = true }),
            CancellationToken.None);
        Assert.AreEqual(resumeCallsBefore + 1, connection.Methods.Count(method => method == "thread/resume"));
    }

    [TestMethod]
    public async Task StartTurn_RejectsStaleSkillWithTypedCodeBeforeSendingTurnStart()
    {
        var connection = new StubConnection
        {
            Handler = method => method == "skills/list"
                ? JsonSerializer.SerializeToElement(new { data = Array.Empty<object>() })
                : JsonSerializer.SerializeToElement(new { }),
        };
        var session = new CodexSessionService(new ApprovalPolicyEngine(new PathAccessPolicy()), new SecretRedactor());
        await using var worker = new WorkerRpcService(new SecretRedactor(), new FakeProcessHost(connection), session);
        await worker.ConnectAsync(Options(), CancellationToken.None);

        LocalRpcException rejected = await Assert.ThrowsExactlyAsync<LocalRpcException>(() => worker.StartTurnAsync(
            Scoped(worker, new StartTurnRequest
            {
                ThreadId = "thread-1",
                Text = "run selected skill",
                Skill = new SkillInvocationInfo
                {
                    Name = "stale-skill",
                    Scope = "repo",
                    Path = "/repo/.codex/skills/stale-skill/SKILL.md",
                },
            }),
            CancellationToken.None));

        Assert.AreEqual(WorkerErrorCodes.SkillRejected, rejected.ErrorCode);
        Assert.IsTrue(connection.Methods.Contains("skills/list"));
        Assert.IsFalse(connection.Methods.Contains("turn/start"));
    }

    [TestMethod]
    public async Task StartTurn_ReclassifiesUpstreamErrorCodesThatOverlapLocalPreDispatchCodes()
    {
        var connection = new StubConnection
        {
            AsyncHandler = (method, _, _) => method == "turn/start"
                ? Task.FromException<JsonElement>(new RemoteInvocationException(
                    "remote attachment error",
                    WorkerErrorCodes.AttachmentRejected,
                    "RemoteError"))
                : Task.FromResult(JsonSerializer.SerializeToElement(new { })),
        };
        var session = new CodexSessionService(new ApprovalPolicyEngine(new PathAccessPolicy()), new SecretRedactor());
        await using var worker = new WorkerRpcService(new SecretRedactor(), new FakeProcessHost(connection), session);
        await worker.ConnectAsync(Options(), CancellationToken.None);

        LocalRpcException error = await Assert.ThrowsExactlyAsync<LocalRpcException>(() => worker.StartTurnAsync(
            Scoped(worker, new StartTurnRequest { ThreadId = "thread-1", Text = "hello" }),
            CancellationToken.None));

        Assert.AreEqual(WorkerErrorCodes.UpstreamOperationFailed, error.ErrorCode);
        Assert.IsTrue(connection.Methods.Contains("turn/start"));
    }

    [TestMethod]
    public async Task StartTurn_MapsSkillPreflightRpcFailureToPreDispatchRejection()
    {
        var connection = new StubConnection
        {
            AsyncHandler = (method, _, _) => method == "skills/list"
                ? Task.FromException<JsonElement>(new RemoteInvocationException("skill catalog unavailable", -32000, "RemoteError"))
                : Task.FromResult(JsonSerializer.SerializeToElement(new { })),
        };
        var session = new CodexSessionService(new ApprovalPolicyEngine(new PathAccessPolicy()), new SecretRedactor());
        await using var worker = new WorkerRpcService(new SecretRedactor(), new FakeProcessHost(connection), session);
        await worker.ConnectAsync(Options(), CancellationToken.None);

        LocalRpcException error = await Assert.ThrowsExactlyAsync<LocalRpcException>(() => worker.StartTurnAsync(
            Scoped(worker, new StartTurnRequest
            {
                ThreadId = "thread-1",
                Text = "hello",
                Skill = new SkillInvocationInfo { Name = "skill", Scope = "repo", Path = "/repo/SKILL.md" },
            }),
            CancellationToken.None));

        Assert.AreEqual(WorkerErrorCodes.PreDispatchRejected, error.ErrorCode);
        Assert.IsTrue(connection.Methods.Contains("skills/list"));
        Assert.IsFalse(connection.Methods.Contains("turn/start"));
    }

    [TestMethod]
    public async Task StartTurn_MapsCancellationInsideDispatchedRequestToUnknownOutcome()
    {
        var connection = new StubConnection
        {
            AsyncHandler = (method, _, _) => method == "turn/start"
                ? Task.FromException<JsonElement>(new OperationCanceledException("response wait cancelled"))
                : Task.FromResult(JsonSerializer.SerializeToElement(new { })),
        };
        var session = new CodexSessionService(new ApprovalPolicyEngine(new PathAccessPolicy()), new SecretRedactor());
        await using var worker = new WorkerRpcService(new SecretRedactor(), new FakeProcessHost(connection), session);
        await worker.ConnectAsync(Options(), CancellationToken.None);

        LocalRpcException error = await Assert.ThrowsExactlyAsync<LocalRpcException>(() => worker.StartTurnAsync(
            Scoped(worker, new StartTurnRequest { ThreadId = "thread-1", Text = "hello" }),
            CancellationToken.None));

        Assert.AreEqual(WorkerErrorCodes.UpstreamOperationFailed, error.ErrorCode);
        Assert.IsTrue(connection.Methods.Contains("turn/start"));
    }

    [TestMethod]
    public async Task ReadRequests_FromAnotherOwner_AreRejectedBeforeReachingTheAppServer()
    {
        var connection = new StubConnection();
        var session = new CodexSessionService(new ApprovalPolicyEngine(new PathAccessPolicy()), new SecretRedactor());
        await using var worker = new WorkerRpcService(new SecretRedactor(), new FakeProcessHost(connection), session);
        await worker.ConnectAsync(Options(), CancellationToken.None);
        int methodsBefore = connection.Methods.Count;

        ListModelsRequest stale = Scoped(worker, new ListModelsRequest());
        stale.OwnerGeneration++;

        LocalRpcException rejected = await Assert.ThrowsExactlyAsync<LocalRpcException>(
            async () => await worker.ListModelsAsync(stale, CancellationToken.None));

        Assert.AreEqual(methodsBefore, connection.Methods.Count);
        Assert.IsNotNull(rejected);
    }

    [TestMethod]
    public async Task SavedAttachmentMutation_RejectsOldConnectionGenerationForSameOwner()
    {
        var connection = new StubConnection();
        var session = new CodexSessionService(new ApprovalPolicyEngine(new PathAccessPolicy()), new SecretRedactor());
        await using var worker = new WorkerRpcService(new SecretRedactor(), new FakeProcessHost(connection), session);
        WorkerStatus current = await worker.ConnectAsync(Options(), CancellationToken.None);

        var stale = new SavedAttachmentAddRequest
        {
            ThreadId = "thread-1",
            LocalPath = Path.Combine(Path.GetTempPath(), "not-read-before-owner-validation.txt"),
            MimeType = "text/plain",
            DisplayName = "notes.txt",
            StatePartitionFingerprint = current.Target!.StatePartitionFingerprint,
            OwnerGeneration = current.Target.OwnerGeneration,
            ConnectionGeneration = current.Target.Generation - 1,
        };
        int callsBefore = connection.Methods.Count;

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            async () => await session.AddSavedAttachmentAsync(stale, CancellationToken.None));

        Assert.AreEqual(callsBefore, connection.Methods.Count);
        Assert.IsFalse(connection.Methods.Contains("thread/attachment/add", StringComparer.Ordinal));
    }

    [TestMethod]
    public async Task ShellMalformedAcknowledgementKeepsPendingLockAndDoesNotReplay()
    {
        string cwd = Options().WorkingDirectory;
        var connection = new StubConnection
        {
            Handler = method => method switch
            {
                "thread/start" => JsonSerializer.SerializeToElement(new { thread = new { id = "thread-1" } }),
                "thread/read" => JsonSerializer.SerializeToElement(new { thread = new { id = "thread-1", cwd } }),
                "thread/shellCommand" => JsonSerializer.SerializeToElement(new { unexpected = "not an empty acknowledgement" }),
                _ => JsonSerializer.SerializeToElement(new { }),
            },
        };
        var session = new CodexSessionService(new ApprovalPolicyEngine(new PathAccessPolicy()), new SecretRedactor());
        await using var worker = new WorkerRpcService(new SecretRedactor(), new FakeProcessHost(connection), session);
        await worker.ConnectAsync(Options(), CancellationToken.None);
        await worker.StartThreadAsync(Scoped(worker, new StartThreadRequest()), CancellationToken.None);
        await connection.EmitNotificationAsync("thread/status/changed", new { threadId = "thread-1", status = new { type = "idle" } });

        ShellCommandPrepareResult firstPrepared = await worker.PrepareShellCommandAsync(
            Scoped(worker, new ShellCommandPrepareRequest { ThreadId = "thread-1", Command = "echo one" }),
            CancellationToken.None);
        Assert.IsTrue(firstPrepared.IsEligible, firstPrepared.RejectionReason);
        ShellCommandExecuteResult first = await worker.ExecuteShellCommandAsync(
            Scoped(worker, new ShellCommandExecuteRequest { Confirmation = firstPrepared.Confirmation!, UserConfirmed = true }),
            CancellationToken.None);
        Assert.AreEqual(ShellCommandOutcome.OutcomeUnknown, first.Outcome);

        ShellCommandPrepareResult secondPrepared = await worker.PrepareShellCommandAsync(
            Scoped(worker, new ShellCommandPrepareRequest { ThreadId = "thread-1", Command = "echo two" }),
            CancellationToken.None);
        Assert.IsTrue(secondPrepared.IsEligible, secondPrepared.RejectionReason);
        ShellCommandExecuteResult second = await worker.ExecuteShellCommandAsync(
            Scoped(worker, new ShellCommandExecuteRequest { Confirmation = secondPrepared.Confirmation!, UserConfirmed = true }),
            CancellationToken.None);

        Assert.AreEqual(ShellCommandOutcome.NotSent, second.Outcome);
        Assert.AreEqual(1, connection.Methods.Count(method => method == "thread/shellCommand"));
    }

    [TestMethod]
    public async Task WindowsSandboxStartedFalseIsDefinitiveAndCannotBeRetriedInGeneration()
    {
        int setupCalls = 0;
        var connection = new StubConnection
        {
            Handler = method =>
            {
                if (method == "initialize")
                {
                    return JsonSerializer.SerializeToElement(new { platformOs = "windows", platformFamily = "windows" });
                }

                if (method == "windowsSandbox/setupStart")
                {
                    setupCalls++;
                    return JsonSerializer.SerializeToElement(new { started = false });
                }

                return JsonSerializer.SerializeToElement(new { });
            },
        };
        var session = new CodexSessionService(new ApprovalPolicyEngine(new PathAccessPolicy()), new SecretRedactor());
        await using var worker = new WorkerRpcService(new SecretRedactor(), new FakeProcessHost(connection), session);
        await worker.ConnectAsync(Options(), CancellationToken.None);
        WindowsSandboxSetupStartRequest request = Scoped(worker, new WindowsSandboxSetupStartRequest
        {
            Mode = WindowsSandboxSetupMode.Unelevated,
            Cwd = Options().WorkingDirectory,
        });

        WindowsSandboxSetupStartResult first = await worker.StartWindowsSandboxSetupAsync(request, CancellationToken.None);
        WindowsSandboxSetupStartResult second = await worker.StartWindowsSandboxSetupAsync(
            Scoped(worker, new WindowsSandboxSetupStartRequest { Mode = WindowsSandboxSetupMode.Unelevated, Cwd = Options().WorkingDirectory }),
            CancellationToken.None);

        Assert.AreEqual(WindowsSandboxSetupState.Failed, first.State);
        Assert.AreEqual(false, first.Started);
        Assert.AreEqual(WindowsSandboxSetupState.Failed, second.State);
        Assert.AreEqual(1, setupCalls);
    }

    [TestMethod]
    public async Task SavedAttachmentRemoveRejectsInvalidIdentityButAllowsKnownAbsentRecord()
    {
        var connection = new StubConnection
        {
            Handler = method => method == "thread/start"
                ? JsonSerializer.SerializeToElement(new { thread = new { id = "thread-1" } })
                : method == "thread/attachment/list"
                    ? JsonSerializer.SerializeToElement(new { data = Array.Empty<object>() })
                    : JsonSerializer.SerializeToElement(new { }),
        };
        var session = new CodexSessionService(new ApprovalPolicyEngine(new PathAccessPolicy()), new SecretRedactor());
        await using var worker = new WorkerRpcService(new SecretRedactor(), new FakeProcessHost(connection), session);
        await worker.ConnectAsync(Options(), CancellationToken.None);
        await worker.StartThreadAsync(Scoped(worker, new StartThreadRequest()), CancellationToken.None);
        int listCallsBefore = connection.Methods.Count(method => method == "thread/attachment/list");
        SavedAttachmentRemoveRequest malformed = Scoped(worker, new SavedAttachmentRemoveRequest
        {
            ThreadId = "thread-1",
            AttachmentType = SavedAttachmentPayloadRegistry.FileAttachmentType,
            IdentityKey = "not-a-valid-key",
            UserConfirmed = true,
        });

        SavedAttachmentMutationResult rejected = await worker.RemoveSavedAttachmentAsync(malformed, CancellationToken.None);
        SavedAttachmentMutationResult absent = await worker.RemoveSavedAttachmentAsync(
            Scoped(worker, new SavedAttachmentRemoveRequest
            {
                ThreadId = "thread-1",
                AttachmentType = SavedAttachmentPayloadRegistry.FileAttachmentType,
                IdentityKey = SavedAttachmentPayloadRegistry.CreateIdentityKey(ServerPath.Create("/workspace/missing.txt"), serverIsWindows: false),
                UserConfirmed = true,
            }),
            CancellationToken.None);

        Assert.AreEqual(AttachmentMutationOutcome.Rejected, rejected.Outcome);
        Assert.AreEqual(AttachmentMutationOutcome.AlreadyAbsent, absent.Outcome);
        Assert.AreEqual(listCallsBefore + 1, connection.Methods.Count(method => method == "thread/attachment/list"));
        Assert.IsFalse(connection.Methods.Contains("thread/attachment/remove", StringComparer.Ordinal));
    }

    [TestMethod]
    public async Task PendingTurnStart_DoesNotBlockOtherOwnerScopedRequests()
    {
        var releaseTurn = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var turnSeen = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var connection = new StubConnection
        {
            AsyncHandler = async (method, timeout, cancellationToken) =>
            {
                if (method == "turn/start")
                {
                    turnSeen.TrySetResult();
                    await releaseTurn.Task;
                    return JsonSerializer.SerializeToElement(new { turn = new { id = "turn-1" } });
                }

                return JsonSerializer.SerializeToElement(new { });
            },
        };
        var session = new CodexSessionService(new ApprovalPolicyEngine(new PathAccessPolicy()), new SecretRedactor());
        await using var worker = new WorkerRpcService(new SecretRedactor(), new FakeProcessHost(connection), session);
        await worker.ConnectAsync(Options(), CancellationToken.None);

        Task<string> turn = worker.StartTurnAsync(
            Scoped(worker, new StartTurnRequest { ThreadId = "thread-1", Text = "hello" }),
            CancellationToken.None);
        await turnSeen.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // An approval answer is delivered through a separate owner-scoped call while the turn is
        // still waiting on the app-server; it must not queue behind the turn.
        Task resolve = worker.ResolveApprovalAsync(
            Scoped(worker, new ResolveApprovalRequest { RequestId = "unknown", Decision = ApprovalDecision.Decline }),
            CancellationToken.None);
        await resolve.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.IsFalse(turn.IsCompleted);

        releaseTurn.SetResult();
        Assert.AreEqual("turn-1", await turn.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [TestMethod]
    public async Task ListSkills_DelegatesToSession()
    {
        var connection = new StubConnection
        {
            Handler = method => method == "skills/list"
                ? JsonSerializer.SerializeToElement(new
                {
                    data = new object[]
                    {
                        new
                        {
                            cwd = "/repo",
                            errors = Array.Empty<object>(),
                            skills = new object[]
                            {
                                new { name = "review-diff", description = "d", enabled = true, path = "/repo/.codex/skills/review-diff/SKILL.md", scope = "repo" },
                            },
                        },
                    },
                })
                : JsonSerializer.SerializeToElement(new { }),
        };

        var session = new CodexSessionService(new ApprovalPolicyEngine(new PathAccessPolicy()), new SecretRedactor());
        await using var worker = new WorkerRpcService(new SecretRedactor(), new FakeProcessHost(connection), session);
        await worker.ConnectAsync(Options(), CancellationToken.None);

        ListSkillsResult result = await worker.ListSkillsAsync(Scoped(worker, new ListSkillsRequest()), CancellationToken.None);

        Assert.IsTrue(result.IsSupported);
        Assert.AreEqual(1, result.Skills.Count);
        Assert.AreEqual("review-diff", result.Skills[0].Name);
    }

    [TestMethod]
    public async Task Connect_RejectsPreviousContractVersion()
    {
        var session = new CodexSessionService(new ApprovalPolicyEngine(new PathAccessPolicy()), new SecretRedactor());
        await using var worker = new WorkerRpcService(new SecretRedactor(), new FakeProcessHost(), session);

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => worker.ConnectAsync(new WorkerOptions { ContractVersion = ContractVersions.Current - 1 }, CancellationToken.None));
    }

    [TestMethod]
    public async Task UnsupportedSlashOperationDoesNotDegradeConnection()
    {
        var connection = new StubConnection
        {
            Handler = method => method switch
            {
                "thread/compact/start" => throw new JsonRpcRemoteException(-32601, "Method not found"),
                "account/read" => JsonSerializer.SerializeToElement(new { account = (object?)null }),
                _ => JsonSerializer.SerializeToElement(new { }),
            },
        };
        var session = new CodexSessionService(new ApprovalPolicyEngine(new PathAccessPolicy()), new SecretRedactor());
        await using var worker = new WorkerRpcService(
            new SecretRedactor(),
            new FakeProcessHost(connection),
            session);

        WorkerStatus connected = await worker.ConnectAsync(Options(), CancellationToken.None);
        CompactThreadResult result = await worker.CompactThreadAsync(
            Scoped(worker, new CompactThreadRequest { ThreadId = "thread-1" }),
            CancellationToken.None);
        WorkerStatus afterOperation = await worker.GetStatusAsync(CancellationToken.None);

        Assert.AreEqual(WorkerConnectionState.Ready, connected.State);
        Assert.IsFalse(result.IsSupported);
        Assert.AreEqual(WorkerConnectionState.Ready, afterOperation.State);
    }

    [TestMethod]
    public async Task CompactionCompletionRestoresReadyWhenNoTurnIsActive()
    {
        // thread/compact/start marks the worker Busy, but the app-server may report completion
        // only through the thread/compacted notification instead of turn/completed. The worker
        // must return to Ready so queued slash commands are not blocked forever.
        var connection = new StubConnection
        {
            Handler = method => method == "account/read"
                ? JsonSerializer.SerializeToElement(new { account = (object?)null })
                : JsonSerializer.SerializeToElement(new { }),
        };
        var session = new CodexSessionService(new ApprovalPolicyEngine(new PathAccessPolicy()), new SecretRedactor());
        await using var worker = new WorkerRpcService(
            new SecretRedactor(),
            new FakeProcessHost(connection),
            session);

        await worker.ConnectAsync(Options(), CancellationToken.None);
        CompactThreadResult result = await worker.CompactThreadAsync(
            Scoped(worker, new CompactThreadRequest { ThreadId = "thread-1" }),
            CancellationToken.None);
        WorkerStatus during = await worker.GetStatusAsync(CancellationToken.None);

        await connection.EmitNotificationAsync(
            "thread/compacted",
            new { threadId = "thread-1", turnId = "turn-9" });
        WorkerStatus after = await worker.GetStatusAsync(CancellationToken.None);

        Assert.IsTrue(result.IsSupported);
        Assert.AreEqual(WorkerConnectionState.Busy, during.State);
        Assert.AreEqual(WorkerConnectionState.Ready, after.State);
    }

    [TestMethod]
    public async Task ConnectionStatusPropagatesVersionAndClearsItAfterInitializationFailure()
    {
        bool failInitialization = false;
        var connection = new StubConnection
        {
            Handler = method => method switch
            {
                "initialize" when failInitialization => throw new InvalidOperationException("initialization failed"),
                "initialize" => JsonSerializer.SerializeToElement(new { userAgent = "codex-cli/3.4.5" }),
                "account/read" => JsonSerializer.SerializeToElement(new { account = (object?)null }),
                _ => JsonSerializer.SerializeToElement(new { }),
            },
        };
        var session = new CodexSessionService(new ApprovalPolicyEngine(new PathAccessPolicy()), new SecretRedactor());
        await using var worker = new WorkerRpcService(
            new SecretRedactor(),
            new FakeProcessHost(connection),
            session);

        WorkerStatus ready = await worker.ConnectAsync(Options(), CancellationToken.None);
        Assert.AreEqual(WorkerConnectionState.Ready, ready.State);
        Assert.AreEqual("3.4.5", ready.CodexVersion);

        failInitialization = true;
        WorkerStatus degraded = await worker.ConnectAsync(Options(), CancellationToken.None);

        Assert.AreEqual(WorkerConnectionState.Degraded, degraded.State);
        Assert.IsNull(degraded.CodexVersion);
        Assert.IsNull((await worker.GetStatusAsync(CancellationToken.None)).CodexVersion);
    }

    [TestMethod]
    public async Task ConnectedVersionSurvivesBusyApprovalAndReadyTransitions()
    {
        var connection = new StubConnection
        {
            Handler = method => method switch
            {
                "initialize" => JsonSerializer.SerializeToElement(new { userAgent = "codex-cli/4.5.6" }),
                "account/read" => JsonSerializer.SerializeToElement(new { account = (object?)null }),
                _ => JsonSerializer.SerializeToElement(new { }),
            },
        };
        var session = new CodexSessionService(new ApprovalPolicyEngine(new PathAccessPolicy()), new SecretRedactor());
        await using var worker = new WorkerRpcService(
            new SecretRedactor(),
            new FakeProcessHost(connection),
            session);
        var approvalSeen = new TaskCompletionSource<ApprovalRequest>(TaskCreationOptions.RunContinuationsAsynchronously);
        session.ApprovalRequested += (request, _) =>
        {
            approvalSeen.TrySetResult(request);
            return Task.CompletedTask;
        };

        WorkerStatus initialReady = await worker.ConnectAsync(Options(), CancellationToken.None);
        await worker.CompactThreadAsync(
            Scoped(worker, new CompactThreadRequest { ThreadId = "thread-1" }),
            CancellationToken.None);
        WorkerStatus busy = await worker.GetStatusAsync(CancellationToken.None);

        Task<JsonElement> approvalTask = connection.EmitRequestAsync(
            "approval-1",
            "item/commandExecution/requestApproval",
            new
            {
                command = "dotnet build",
                cwd = Options().WorkingDirectory,
                threadId = "thread-1",
                turnId = "turn-1",
                itemId = "item-1",
                startedAtMs = 1L,
            });
        ApprovalRequest approval = await approvalSeen.Task.WaitAsync(TimeSpan.FromSeconds(5));
        WorkerStatus waiting = await worker.GetStatusAsync(CancellationToken.None);

        await session.ResolveApprovalAsync(
            new ResolveApprovalRequest { RequestId = approval.RequestId, Decision = ApprovalDecision.Decline },
            CancellationToken.None);
        await approvalTask;
        await connection.EmitNotificationAsync(
            "thread/compacted",
            new { threadId = "thread-1", turnId = "turn-1" });
        WorkerStatus finalReady = await worker.GetStatusAsync(CancellationToken.None);

        Assert.AreEqual(WorkerConnectionState.Ready, initialReady.State);
        Assert.AreEqual("4.5.6", initialReady.CodexVersion);
        Assert.AreEqual(WorkerConnectionState.Busy, busy.State);
        Assert.AreEqual("4.5.6", busy.CodexVersion);
        Assert.AreEqual(WorkerConnectionState.WaitingForApproval, waiting.State);
        Assert.AreEqual("4.5.6", waiting.CodexVersion);
        Assert.AreEqual(WorkerConnectionState.Ready, finalReady.State);
        Assert.AreEqual("4.5.6", finalReady.CodexVersion);
    }

    [TestMethod]
    public async Task ThreadSettingsUpdatePublishesEffectiveApprovalStateAcrossWorkerContract()
    {
        var connection = new StubConnection
        {
            Handler = method => method == "thread/start"
                ? JsonSerializer.SerializeToElement(new
                {
                    thread = new { id = "thread-1" },
                    approvalPolicy = "on-request",
                    approvalsReviewer = "user",
                    sandbox = new { type = "workspaceWrite" },
                })
                : JsonSerializer.SerializeToElement(new { }),
        };
        var session = new CodexSessionService(new ApprovalPolicyEngine(new PathAccessPolicy()), new SecretRedactor());
        await using var worker = new WorkerRpcService(new SecretRedactor(), new FakeProcessHost(connection), session);
        await worker.ConnectAsync(Options(), CancellationToken.None);
        await worker.StartThreadAsync(Scoped(worker, new StartThreadRequest()), CancellationToken.None);
        await using var client = new ClientChannel(worker);

        await connection.EmitNotificationAsync(
            "thread/settings/updated",
            new
            {
                threadId = "thread-1",
                threadSettings = new
                {
                    activePermissionProfile = new { id = "review" },
                    approvalPolicy = "on-request",
                    approvalsReviewer = "auto_review",
                    sandboxPolicy = new { type = "workspaceWrite" },
                },
            });

        WorkerStatus published = await client.EffectiveStateSeen.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.AreEqual("review", published.EffectiveApprovalState!.ActivePermissionProfile);
        Assert.AreEqual("on-request", published.EffectiveApprovalState.ApprovalPolicy);
        Assert.AreEqual("auto_review", published.EffectiveApprovalState.ApprovalsReviewer);
        Assert.AreEqual("workspaceWrite", published.EffectiveApprovalState.SandboxMode);
    }

    [TestMethod]
    [DataRow(false, null)]
    [DataRow(true, null)]
    [DataRow(true, "canonical")]
    public async Task StartTurnRequest_V13PresencePairsSurviveRealRpcRoundTrip(bool hasValue, string? value)
    {
        var clientToServer = new Pipe();
        var serverToClient = new Pipe();
        using var serverRpc = new JsonRpc(new HeaderDelimitedMessageHandler(
            serverToClient.Writer, clientToServer.Reader, new SystemTextJsonFormatter()));
        using var clientRpc = new JsonRpc(new HeaderDelimitedMessageHandler(
            clientToServer.Writer, serverToClient.Reader, new SystemTextJsonFormatter()));
        var echo = new ContractEcho();
        serverRpc.AddLocalRpcTarget(echo);
        serverRpc.StartListening();
        clientRpc.StartListening();

        var request = new StartTurnRequest
        {
            ThreadId = "thread-1",
            Text = "round trip",
            HasEffort = hasValue,
            Effort = value,
            HasServiceTier = hasValue,
            ServiceTier = value,
        };

        StartTurnRequest result = await clientRpc.InvokeWithParameterObjectAsync<StartTurnRequest>(
            "test/echoStartTurn",
            request,
            CancellationToken.None);

        Assert.AreEqual(hasValue, result.HasEffort);
        Assert.AreEqual(value, result.Effort);
        Assert.AreEqual(hasValue, result.HasServiceTier);
        Assert.AreEqual(value, result.ServiceTier);
        Assert.AreEqual(1, echo.CallCount);
    }

    [TestMethod]
    public async Task RemoteConnectionLossPublishesDegradedStatus()
    {
        var connection = new StubConnection();
        var host = new FakeProcessHost(connection);
        var session = new CodexSessionService(new ApprovalPolicyEngine(new PathAccessPolicy()), new SecretRedactor());
        await using var worker = new WorkerRpcService(new SecretRedactor(), host, session);

        WorkerStatus connected = await worker.ConnectAsync(RemoteOptions(), CancellationToken.None);
        Assert.AreEqual(WorkerConnectionState.Ready, connected.State);
        Assert.AreEqual(1, host.RemoteStarts);

        connection.EmitClosed(new IOException("socket reset"));

        WorkerStatus lost = await WaitForStatusAsync(worker, WorkerConnectionState.Degraded);
        StringAssert.Contains(lost.Message, "Reconnect");
    }

    [TestMethod]
    public async Task FirstNetworkFailureOnStandardErrorMarksServerUnavailable()
    {
        var connection = new StubConnection();
        var host = new FakeProcessHost(connection);
        var session = new CodexSessionService(new ApprovalPolicyEngine(new PathAccessPolicy()), new SecretRedactor());
        await using var worker = new WorkerRpcService(new SecretRedactor(), host, session);
        await worker.ConnectAsync(Options(), CancellationToken.None);

        host.EmitStandardError(
            "ERROR codex_api::endpoint::responses_websocket: failed to connect to websocket: " +
            "IO error: ... (os error 11003), url: wss://chatgpt.com/backend-api/codex/responses");

        WorkerStatus degraded = await WaitForStatusAsync(worker, WorkerConnectionState.Degraded);
        Assert.AreEqual(WorkerRecoveryFailureKind.ServerUnavailable, degraded.RecoveryFailureKind);
    }

    [TestMethod]
    public async Task RemoteReconnectDoesNotReportIntentionalCloseAsConnectionLoss()
    {
        var connection = new StubConnection();
        var host = new FakeProcessHost(connection) { OnStop = () => connection.EmitClosed() };
        var session = new CodexSessionService(new ApprovalPolicyEngine(new PathAccessPolicy()), new SecretRedactor());
        await using var worker = new WorkerRpcService(new SecretRedactor(), host, session);
        WorkerOptions options = RemoteOptions();
        WorkerStatus connected = await worker.ConnectAsync(options, CancellationToken.None);

        WorkerStatus reconnected = await worker.ReconnectAsync(ReconnectRequest(connected, options), CancellationToken.None);
        await Task.Delay(50);

        Assert.AreEqual(WorkerConnectionState.Ready, reconnected.State);
        Assert.AreEqual(WorkerConnectionState.Ready, (await worker.GetStatusAsync(CancellationToken.None)).State);
        Assert.AreEqual(2, host.RemoteStarts);
        Assert.AreEqual(connected.Target!.Generation + 1, reconnected.Target!.Generation);
    }

    [TestMethod]
    public async Task RemoteCloseDuringConnectIsPublishedAfterTheConnectCompletes()
    {
        var connection = new StubConnection();
        bool closed = false;
        connection.Handler = method =>
        {
            // Close while ConnectAsync holds the transition gate and already observes the socket.
            if (method == "account/read" && !closed)
            {
                closed = true;
                connection.EmitClosed(new IOException("socket reset"));
            }

            return JsonSerializer.SerializeToElement(new { });
        };
        var host = new FakeProcessHost(connection);
        var session = new CodexSessionService(new ApprovalPolicyEngine(new PathAccessPolicy()), new SecretRedactor());
        await using var worker = new WorkerRpcService(new SecretRedactor(), host, session);

        WorkerStatus result = await worker.ConnectAsync(RemoteOptions(), CancellationToken.None);

        // The close fails the startup account read, so the candidate is retired and the attempt
        // ends Degraded; no later loss publication can overwrite or duplicate it.
        Assert.AreEqual(WorkerConnectionState.Degraded, result.State);
        await Task.Delay(100);
        WorkerStatus lost = await worker.GetStatusAsync(CancellationToken.None);
        Assert.AreEqual(WorkerConnectionState.Degraded, lost.State);
        Assert.AreEqual(RemoteConnectionException.Describe(RemoteConnectionFailure.AccountReadFailed), lost.Message);
        Assert.IsFalse(lost.Message.Contains("socket reset", StringComparison.Ordinal));
        Assert.IsTrue(host.Stops >= 1);
    }

    [TestMethod]
    [DataRow(null, "/srv/repo", DisplayName = "missing localRoot")]
    [DataRow("C:\\repo", null, DisplayName = "missing serverRoot")]
    public async Task RemoteConnectWithOneRootIsRejected(string? localRoot, string? serverRoot)
    {
        var host = new FakeProcessHost(new StubConnection());
        var session = new CodexSessionService(new ApprovalPolicyEngine(new PathAccessPolicy()), new SecretRedactor());
        await using var worker = new WorkerRpcService(new SecretRedactor(), host, session);
        WorkerOptions options = RemoteOptions();
        options.LocalRoot = localRoot;
        options.ServerRoot = serverRoot;

        // A single root is a malformed mapping for any transport and is rejected before connecting.
        InvalidOperationException ex = await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => worker.ConnectAsync(options, CancellationToken.None));

        StringAssert.Contains(ex.Message, "localRoot and serverRoot");
        Assert.AreEqual(0, host.RemoteStarts);
    }

    [TestMethod]
    public async Task RemoteConnectWithoutRootsIsRejectedWithoutSendingLocalPaths()
    {
        var host = new FakeProcessHost(new StubConnection());
        var session = new CodexSessionService(new ApprovalPolicyEngine(new PathAccessPolicy()), new SecretRedactor());
        await using var worker = new WorkerRpcService(new SecretRedactor(), host, session);
        WorkerOptions options = RemoteOptions();
        options.LocalRoot = null;
        options.ServerRoot = null;

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => worker.ConnectAsync(options, CancellationToken.None));
        Assert.AreEqual(0, host.RemoteStarts);
    }

    [TestMethod]
    public async Task RemoteConnectWithBothRootsIsAccepted()
    {
        var host = new FakeProcessHost(new StubConnection());
        var session = new CodexSessionService(new ApprovalPolicyEngine(new PathAccessPolicy()), new SecretRedactor());
        await using var worker = new WorkerRpcService(new SecretRedactor(), host, session);

        WorkerStatus status = await worker.ConnectAsync(RemoteOptions(), CancellationToken.None);

        Assert.AreEqual(WorkerConnectionState.Ready, status.State);
        Assert.AreEqual(1, host.RemoteStarts);
    }

    [TestMethod]
    public async Task RemoteRestartIsRejectedBeforeAnythingIsStoppedOrSent()
    {
        var connection = new StubConnection();
        var host = new FakeProcessHost(connection);
        var session = new CodexSessionService(new ApprovalPolicyEngine(new PathAccessPolicy()), new SecretRedactor());
        await using var worker = new WorkerRpcService(new SecretRedactor(), host, session);
        await worker.ConnectAsync(RemoteOptions(), CancellationToken.None);
        int sent = connection.Methods.Count;

        LocalRpcException ex = await Assert.ThrowsExactlyAsync<LocalRpcException>(
            () => worker.RestartAsync(CancellationToken.None));

        Assert.AreEqual(WorkerErrorCodes.ConnectionOperationRejected, ex.ErrorCode);
        Assert.AreEqual(nameof(ConnectionOperationRejectionReason.LocalProcessRequired), ex.ErrorData);
        Assert.AreEqual(0, host.Stops);
        Assert.AreEqual(1, host.RemoteStarts);
        Assert.AreEqual(sent, connection.Methods.Count);
        Assert.AreEqual(WorkerConnectionState.Ready, (await worker.GetStatusAsync(CancellationToken.None)).State);
    }

    [TestMethod]
    public async Task ReconnectOfALocalConnectionIsRejected()
    {
        var host = new FakeProcessHost(new StubConnection());
        var session = new CodexSessionService(new ApprovalPolicyEngine(new PathAccessPolicy()), new SecretRedactor());
        await using var worker = new WorkerRpcService(new SecretRedactor(), host, session);
        WorkerStatus connected = await worker.ConnectAsync(Options(), CancellationToken.None);

        LocalRpcException ex = await Assert.ThrowsExactlyAsync<LocalRpcException>(() => worker.ReconnectAsync(
            new RemoteReconnectRequest { ProfileName = "x", Fingerprint = "y", ExpectedGeneration = connected.Target!.Generation },
            CancellationToken.None));

        Assert.AreEqual(nameof(ConnectionOperationRejectionReason.RemoteConnectionRequired), ex.ErrorData);
        Assert.AreEqual(0, host.Stops);
    }

    [TestMethod]
    public async Task ReconnectRejectsChangedProfileAndStaleGenerationBeforeStopping()
    {
        var host = new FakeProcessHost(new StubConnection());
        var session = new CodexSessionService(new ApprovalPolicyEngine(new PathAccessPolicy()), new SecretRedactor());
        await using var worker = new WorkerRpcService(new SecretRedactor(), host, session);
        WorkerOptions options = RemoteOptions();
        WorkerStatus connected = await worker.ConnectAsync(options, CancellationToken.None);
        RemoteReconnectRequest valid = ReconnectRequest(connected, options);

        foreach ((RemoteReconnectRequest request, ConnectionOperationRejectionReason expected) in new[]
        {
            (new RemoteReconnectRequest { ProfileName = valid.ProfileName, Fingerprint = "changed", ExpectedGeneration = valid.ExpectedGeneration }, ConnectionOperationRejectionReason.ProfileChanged),
            (new RemoteReconnectRequest { ProfileName = "Other", Fingerprint = valid.Fingerprint, ExpectedGeneration = valid.ExpectedGeneration }, ConnectionOperationRejectionReason.ProfileChanged),
            (new RemoteReconnectRequest { ProfileName = valid.ProfileName, Fingerprint = valid.Fingerprint, ExpectedGeneration = valid.ExpectedGeneration - 1 }, ConnectionOperationRejectionReason.StaleGeneration),
        })
        {
            LocalRpcException ex = await Assert.ThrowsExactlyAsync<LocalRpcException>(
                () => worker.ReconnectAsync(request, CancellationToken.None));
            Assert.AreEqual(WorkerErrorCodes.ConnectionOperationRejected, ex.ErrorCode);
            Assert.AreEqual(expected.ToString(), ex.ErrorData);
        }

        Assert.AreEqual(0, host.Stops);
        Assert.AreEqual(1, host.RemoteStarts);
    }

    [TestMethod]
    public async Task ReconnectWithoutAppliedProfileIdentityIsRejectedAsUnavailable()
    {
        var host = new FakeProcessHost(new StubConnection());
        var session = new CodexSessionService(new ApprovalPolicyEngine(new PathAccessPolicy()), new SecretRedactor());
        await using var worker = new WorkerRpcService(new SecretRedactor(), host, session);
        WorkerOptions options = RemoteOptions();
        options.RemoteProfileName = null;
        options.RemoteProfileFingerprint = null;
        WorkerStatus connected = await worker.ConnectAsync(options, CancellationToken.None);

        LocalRpcException ex = await Assert.ThrowsExactlyAsync<LocalRpcException>(() => worker.ReconnectAsync(
            new RemoteReconnectRequest { ProfileName = "Build box", Fingerprint = "f", ExpectedGeneration = connected.Target!.Generation },
            CancellationToken.None));

        Assert.AreEqual(nameof(ConnectionOperationRejectionReason.ProfileUnavailable), ex.ErrorData);
        Assert.AreEqual(0, host.Stops);
    }

    [TestMethod]
    public async Task StatusCarriesTargetSnapshotAndReportsAPidOnlyForAnOwnedLocalProcess()
    {
        var host = new FakeProcessHost(new StubConnection());
        var session = new CodexSessionService(new ApprovalPolicyEngine(new PathAccessPolicy()), new SecretRedactor());
        await using var worker = new WorkerRpcService(new SecretRedactor(), host, session);

        WorkerStatus local = await worker.ConnectAsync(Options(), CancellationToken.None);
        WorkerOptions options = RemoteOptions();
        WorkerStatus remote = await worker.ConnectAsync(options, CancellationToken.None);

        Assert.AreEqual(ConnectionTargetKind.Local, local.Target!.Kind);
        Assert.AreEqual(4242, local.ProcessId);
        Assert.AreEqual(ConnectionTargetKind.Remote, remote.Target!.Kind);
        Assert.AreEqual("Build box", remote.Target.DisplayName);
        Assert.AreEqual(options.RemoteProfileFingerprint, remote.Target.Fingerprint);
        Assert.AreEqual(local.Target.Generation + 1, remote.Target.Generation);
        Assert.IsNull(remote.ProcessId);
    }

    [TestMethod]
    public async Task ConnectingStatusReportsTheIntendedRemoteTarget()
    {
        var host = new FakeProcessHost(new StubConnection());
        var session = new CodexSessionService(new ApprovalPolicyEngine(new PathAccessPolicy()), new SecretRedactor());
        await using var worker = new WorkerRpcService(new SecretRedactor(), host, session);
        WorkerStatus? during = null;
        host.OnStartRemote = async (_, _) => during = await worker.GetStatusAsync(CancellationToken.None);

        await worker.ConnectAsync(RemoteOptions(), CancellationToken.None);

        Assert.AreEqual(WorkerConnectionState.Connecting, during!.State);
        Assert.AreEqual(ConnectionTargetKind.Remote, during.Target!.Kind);
        Assert.AreEqual("Build box", during.Target.DisplayName);
    }

    [TestMethod]
    public async Task RemoteStartupOverallDeadlineRetiresTheCandidateAsTimeout()
    {
        var host = new FakeProcessHost(new StubConnection())
        {
            OnStartRemote = (_, cancellationToken) => Task.Delay(Timeout.Infinite, cancellationToken),
        };
        var session = new CodexSessionService(new ApprovalPolicyEngine(new PathAccessPolicy()), new SecretRedactor());
        var limits = RemoteStartupLimits.Default with { Overall = TimeSpan.FromMilliseconds(200) };
        await using var worker = new WorkerRpcService(new SecretRedactor(), host, session, limits: limits);

        WorkerStatus status = await worker.ConnectAsync(RemoteOptions(), CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.AreEqual(WorkerConnectionState.Degraded, status.State);
        Assert.AreEqual(RemoteConnectionException.Describe(RemoteConnectionFailure.Timeout), status.Message);
        Assert.IsTrue(host.Stops >= 1);
    }

    [TestMethod]
    public async Task RemoteStartupCallerCancellationIsPreservedAsCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        var host = new FakeProcessHost(new StubConnection())
        {
            OnStartRemote = async (_, cancellationToken) =>
            {
                cancellation.Cancel();
                await Task.Delay(Timeout.Infinite, cancellationToken);
            },
        };
        var session = new CodexSessionService(new ApprovalPolicyEngine(new PathAccessPolicy()), new SecretRedactor());
        await using var worker = new WorkerRpcService(new SecretRedactor(), host, session);

        await Assert.ThrowsAsync<OperationCanceledException>(() => worker.ConnectAsync(RemoteOptions(), cancellation.Token));

        WorkerStatus status = await worker.GetStatusAsync(CancellationToken.None);
        Assert.AreEqual(WorkerConnectionState.Degraded, status.State);
        Assert.IsTrue(host.Stops >= 1);
    }

    [TestMethod]
    public async Task RemoteStartupStageCapIsReportedAsTimeout()
    {
        var host = new FakeProcessHost(new StubConnection
        {
            AsyncHandler = async (method, timeout, cancellationToken) =>
            {
                await Task.Delay(method == "initialize" ? Timeout.Infinite : 0, cancellationToken);
                return JsonSerializer.SerializeToElement(new { });
            },
        });
        var session = new CodexSessionService(new ApprovalPolicyEngine(new PathAccessPolicy()), new SecretRedactor());
        var limits = RemoteStartupLimits.Default with { Initialize = TimeSpan.FromMilliseconds(150) };
        await using var worker = new WorkerRpcService(new SecretRedactor(), host, session, limits: limits);

        WorkerStatus status = await worker.ConnectAsync(RemoteOptions(), CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.AreEqual(WorkerConnectionState.Degraded, status.State);
        Assert.AreEqual(RemoteConnectionException.Describe(RemoteConnectionFailure.Timeout), status.Message);
    }

    [TestMethod]
    public async Task InitializeFailureIsDistinctFromAccountReadFailure()
    {
        async Task<WorkerStatus> ConnectFailingOnAsync(string failingMethod)
        {
            var connection = new StubConnection
            {
                Handler = method => method == failingMethod
                    ? throw new JsonRpcConnectionClosedException("closed with Bearer secret-value-that-must-not-leak")
                    : JsonSerializer.SerializeToElement(new { }),
            };
            var host = new FakeProcessHost(connection);
            var session = new CodexSessionService(new ApprovalPolicyEngine(new PathAccessPolicy()), new SecretRedactor());
            await using var worker = new WorkerRpcService(new SecretRedactor(), host, session);
            WorkerStatus status = await worker.ConnectAsync(RemoteOptions(), CancellationToken.None);
            Assert.IsTrue(host.Stops >= 1, "The failed candidate must be retired.");
            return status;
        }

        WorkerStatus initialize = await ConnectFailingOnAsync("initialize");
        WorkerStatus account = await ConnectFailingOnAsync("account/read");

        Assert.AreEqual(RemoteConnectionException.Describe(RemoteConnectionFailure.InitializeFailed), initialize.Message);
        Assert.AreEqual(RemoteConnectionException.Describe(RemoteConnectionFailure.AccountReadFailed), account.Message);
        Assert.IsFalse(initialize.Message.Contains("secret-value", StringComparison.Ordinal));
        Assert.IsFalse(account.Message.Contains("secret-value", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task UnavailableAccountReadKeepsAnInitializedRemoteConnectionReady()
    {
        var connection = new StubConnection
        {
            Handler = method => method == "account/read"
                ? throw new JsonRpcRemoteException(-32603, "internal")
                : JsonSerializer.SerializeToElement(new { }),
        };
        var session = new CodexSessionService(new ApprovalPolicyEngine(new PathAccessPolicy()), new SecretRedactor());
        await using var worker = new WorkerRpcService(new SecretRedactor(), new FakeProcessHost(connection), session);

        WorkerStatus status = await worker.ConnectAsync(RemoteOptions(), CancellationToken.None);

        Assert.AreEqual(WorkerConnectionState.Ready, status.State);
        Assert.AreEqual(AccountState.Unavailable, (await worker.GetAccountStatusAsync(CancellationToken.None)).State);
    }

    [TestMethod]
    public async Task WatchdogRetiresOnlyTheCapturedSocketAfterTwoSilentProbes()
    {
        bool silent = false;
        var connection = new StubConnection
        {
            AsyncHandler = async (method, timeout, cancellationToken) =>
            {
                if (silent && method == "account/read")
                {
                    await Task.Delay(timeout, cancellationToken);
                    throw new OperationCanceledException("request timeout");
                }

                return JsonSerializer.SerializeToElement(new { });
            },
        };
        var host = new FakeProcessHost(connection);
        var session = new CodexSessionService(new ApprovalPolicyEngine(new PathAccessPolicy()), new SecretRedactor());
        var timing = new RemoteWatchdogTiming(TimeSpan.FromMilliseconds(60), TimeSpan.FromMilliseconds(80));
        await using var worker = new WorkerRpcService(new SecretRedactor(), host, session, watchdogTiming: timing);
        await worker.ConnectAsync(RemoteOptions(), CancellationToken.None);
        int sentAtReady = connection.Methods.Count;
        silent = true;

        WorkerStatus degraded = await WaitForStatusAsync(worker, WorkerConnectionState.Degraded);

        Assert.AreEqual(RemoteConnectionException.Describe(RemoteConnectionFailure.PeerUnresponsive), degraded.Message);
        Assert.AreEqual(1, host.Stops);
        Assert.AreEqual(1, host.RemoteStarts, "The watchdog must never reconnect.");
        string[] probes = connection.Methods.Skip(sentAtReady).ToArray();
        Assert.AreEqual(2, probes.Length);
        Assert.IsTrue(probes.All(static method => method == "account/read"));
    }

    [TestMethod]
    public async Task WatchdogKeepsAConnectionWhosePeerAnswersOrStaysActive()
    {
        int probes = 0;
        var connection = new StubConnection();
        connection.AsyncHandler = async (method, timeout, cancellationToken) =>
        {
            if (method == "account/read" && Interlocked.Increment(ref probes) > 1)
            {
                // Odd probes: inbound traffic arrives while the probe is outstanding, then it times
                // out. Even probes: a successful SignedOut-shaped answer.
                if (probes % 2 == 1)
                {
                    connection.RecordInboundActivity();
                    await Task.Delay(timeout, cancellationToken);
                    throw new OperationCanceledException("request timeout");
                }

                return JsonSerializer.SerializeToElement(new { account = (object?)null, requiresOpenaiAuth = true });
            }

            return JsonSerializer.SerializeToElement(new { });
        };
        var host = new FakeProcessHost(connection);
        var session = new CodexSessionService(new ApprovalPolicyEngine(new PathAccessPolicy()), new SecretRedactor());
        var timing = new RemoteWatchdogTiming(TimeSpan.FromMilliseconds(40), TimeSpan.FromMilliseconds(60));
        await using var worker = new WorkerRpcService(new SecretRedactor(), host, session, watchdogTiming: timing);
        await worker.ConnectAsync(RemoteOptions(), CancellationToken.None);

        await Task.Delay(700);

        Assert.IsTrue(Volatile.Read(ref probes) >= 4, "The watchdog should keep probing an idle connection.");
        Assert.AreEqual(WorkerConnectionState.Ready, (await worker.GetStatusAsync(CancellationToken.None)).State);
        Assert.AreEqual(0, host.Stops);
        Assert.IsTrue(connection.Methods.All(static method => method is "initialize" or "account/gatewayOAuth/read" or "account/read"));
    }

    [TestMethod]
    public async Task WatchdogOfASupersededGenerationCannotDegradeTheNewConnection()
    {
        bool silent = true;
        var connection = new StubConnection();
        bool connected = false;
        connection.AsyncHandler = async (method, timeout, cancellationToken) =>
        {
            if (connected && silent && method == "account/read")
            {
                await Task.Delay(timeout, cancellationToken);
                throw new OperationCanceledException("request timeout");
            }

            return JsonSerializer.SerializeToElement(new { });
        };
        var host = new FakeProcessHost(connection);
        var session = new CodexSessionService(new ApprovalPolicyEngine(new PathAccessPolicy()), new SecretRedactor());
        var timing = new RemoteWatchdogTiming(TimeSpan.FromMilliseconds(80), TimeSpan.FromMilliseconds(200));
        await using var worker = new WorkerRpcService(new SecretRedactor(), host, session, watchdogTiming: timing);
        WorkerOptions options = RemoteOptions();
        WorkerStatus first = await worker.ConnectAsync(options, CancellationToken.None);
        connected = true;

        // Let the first generation's watchdog start its first silent probe, then reconnect.
        await Task.Delay(120);
        silent = false;
        connected = false;
        WorkerStatus second = await worker.ReconnectAsync(ReconnectRequest(first, options), CancellationToken.None);
        connected = true;
        await Task.Delay(600);

        WorkerStatus current = await worker.GetStatusAsync(CancellationToken.None);
        Assert.AreEqual(WorkerConnectionState.Ready, current.State);
        Assert.AreEqual(second.Target!.Generation, current.Target!.Generation);
    }

    [TestMethod]
    public async Task WatchdogMeasuresSilenceFromTheLastInboundMessage()
    {
        // Activity early in the first window must not push probing out to a second full window:
        // the first probe follows one idle window after that message, not two after start.
        var idleWindow = TimeSpan.FromMilliseconds(400);
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var firstProbe = new TaskCompletionSource<TimeSpan>(TaskCreationOptions.RunContinuationsAsynchronously);
        var connection = new StubConnection
        {
            AsyncHandler = (method, timeout, cancellationToken) =>
            {
                firstProbe.TrySetResult(clock.Elapsed);
                return Task.FromResult(JsonSerializer.SerializeToElement(new { }));
            },
        };
        using var watchdog = new RemoteIdleWatchdog(
            connection,
            connection,
            new RemoteWatchdogTiming(idleWindow, TimeSpan.FromMilliseconds(200)),
            TimeProvider.System,
            static (_, _) => Task.FromResult(false));
        watchdog.Start();

        await Task.Delay(50);
        TimeSpan activityAt = clock.Elapsed;
        connection.RecordInboundActivity();
        TimeSpan probeAt = await firstProbe.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.IsTrue(probeAt - activityAt >= idleWindow - TimeSpan.FromMilliseconds(30), $"Probed after {probeAt - activityAt} of silence.");
        Assert.IsTrue(probeAt < idleWindow * 1.75, $"Probed {probeAt} after start; silence was not measured from the last message.");
    }

    [TestMethod]
    public async Task WatchdogKeepsWatchingWhenActivityArrivesBeforeRetirement()
    {
        // A message that arrives after both probes timed out (for example while the owner waits
        // for its transition gate) must keep the socket and restart the idle window.
        var connection = new StubConnection
        {
            AsyncHandler = async (method, timeout, cancellationToken) =>
            {
                await Task.Delay(timeout, cancellationToken);
                throw new OperationCanceledException("request timeout");
            },
        };
        var silentChecks = new List<bool>();
        var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var watchdog = new RemoteIdleWatchdog(
            connection,
            connection,
            new RemoteWatchdogTiming(TimeSpan.FromMilliseconds(50), TimeSpan.FromMilliseconds(30)),
            TimeProvider.System,
            (isStillSilent, _) =>
            {
                if (silentChecks.Count == 0)
                {
                    connection.RecordInboundActivity();
                    silentChecks.Add(isStillSilent());
                    return Task.FromResult(true);
                }

                silentChecks.Add(isStillSilent());
                stopped.TrySetResult();
                return Task.FromResult(false);
            });
        watchdog.Start();

        await stopped.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await watchdog.Completion.WaitAsync(TimeSpan.FromSeconds(5));

        CollectionAssert.AreEqual(new[] { false, true }, silentChecks, "Activity before retirement must be seen; a later silent episode must not.");
        Assert.AreEqual(4, connection.Methods.Count, "Each episode sends exactly two probes.");
    }

    [TestMethod]
    public async Task DiagnoseNeverChangesConnectionStateOrSendsRpc()
    {
        var connection = new StubConnection();
        var host = new FakeProcessHost(connection);
        var session = new CodexSessionService(new ApprovalPolicyEngine(new PathAccessPolicy()), new SecretRedactor());
        var diagnostics = new FakeDiagnostics();
        await using var worker = new WorkerRpcService(new SecretRedactor(), host, session, diagnostics);
        WorkerStatus before = await worker.ConnectAsync(RemoteOptions(), CancellationToken.None);
        int sent = connection.Methods.Count;

        ConnectionDiagnosticsResult result = await worker.DiagnoseConnectionAsync(
            new ConnectionDiagnosticsRequest { ProfileName = "Other", Endpoint = "wss://other.example.invalid" },
            CancellationToken.None);

        WorkerStatus after = await worker.GetStatusAsync(CancellationToken.None);
        Assert.AreEqual("Other", result.ProfileName);
        Assert.AreEqual(1, diagnostics.Calls);
        Assert.AreEqual(before.State, after.State);
        Assert.AreEqual(before.Target!.Generation, after.Target!.Generation);
        Assert.AreEqual(sent, connection.Methods.Count);
        Assert.AreEqual(0, host.Stops);
        Assert.AreEqual(1, host.RemoteStarts);
    }

    private sealed class FakeDiagnostics : IRemoteConnectionDiagnostics
    {
        public int Calls { get; private set; }

        public Task<ConnectionDiagnosticsResult> DiagnoseAsync(ConnectionDiagnosticsRequest request, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(new ConnectionDiagnosticsResult
            {
                ProfileName = request.ProfileName,
                Health = new HealthProbeResult { State = HealthProbeState.Healthy, HttpStatus = 200 },
                Ready = new HealthProbeResult { State = HealthProbeState.Unhealthy, HttpStatus = 503 },
            });
        }
    }

    private static async Task<WorkerStatus> WaitForStatusAsync(
        WorkerRpcService worker,
        WorkerConnectionState state,
        string? messageFragment = null)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (true)
        {
            WorkerStatus current = await worker.GetStatusAsync(CancellationToken.None);
            if (current.State == state
                && (messageFragment is null || current.Message.Contains(messageFragment, StringComparison.Ordinal)))
            {
                return current;
            }

            await Task.Delay(10, timeout.Token);
        }
    }

    private static WorkerOptions RemoteOptions()
    {
        var options = new WorkerOptions
        {
            WorkingDirectory = Path.GetTempPath(),
            ExtensionVersion = "test",
            RemoteEndpoint = "wss://app-server.example.invalid",
            RemoteTokenFilePath = Path.Combine(Path.GetTempPath(), "unused.token"),
            LocalRoot = Path.GetTempPath(),
            ServerRoot = "/srv/repo",
            RemoteProfileName = "Build box",
        };
        options.RemoteProfileFingerprint = Fingerprint(options);
        return options;
    }

    private static string Fingerprint(WorkerOptions options) => RemoteProfileFingerprint.Compute(
        options.RemoteProfileName,
        options.RemoteEndpoint,
        options.RemoteTokenFilePath,
        options.LocalRoot,
        options.ServerRoot,
        enabled: true);

    private static RemoteReconnectRequest ReconnectRequest(WorkerStatus status, WorkerOptions options) => new()
    {
        ProfileName = options.RemoteProfileName!,
        Fingerprint = options.RemoteProfileFingerprint!,
        ExpectedGeneration = status.Target!.Generation,
    };

    private static WorkerOptions Options() => new()
    {
        WorkingDirectory = Path.GetTempPath(),
        ExtensionVersion = "test",
    };

    private static TRequest Scoped<TRequest>(WorkerRpcService worker, TRequest request)
        where TRequest : OwnerScopedRequest
    {
        WorkerStatus status = worker.GetStatusAsync(CancellationToken.None).GetAwaiter().GetResult();
        request.StatePartitionFingerprint = status.Target!.StatePartitionFingerprint!;
        request.OwnerGeneration = status.Target.OwnerGeneration;
        request.ConnectionGeneration = status.Target.Generation;
        return request;
    }

    // Captures observer/stateChanged notifications published by the worker over a real StreamJsonRpc
    // duplex so the test asserts the actual client-facing contract, not just internal state.
    private sealed class ClientChannel : IAsyncDisposable
    {
        private readonly JsonRpc workerRpc;
        private readonly JsonRpc clientRpc;
        private readonly Observer observer = new();

        public ClientChannel(WorkerRpcService worker)
        {
            var clientToWorker = new Pipe();
            var workerToClient = new Pipe();

            workerRpc = new JsonRpc(new HeaderDelimitedMessageHandler(
                workerToClient.Writer, clientToWorker.Reader, new SystemTextJsonFormatter()));
            clientRpc = new JsonRpc(new HeaderDelimitedMessageHandler(
                clientToWorker.Writer, workerToClient.Reader, new SystemTextJsonFormatter()));
            clientRpc.AddLocalRpcTarget(observer);

            worker.AttachClient(workerRpc);
            workerRpc.StartListening();
            clientRpc.StartListening();
        }

        public Task<WorkerStatus> TurnIdSeen => observer.TurnIdSeen;

        public Task<WorkerStatus> EffectiveStateSeen => observer.EffectiveStateSeen;

        public ValueTask DisposeAsync()
        {
            workerRpc.Dispose();
            clientRpc.Dispose();
            return ValueTask.CompletedTask;
        }

        private sealed class Observer
        {
            private readonly TaskCompletionSource<WorkerStatus> turnIdSeen =
                new(TaskCreationOptions.RunContinuationsAsynchronously);
            private readonly TaskCompletionSource<WorkerStatus> effectiveStateSeen =
                new(TaskCreationOptions.RunContinuationsAsynchronously);

            public Task<WorkerStatus> TurnIdSeen => turnIdSeen.Task;

            public Task<WorkerStatus> EffectiveStateSeen => effectiveStateSeen.Task;

            [JsonRpcMethod("observer/stateChanged", UseSingleObjectParameterDeserialization = true)]
            public void OnStateChanged(StateChangedArgs args)
            {
                if (args.Notification?.Value?.TurnId is not null)
                {
                    turnIdSeen.TrySetResult(args.Notification.Value);
                }

                if (args.Notification?.Value?.EffectiveApprovalState is not null)
                {
                    effectiveStateSeen.TrySetResult(args.Notification.Value);
                }
            }
        }

        private sealed class StateChangedArgs
        {
            [JsonPropertyName("notification")]
            public WorkerNotification<WorkerStatus>? Notification { get; set; }
        }
    }

    private sealed class ContractEcho
    {
        public int CallCount { get; private set; }

        [JsonRpcMethod("test/echoStartTurn", UseSingleObjectParameterDeserialization = true)]
        public StartTurnRequest EchoStartTurn(StartTurnRequest request)
        {
            CallCount++;
            return request;
        }
    }

    private sealed class FakeProcessHost : ICodexProcessHost
    {
        private readonly IJsonRpcConnection? connection;
        private EventHandler<string>? standardErrorReceived;

        public FakeProcessHost(IJsonRpcConnection? connection = null)
        {
            this.connection = connection;
        }

        public event EventHandler<string>? StandardErrorReceived
        {
            add => standardErrorReceived += value;
            remove => standardErrorReceived -= value;
        }

        public event EventHandler<int>? Exited { add { } remove { } }

        public int? ProcessId => 4242;

        public IJsonRpcConnection? Connection => connection;

        public int RemoteStarts { get; private set; }

        public int Stops { get; private set; }

        public Action? OnStop { get; init; }

        public void EmitStandardError(string text) => standardErrorReceived?.Invoke(this, text);

        public Func<RemoteConnectionRequest, CancellationToken, Task>? OnStartRemote { get; set; }

        public RemoteConnectionRequest? LastRemoteRequest { get; private set; }

        public Func<CancellationToken, Task>? OnStartLocal { get; set; }

        public Task StartAsync(string codexPath, string workingDirectory, CancellationToken cancellationToken)
            => OnStartLocal?.Invoke(cancellationToken) ?? Task.CompletedTask;

        public Task StartRemoteAsync(RemoteConnectionRequest request, CancellationToken cancellationToken)
        {
            RemoteStarts++;
            LastRemoteRequest = request;
            return OnStartRemote?.Invoke(request, cancellationToken) ?? Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            Stops++;
            OnStop?.Invoke();
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class StubConnection : IJsonRpcConnection, IInboundActivitySource
    {
        private long activity;

        public event Func<JsonRpcMessage, CancellationToken, Task>? NotificationReceived;

        public event Func<JsonRpcMessage, CancellationToken, Task<JsonElement>>? RequestReceived;

        public event EventHandler<Exception?>? Closed;

        public Func<string, JsonElement> Handler { get; set; } = _ => JsonSerializer.SerializeToElement(new { });

        public JsonElement GatewayOAuthReadResponse { get; set; } = JsonSerializer.SerializeToElement(new
        {
            providerId = "test-provider",
            providerName = "Test provider",
            required = false,
            status = (string?)null,
        });

        // When set, replaces Handler and may hang until the per-request timeout elapses.
        public Func<string, TimeSpan, CancellationToken, Task<JsonElement>>? AsyncHandler { get; set; }

        public event EventHandler? InboundActivity;

        public long InboundActivitySequence => Interlocked.Read(ref activity);

        public List<string> Methods { get; } = [];

        public void RecordInboundActivity()
        {
            Interlocked.Increment(ref activity);
            InboundActivity?.Invoke(this, EventArgs.Empty);
        }

        public void EmitClosed(Exception? exception = null) => Closed?.Invoke(this, exception);

        public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public async Task<JsonElement> SendRequestAsync(string method, object? parameters, TimeSpan timeout, CancellationToken cancellationToken)
        {
            lock (Methods)
            {
                Methods.Add(method);
            }

            JsonElement result = method == "account/gatewayOAuth/read"
                ? GatewayOAuthReadResponse.Clone()
                : AsyncHandler is null
                    ? Handler(method)
                    : await AsyncHandler(method, timeout, cancellationToken);

            if (method == "model/list" && !result.TryGetProperty("data", out _))
            {
                result = JsonSerializer.SerializeToElement(new
                {
                    data = new[] { new { model = "gpt-5-codex", isDefault = true } },
                    nextCursor = (string?)null,
                });
            }
            if (method == "thread/read" && !result.TryGetProperty("thread", out _))
            {
                JsonElement request = JsonSerializer.SerializeToElement(parameters);
                result = JsonSerializer.SerializeToElement(new
                {
                    thread = new { id = request.GetProperty("threadId").GetString(), model = "gpt-5-codex" },
                });
            }

            // A delivered response is inbound activity, as in the real transports.
            RecordInboundActivity();
            return result;
        }

        public Task SendNotificationAsync(string method, object? parameters, CancellationToken cancellationToken)
            => Task.CompletedTask;

        public Task EmitNotificationAsync(string method, object parameters)
            => NotificationReceived?.Invoke(
                new JsonRpcMessage
                {
                    Method = method,
                    Params = JsonSerializer.SerializeToElement(parameters),
                },
                CancellationToken.None) ?? Task.CompletedTask;

        public Task<JsonElement> EmitRequestAsync(string id, string method, object parameters)
            => RequestReceived?.Invoke(
                new JsonRpcMessage
                {
                    Id = JsonSerializer.SerializeToElement(id),
                    Method = method,
                    Params = JsonSerializer.SerializeToElement(parameters),
                },
                CancellationToken.None)
                ?? Task.FromResult(JsonSerializer.SerializeToElement(new { }));

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
