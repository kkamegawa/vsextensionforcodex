using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using Codex.AppServer.Protocol;
using Codex.VisualStudio.Contracts;
using Codex.VisualStudio.Worker;

namespace Codex.VisualStudio.Core.Tests;

[TestClass]
public sealed class FakeAppServerIntegrationTests
{
    private static readonly JsonSerializerOptions WireJsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    [TestMethod]
    [DataRow("Current01690", "codex-cli/0.169.0")]
    [DataRow("Current01700", "codex-cli/0.170.0")]
    [DataRow("Current01710", "codex-cli/0.171.0")]
    public async Task FakeReportsSupportedVersionScenariosInUserAgent(string scenario, string expectedUserAgent)
    {
        await using FakeAppServerProcess server = await FakeAppServerProcess.StartAsync(scenario);
        await server.SendAsync(new { id = 1, method = "initialize", @params = new { } });
        using JsonDocument initialized = await server.ReadAsync();
        Assert.AreEqual(expectedUserAgent, initialized.RootElement.GetProperty("result").GetProperty("userAgent").GetString());
    }

    [TestMethod]
    [DataRow("Current01690", "config/value/write")]
    [DataRow("Current01700", "config/value/write")]
    [DataRow("Current01710", "modelProvider/default/set")]
    public async Task ModelProviderRequestsAreScopedAndDefaultMutationIsVersionGated(string scenario, string expectedMutation)
    {
        await using FakeAppServerProcess server = await FakeAppServerProcess.StartAsync(scenario);
        await using JsonLineRpcConnection connection = await server.ConnectAsync();
        await using var service = new CodexSessionService(new ApprovalPolicyEngine(new PathAccessPolicy()), new SecretRedactor());
        await service.InitializeAsync(connection, new WorkerOptions
        {
            ExperimentalApi = true,
            WorkingDirectory = Environment.CurrentDirectory,
            ExtensionVersion = "provider-integration-test",
        }, CancellationToken.None);

        ListModelProvidersResult catalog = await service.ListModelProvidersAsync(CancellationToken.None);
        Assert.AreEqual(2, catalog.Providers.Count);
        Assert.AreEqual("fake", catalog.DefaultProviderId);
        ListModelsResult models = await service.ListModelsAsync("alternate", CancellationToken.None);
        Assert.AreEqual(1, models.Models.Count);
        Assert.AreEqual("alternate-only-model", models.Models[0].Id, "The alternate provider must return its own model catalog.");
        ThreadSummary thread = await service.StartThreadAsync("alternate", CancellationToken.None);
        Assert.AreEqual("alternate", thread.ModelProviderId);
        ListModelProvidersResult updated = await service.SetDefaultModelProviderAsync("alternate", CancellationToken.None);
        Assert.AreEqual("alternate", updated.DefaultProviderId);

        JsonElement state = await connection.SendRequestAsync("fake/provider/state", new { }, TimeSpan.FromSeconds(5), CancellationToken.None);
        JsonElement requests = state.GetProperty("requests");
        JsonElement modelList = requests.EnumerateArray().First(item => item.GetProperty("method").GetString() == "model/list").GetProperty("parameters");
        Assert.AreEqual("alternate", modelList.GetProperty("modelProvider").GetString());
        JsonElement threadStart = requests.EnumerateArray().First(item => item.GetProperty("method").GetString() == "thread/start").GetProperty("parameters");
        Assert.AreEqual("alternate", threadStart.GetProperty("modelProvider").GetString());
        JsonElement mutations = requests.EnumerateArray()
            .Where(item => item.GetProperty("method").GetString() is "config/value/write" or "modelProvider/default/set")
            .ToArray()
            .Single();
        Assert.AreEqual(expectedMutation, mutations.GetProperty("method").GetString());
        JsonElement mutationParameters = mutations.GetProperty("parameters");
        if (expectedMutation == "config/value/write")
        {
            Assert.AreEqual("model_provider", mutationParameters.GetProperty("keyPath").GetString());
            Assert.AreEqual("alternate", mutationParameters.GetProperty("value").GetString());
            Assert.AreEqual("replace", mutationParameters.GetProperty("mergeStrategy").GetString());
        }
        else
        {
            Assert.AreEqual("alternate", mutationParameters.GetProperty("providerId").GetString());
        }

        string incompatibleMutation = expectedMutation == "config/value/write"
            ? "modelProvider/default/set"
            : "config/value/write";
        object incompatibleParameters = incompatibleMutation == "config/value/write"
            ? new { keyPath = "model_provider", value = "fake", mergeStrategy = "replace" }
            : new { providerId = "fake" };
        JsonRpcRemoteException incompatible = await Assert.ThrowsExactlyAsync<JsonRpcRemoteException>(
            () => connection.SendRequestAsync(
                incompatibleMutation,
                incompatibleParameters,
                TimeSpan.FromSeconds(5),
                CancellationToken.None));
        Assert.AreEqual("The model provider default method is unavailable for this CLI version.", incompatible.Message);

        if (expectedMutation == "config/value/write")
        {
            JsonRpcRemoteException invalidKeyPath = await Assert.ThrowsExactlyAsync<JsonRpcRemoteException>(
                () => connection.SendRequestAsync(
                    "config/value/write",
                    new { keyPath = "other", value = "fake", mergeStrategy = "replace" },
                    TimeSpan.FromSeconds(5),
                    CancellationToken.None));
            Assert.AreEqual("The fake server only accepts the model_provider configuration key.", invalidKeyPath.Message);

            JsonRpcRemoteException invalidMerge = await Assert.ThrowsExactlyAsync<JsonRpcRemoteException>(
                () => connection.SendRequestAsync(
                    "config/value/write",
                    new { keyPath = "model_provider", value = "fake", mergeStrategy = "merge" },
                    TimeSpan.FromSeconds(5),
                    CancellationToken.None));
            Assert.AreEqual("The model provider default mutation is invalid.", invalidMerge.Message);
        }

        JsonElement gatewayStatusWithoutParameters = await connection.SendRequestAsync(
            "account/gatewayOAuth/read",
            null,
            TimeSpan.FromSeconds(5),
            CancellationToken.None);
        Assert.AreEqual("alternate", gatewayStatusWithoutParameters.GetProperty("providerId").GetString());
        Assert.AreEqual("Alternate provider", gatewayStatusWithoutParameters.GetProperty("providerName").GetString());
        JsonElement stateAfterRejectedWrites = await connection.SendRequestAsync(
            "fake/provider/state",
            new { },
            TimeSpan.FromSeconds(5),
            CancellationToken.None);
        Assert.AreEqual("alternate", stateAfterRejectedWrites.GetProperty("defaultProviderId").GetString());
    }

    [TestMethod]
    public async Task FakeSeparateReviewAndUsageMatchTheStableSurface()
    {
        await using FakeAppServerProcess server = await FakeAppServerProcess.StartAsync("Current01690");
        await server.SendAsync(new { id = 1, method = "initialize", @params = new { } });
        (await server.ReadAsync()).Dispose();

        await server.SendAsync(new { id = 5, method = "model/list" });
        using JsonDocument models = await ReadResponseAsync(server, 5);
        Assert.IsTrue(models.RootElement.GetProperty("result").GetProperty("data").GetArrayLength() > 0);

        await server.SendAsync(new { id = 2, method = "thread/start", @params = new { sandbox = "read-only" } });
        using JsonDocument started = await server.ReadAsync();
        JsonElement thread = started.RootElement.GetProperty("result");
        Assert.AreEqual(JsonValueKind.Null, thread.GetProperty("activePermissionProfile").ValueKind);
        string threadId = thread.GetProperty("thread").GetProperty("id").GetString()!;

        await server.SendAsync(new { id = 3, method = "review/start", @params = new { threadId, target = new { type = "uncommittedChanges" }, delivery = "inline" } });
        using JsonDocument review = await ReadResponseAsync(server, 3);
        Assert.AreEqual(threadId, review.RootElement.GetProperty("result").GetProperty("reviewThreadId").GetString());

        await server.SendAsync(new { id = 4, method = "account/usage/read", @params = new { threadId } });
        using JsonDocument usage = await ReadResponseAsync(server, 4);
        JsonElement amounts = usage.RootElement.GetProperty("result").GetProperty("threadUsage");
        Assert.AreEqual(1_500_000L, amounts.GetProperty("estimatedUsageCreditsMicros").GetInt64());
        Assert.AreEqual(JsonValueKind.Null, amounts.GetProperty("estimatedUsageUsdMicros").ValueKind);
    }

    [TestMethod]
    public async Task FakeProtocolReplayRetainsRolledBackTurnsAndTerminalErrors()
    {
        await using FakeAppServerProcess server = await FakeAppServerProcess.StartAsync("Issue292");
        await using JsonLineRpcConnection connection = await server.ConnectAsync();
        await using var service = new CodexSessionService(new ApprovalPolicyEngine(new PathAccessPolicy()), new SecretRedactor());
        var terminalEvents = new System.Collections.Concurrent.ConcurrentQueue<ConversationEvent>();
        var terminalEventsReceived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        service.ConversationEventReceived += (value, _) =>
        {
            if (value.ErrorReason is not null)
            {
                terminalEvents.Enqueue(value);
                if (terminalEvents.Count == 2)
                {
                    terminalEventsReceived.TrySetResult();
                }
            }

            return Task.CompletedTask;
        };
        await service.InitializeAsync(connection, new WorkerOptions
        {
            ExperimentalApi = true,
            WorkingDirectory = Environment.CurrentDirectory,
            ExtensionVersion = "issue-292-integration-test",
        }, CancellationToken.None);

        ThreadSummary started = await service.StartThreadAsync(CancellationToken.None);
        Assert.AreEqual("read-only", started.EffectiveApprovalState!.ActivePermissionProfile);
        ThreadSummary resumed = await service.ResumeThreadAsync(started.Id, CancellationToken.None);
        Assert.AreEqual("read-only", resumed.EffectiveApprovalState!.ActivePermissionProfile);

        _ = await service.StartTurnAsync(new StartTurnRequest
        {
            ThreadId = started.Id,
            Text = "exercise terminal errors",
            Model = "gpt-5.1-codex-max",
        }, CancellationToken.None);
        await terminalEventsReceived.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.IsTrue(terminalEvents.Any(value => value.ErrorReason == "sessionBudgetExceeded" && value.Text is not null));
        Assert.IsTrue(terminalEvents.Any(value => value.ErrorReason == "inferenceHookDenied" && value.Text is not null));
        Assert.AreEqual("read-only", service.EffectiveApprovalState!.ActivePermissionProfile);

        ThreadTurnsPage history = await service.ListThreadTurnsAsync(started.Id, cursor: null, limit: 10, CancellationToken.None);
        Assert.AreEqual("rolledBack", history.Turns[0].Status);
        Assert.AreEqual("sessionBudgetExceeded", history.Turns[1].ErrorReason);
        Assert.AreEqual("inferenceHookDenied", history.Turns[2].ErrorReason);

        JsonElement state = await connection.SendRequestAsync("fake/goal/state", new { }, TimeSpan.FromSeconds(10), CancellationToken.None);
        string[] methods = state.GetProperty("methods").EnumerateArray().Select(value => value.GetString()!).ToArray();
        Assert.IsFalse(methods.Contains("thread/rollback", StringComparer.Ordinal));
        Assert.AreEqual(1, methods.Count(method => method == "turn/start"), "Terminal errors are not automatically retried.");
    }

    // Notifications may precede a response, so messages are read until the id matches.
    private static async Task<JsonDocument> ReadResponseAsync(FakeAppServerProcess server, int id)
    {
        while (true)
        {
            JsonDocument message = await server.ReadAsync();
            if (message.RootElement.TryGetProperty("id", out JsonElement value)
                && value.ValueKind == JsonValueKind.Number
                && value.GetInt32() == id)
            {
                return message;
            }

            message.Dispose();
        }
    }

    [TestMethod]
    [DataRow("Current01690", true)]
    [DataRow("Current01690", false)]
    public async Task FakeExperimentalMemoryHonorsNegotiatedCapability(string scenario, bool experimental)
    {
        await using FakeAppServerProcess server = await FakeAppServerProcess.StartAsync(scenario);
        await InitializeFakeAsync(server, 1, experimental);

        await server.SendAsync(new { id = 2, method = "thread/start", @params = new { } });
        using JsonDocument started = await ReadResponseAsync(server, 2);
        string threadId = started.RootElement.GetProperty("result").GetProperty("thread").GetProperty("id").GetString()!;

        await server.SendAsync(new { id = 3, method = "thread/memory/read", @params = new { threadId } });
        using JsonDocument memory = await ReadResponseAsync(server, 3);
        if (experimental)
        {
            Assert.AreEqual(JsonValueKind.Null, memory.RootElement.GetProperty("result").GetProperty("summary").ValueKind);
        }
        else
        {
            Assert.AreEqual(-32601, memory.RootElement.GetProperty("error").GetProperty("code").GetInt32());
        }
    }

    [TestMethod]
    public async Task FakeLinkedForksStayOutOfOrdinaryHistoryAndUseAttachedThreadFilter()
    {
        await using FakeAppServerProcess server = await FakeAppServerProcess.StartAsync("Current01690");
        await InitializeFakeAsync(server, 1, experimental: true);

        await server.SendAsync(new { id = 2, method = "thread/start", @params = new { } });
        using JsonDocument started = await ReadResponseAsync(server, 2);
        string parentId = started.RootElement.GetProperty("result").GetProperty("thread").GetProperty("id").GetString()!;

        await server.SendAsync(new
        {
            id = 3,
            method = "thread/fork",
            @params = new { threadId = parentId, attachToSource = true, excludeTurns = true },
        });
        using JsonDocument forked = await ReadResponseAsync(server, 3);
        string childId = forked.RootElement.GetProperty("result").GetProperty("thread").GetProperty("id").GetString()!;

        await server.SendAsync(new { id = 4, method = "thread/list", @params = new { limit = 25 } });
        using JsonDocument ordinary = await ReadResponseAsync(server, 4);
        JsonElement ordinaryThreads = ordinary.RootElement.GetProperty("result").GetProperty("data");
        Assert.IsFalse(ContainsThread(ordinaryThreads, childId), ordinaryThreads.ToString());
        Assert.IsTrue(ContainsThread(ordinaryThreads, parentId), ordinaryThreads.ToString());

        await server.SendAsync(new
        {
            id = 5,
            method = "thread/list",
            @params = new { attachedToThreadId = parentId, cursor = (string?)null, limit = 25 },
        });
        using JsonDocument children = await ReadResponseAsync(server, 5);
        JsonElement childThreads = children.RootElement.GetProperty("result").GetProperty("data");
        Assert.IsTrue(ContainsThread(childThreads, childId), childThreads.ToString());
        Assert.IsFalse(ContainsThread(childThreads, parentId), childThreads.ToString());
    }

    [TestMethod]
    public async Task FakeExperimentalRequestsRequireNegotiationButOrdinaryForkRemainsStable()
    {
        await using FakeAppServerProcess server = await FakeAppServerProcess.StartAsync("Current01690");
        await InitializeFakeAsync(server, 1, experimental: false);

        await server.SendAsync(new { id = 2, method = "thread/start", @params = new { } });
        using JsonDocument started = await ReadResponseAsync(server, 2);
        string threadId = started.RootElement.GetProperty("result").GetProperty("thread").GetProperty("id").GetString()!;

        await server.SendAsync(new { id = 3, method = "thread/fork", @params = new { threadId } });
        using JsonDocument ordinaryFork = await ReadResponseAsync(server, 3);
        Assert.IsTrue(ordinaryFork.RootElement.TryGetProperty("result", out _));

        await server.SendAsync(new
        {
            id = 4,
            method = "thread/fork",
            @params = new { threadId, attachToSource = true, excludeTurns = true },
        });
        using JsonDocument linkedFork = await ReadResponseAsync(server, 4);
        Assert.AreEqual(-32601, linkedFork.RootElement.GetProperty("error").GetProperty("code").GetInt32());

        await server.SendAsync(new { id = 5, method = "accountSession/list", @params = new { refreshWorkspaceMetadata = true } });
        using JsonDocument sessions = await ReadResponseAsync(server, 5);
        Assert.AreEqual(-32601, sessions.RootElement.GetProperty("error").GetProperty("code").GetInt32());
    }

    [TestMethod]
    public async Task FakeWorkspaceSwitchUsesSameLoginAndDistinguishesWorkspaceIds()
    {
        await using FakeAppServerProcess server = await FakeAppServerProcess.StartAsync("Current01690,Issue243WorkspaceSwitch");
        await InitializeFakeAsync(server, 1, experimental: true);
        await server.SendAsync(new { id = 2, method = "account/read", @params = new { } });
        using JsonDocument account = await ReadResponseAsync(server, 2);
        Assert.AreEqual("plus", account.RootElement.GetProperty("result").GetProperty("account").GetProperty("planType").GetString());

        await server.SendAsync(new { id = 3, method = "accountSession/list", @params = new { refreshWorkspaceMetadata = true } });
        using JsonDocument sessions = await ReadResponseAsync(server, 3);
        JsonElement session = sessions.RootElement.GetProperty("result").GetProperty("sessions")[0];
        Assert.AreEqual("fake-account-session", session.GetProperty("sessionId").GetString());
        Assert.AreEqual("same-user@example.test", session.GetProperty("email").GetString());
        JsonElement workspaces = session.GetProperty("workspaces");
        Assert.AreEqual(2, workspaces.GetArrayLength());
        string personal = workspaces[0].GetProperty("accountId").GetString()!;
        string team = workspaces[1].GetProperty("accountId").GetString()!;
        Assert.AreNotEqual(personal, team);

        await server.SendAsync(new
        {
            id = 4,
            method = "accountSession/switch",
            @params = new { sessionId = "fake-account-session", accountId = team, allowBrowserLogin = true },
        });
        using JsonDocument switched = await ReadResponseAsync(server, 4);
        JsonElement switchedSession = switched.RootElement.GetProperty("result").GetProperty("sessions")[0];
        Assert.AreEqual("same-user@example.test", switchedSession.GetProperty("email").GetString());
        Assert.AreEqual(team, switchedSession.GetProperty("selectedWorkspaceAccountId").GetString());
    }

    [TestMethod]
    public async Task FakeWorkspaceSwitchCoversSsoCancellationAndUnknownResultLookup()
    {
        await using FakeAppServerProcess sso = await FakeAppServerProcess.StartAsync("Current01690,Issue243SsoSwitch");
        await InitializeFakeAsync(sso, 1, experimental: true);
        await sso.SendAsync(new { id = 2, method = "accountSession/switch", @params = new { sessionId = "fake-account-session", accountId = "fake-team-workspace", allowBrowserLogin = true } });
        using JsonDocument login = await ReadResponseAsync(sso, 2);
        JsonElement loginResult = login.RootElement.GetProperty("result");
        Assert.AreEqual("fake-workspace-login", loginResult.GetProperty("login_id").GetString());
        Assert.IsTrue(Uri.TryCreate(loginResult.GetProperty("auth_url").GetString(), UriKind.Absolute, out Uri? authUrl));
        Assert.AreEqual(Uri.UriSchemeHttps, authUrl!.Scheme);
        await sso.SendAsync(new { id = 3, method = "account/login/cancel", @params = new { loginId = "fake-workspace-login" } });
        using JsonDocument canceled = await ReadResponseAsync(sso, 3);
        Assert.AreEqual("canceled", canceled.RootElement.GetProperty("result").GetProperty("status").GetString());

        await using FakeAppServerProcess unknown = await FakeAppServerProcess.StartAsync("Current01690,Issue243UnknownSwitch");
        await InitializeFakeAsync(unknown, 1, experimental: true);
        await unknown.SendAsync(new { id = 2, method = "accountSession/switch", @params = new { sessionId = "fake-account-session", accountId = "fake-team-workspace", allowBrowserLogin = true } });
        await unknown.SendAsync(new { id = 3, method = "accountSession/list", @params = new { refreshWorkspaceMetadata = true } });
        using JsonDocument confirmed = await ReadResponseAsync(unknown, 3);
        JsonElement active = confirmed.RootElement.GetProperty("result").GetProperty("sessions")[0];
        Assert.AreEqual("fake-team-workspace", active.GetProperty("selectedWorkspaceAccountId").GetString());
        await unknown.SendAsync(new { id = 4, method = "fake/experimental/state", @params = new { } });
        using JsonDocument state = await ReadResponseAsync(unknown, 4);
        Assert.AreEqual(1, state.RootElement.GetProperty("result").GetProperty("workspaceSwitchCount").GetInt32());
    }

    private static async Task InitializeFakeAsync(FakeAppServerProcess server, int id, bool experimental)
    {
        await server.SendAsync(new
        {
            id,
            method = "initialize",
            @params = new { capabilities = new { experimentalApi = experimental } },
        });
        using JsonDocument initialized = await ReadResponseAsync(server, id);
        Assert.IsTrue(initialized.RootElement.TryGetProperty("result", out _), initialized.RootElement.ToString());
    }

    private static bool ContainsThread(JsonElement threads, string threadId)
        => threads.EnumerateArray().Any(thread =>
            string.Equals(thread.GetProperty("id").GetString(), threadId, StringComparison.Ordinal));

    [TestMethod]
    [DataRow("Issue244NativeUsd", 4_250_000L, false, false)]
    [DataRow("Issue244ZeroUsd", 0L, false, false)]
    [DataRow("Issue244NullUsd", 0L, false, true)]
    [DataRow("Issue244MissingUsageValues", 0L, true, false)]
    public async Task FakeThreadUsagePreservesNativeZeroAndUnknown(string scenario, long expectedNativeUsdMicros, bool expectMissing, bool expectNull)
    {
        await using FakeAppServerProcess server = await FakeAppServerProcess.StartAsync($"Current01690,{scenario}");
        await InitializeFakeAsync(server, 1, experimental: false);
        await server.SendAsync(new { id = 2, method = "account/usage/read", @params = new { threadId = "fake-thread-usage" } });
        using JsonDocument usage = await ReadResponseAsync(server, 2);
        JsonElement amounts = usage.RootElement.GetProperty("result").GetProperty("threadUsage");
        if (expectMissing)
        {
            Assert.IsFalse(amounts.TryGetProperty("nativeUsageUsdMicros", out _), amounts.ToString());
            Assert.AreEqual(JsonValueKind.Null, amounts.GetProperty("estimatedUsageUsdMicros").ValueKind);
        }
        else
        {
            JsonElement nativeUsage = amounts.GetProperty("nativeUsageUsdMicros");
            if (expectNull)
            {
                Assert.AreEqual(JsonValueKind.Null, nativeUsage.ValueKind);
            }
            else
            {
                Assert.AreEqual(expectedNativeUsdMicros, nativeUsage.GetInt64());
            }
        }

        Assert.AreEqual(0L, amounts.GetProperty("estimatedUsageCreditsMicros").GetInt64());
    }

    [TestMethod]
    public async Task FakeProviderOnlyModelListOffersProviderModelsWithoutOpeningAuthWizards()
    {
        await using FakeAppServerProcess server = await FakeAppServerProcess.StartAsync("Issue244ProviderOnly");
        await InitializeFakeAsync(server, 1, experimental: false);

        await server.SendAsync(new { id = 2, method = "model/list", @params = new { } });
        using JsonDocument models = await ReadResponseAsync(server, 2);
        JsonElement catalog = models.RootElement.GetProperty("result").GetProperty("data");
        Assert.AreEqual(2, catalog.GetArrayLength());
        Assert.AreEqual("vertex-ai-gemini", catalog[0].GetProperty("model").GetString());
        Assert.AreEqual("bedrock-claude", catalog[1].GetProperty("model").GetString());

        await server.SendAsync(new { id = 3, method = "fake/experimental/state", @params = new { } });
        using JsonDocument state = await ReadResponseAsync(server, 3);
        string[] methods = state.RootElement.GetProperty("result").GetProperty("methods")
            .EnumerateArray().Select(method => method.GetString()!).ToArray();
        Assert.IsFalse(methods.Contains("account/gcp/setup", StringComparer.Ordinal));
        Assert.IsFalse(methods.Contains("account/login/start", StringComparer.Ordinal));
        Assert.IsFalse(methods.Contains("mcpServer/oauth/login", StringComparer.Ordinal));
    }

    [TestMethod]
    public async Task FakeReadStateContractSupportsUpdatesConflictsAndMinimumVersion()
    {
        await using FakeAppServerProcess current = await FakeAppServerProcess.StartAsync(string.Empty);
        await current.SendAsync(new
        {
            id = 1,
            method = "initialize",
            @params = new { capabilities = new { experimentalApi = true } },
        });
        using JsonDocument initialized = await current.ReadAsync();
        Assert.AreEqual("codex-cli/0.169.0", initialized.RootElement.GetProperty("result").GetProperty("userAgent").GetString());

        await current.SendAsync(new { id = 2, method = "thread/start", @params = new { } });
        using JsonDocument started = await current.ReadAsync();
        string threadId = started.RootElement.GetProperty("result").GetProperty("thread").GetProperty("id").GetString()!;
        using JsonDocument idleStatus = await current.ReadAsync();
        Assert.AreEqual("thread/status/changed", idleStatus.RootElement.GetProperty("method").GetString());

        await current.SendAsync(new { id = 3, method = "thread/list", @params = new { } });
        using JsonDocument listed = await current.ReadAsync();
        Assert.IsTrue(listed.RootElement.TryGetProperty("result", out JsonElement listResult), listed.RootElement.ToString());
        Assert.IsTrue(listResult.TryGetProperty("readStates", out JsonElement readStates), listed.RootElement.ToString());
        JsonElement initialState = readStates.GetProperty(threadId);
        Assert.AreEqual("threadStart", initialState.GetProperty("firstUnread").GetProperty("type").GetString());
        string expectedRevision = initialState.GetProperty("revision").GetString()!;

        await current.SendAsync(new
        {
            id = 4,
            method = "thread/readState/update",
            @params = new
            {
                threadId,
                expectedRevision,
                operation = new { type = "read" },
            },
        });
        using JsonDocument updated = await current.ReadAsync();
        using JsonDocument changed = await current.ReadAsync();
        JsonElement updatedState = updated.RootElement.GetProperty("result").GetProperty("readState");
        Assert.AreEqual(4, updated.RootElement.GetProperty("id").GetInt32());
        Assert.AreEqual(JsonValueKind.Null, updatedState.GetProperty("firstUnread").ValueKind);
        Assert.AreEqual("thread/readState/changed", changed.RootElement.GetProperty("method").GetString());
        Assert.AreEqual(threadId, changed.RootElement.GetProperty("params").GetProperty("threadId").GetString());

        await using FakeAppServerProcess conflict = await FakeAppServerProcess.StartAsync("ReadStateConflict");
        await conflict.SendAsync(new
        {
            id = 1,
            method = "initialize",
            @params = new { capabilities = new { experimentalApi = true } },
        });
        using JsonDocument conflictInitialized = await conflict.ReadAsync();
        await conflict.SendAsync(new { id = 2, method = "thread/start", @params = new { } });
        using JsonDocument conflictStarted = await conflict.ReadAsync();
        string conflictThreadId = conflictStarted.RootElement.GetProperty("result").GetProperty("thread").GetProperty("id").GetString()!;
        using JsonDocument conflictIdleStatus = await conflict.ReadAsync();
        Assert.AreEqual("thread/status/changed", conflictIdleStatus.RootElement.GetProperty("method").GetString());
        await conflict.SendAsync(new { id = 3, method = "thread/readState/update", @params = new { threadId = conflictThreadId, expectedRevision = "stale", operation = new { type = "read" } } });
        using JsonDocument conflicted = await conflict.ReadAsync();
        JsonElement error = conflicted.RootElement.GetProperty("error");
        Assert.AreEqual(-32600, error.GetProperty("code").GetInt32());
        Assert.AreEqual("readStateConflict", error.GetProperty("data").GetProperty("reason").GetString());
        Assert.IsTrue(error.GetProperty("data").GetProperty("readState").GetProperty("revision").GetString()!.Length > 0);
    }

    [TestMethod]
    public async Task FakeServerAt168IsRejectedBeforeInitialized()
    {
        await using FakeAppServerProcess server = await FakeAppServerProcess.StartAsync("Unsupported01680");
        await using var connection = new RecordingJsonRpcConnection(await server.ConnectAsync());
        await using var service = new CodexSessionService(new ApprovalPolicyEngine(new PathAccessPolicy()), new SecretRedactor());

        UnsupportedCodexVersionException error = await Assert.ThrowsExactlyAsync<UnsupportedCodexVersionException>(
            () => service.InitializeAsync(connection, new WorkerOptions
            {
                WorkingDirectory = Environment.CurrentDirectory,
                ExtensionVersion = "minimum-version-integration-test",
            }, CancellationToken.None));

        StringAssert.Contains(error.Message, "version 0.168.0");
        StringAssert.Contains(error.Message, SupportedCodexVersions.Minimum);
        Assert.IsNull(service.CodexVersion);
        Assert.AreEqual(CodexFeatureTier.Unsupported, service.FeatureTier);
        CollectionAssert.AreEqual(new[] { "initialize" }, connection.Requests.ToArray());
        CollectionAssert.DoesNotContain(connection.Notifications, "initialized");
    }

    [TestMethod]
    public async Task FakeThreadListExposesReadStatesOnTheMinimumSupportedVersion()
    {
        await using FakeAppServerProcess server = await FakeAppServerProcess.StartAsync("Current01690");
        await server.SendAsync(new
        {
            id = 1,
            method = "initialize",
            @params = new { capabilities = new { experimentalApi = true } },
        });
        using JsonDocument initialized = await server.ReadAsync();
        Assert.AreEqual("codex-cli/0.169.0", initialized.RootElement.GetProperty("result").GetProperty("userAgent").GetString());
        await server.SendAsync(new { id = 2, method = "thread/list", @params = new { } });
        using JsonDocument listed = await server.ReadAsync();
        Assert.IsTrue(listed.RootElement.TryGetProperty("result", out JsonElement listResult), listed.RootElement.ToString());
        Assert.IsTrue(listResult.TryGetProperty("readStates", out _), listed.RootElement.ToString());
    }

    [TestMethod]
    [DataRow("Issue174GoalStop", true)]
    [DataRow("Issue174GoalGap", false)]
    public async Task GoalStopPausesBeforeInterruptingAnAutonomousTurnAndPreventsContinuation(string scenario, bool expectTurn)
    {
        await using FakeAppServerProcess server = await FakeAppServerProcess.StartAsync(scenario);
        await using JsonLineRpcConnection connection = await server.ConnectAsync();
        await using var service = new CodexSessionService(new ApprovalPolicyEngine(new PathAccessPolicy()), new SecretRedactor());
        var started = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var completed = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.ConversationEventReceived += (value, _) =>
        {
            if (value.Kind == ConversationEventKind.TurnStarted && value.TurnId is { } startedTurnId)
            {
                started.TrySetResult(startedTurnId);
            }
            if (value.Kind == ConversationEventKind.TurnCompleted && value.TurnId is { } completedTurnId)
            {
                completed.TrySetResult(completedTurnId);
            }
            return Task.CompletedTask;
        };
        await service.InitializeAsync(connection, new WorkerOptions
        {
            WorkingDirectory = Environment.CurrentDirectory,
            ExtensionVersion = "goal-stop-integration-test",
        }, CancellationToken.None);
        ThreadSummary thread = await service.StartThreadAsync(CancellationToken.None);
        ThreadGoalResult goal = await service.SetThreadGoalAsync(new SetThreadGoalRequest
        {
            ThreadId = thread.Id,
            Objective = "Keep working until explicitly stopped.",
            TokenBudget = 30_000,
            Status = ThreadGoalStatus.Active,
        }, CancellationToken.None);
        string? autonomousTurnId = expectTurn
            ? await started.Task.WaitAsync(TimeSpan.FromSeconds(10))
            : null;

        StopThreadGoalResult stopped = await service.StopThreadGoalAsync(new StopThreadGoalRequest { ThreadId = thread.Id }, CancellationToken.None);

        Assert.AreEqual(GoalStopStepOutcome.Succeeded, stopped.PauseOutcome);
        Assert.AreEqual(expectTurn ? GoalStopStepOutcome.Succeeded : GoalStopStepOutcome.NotRequired, stopped.InterruptOutcome);
        Assert.AreEqual(autonomousTurnId, stopped.TurnId);
        Assert.AreEqual(ThreadGoalStatus.Paused, stopped.Goal?.Status);
        Assert.AreEqual(goal.Goal?.Objective, stopped.Goal?.Objective);
        Assert.AreEqual(goal.Goal?.TokenBudget, stopped.Goal?.TokenBudget);
        Assert.AreEqual(goal.Goal?.TokensUsed, stopped.Goal?.TokensUsed);
        Assert.AreEqual(goal.Goal?.TimeUsedSeconds, stopped.Goal?.TimeUsedSeconds);
        if (expectTurn)
        {
            Assert.AreEqual(autonomousTurnId, await completed.Task.WaitAsync(TimeSpan.FromSeconds(10)));
        }

        JsonElement state = await connection.SendRequestAsync("fake/goal/state", new { }, TimeSpan.FromSeconds(10), CancellationToken.None);
        JsonElement[] mutations = state.GetProperty("requests").EnumerateArray().ToArray();
        Assert.AreEqual(expectTurn ? 3 : 2, mutations.Length);
        Assert.AreEqual("thread/goal/set", mutations[1].GetProperty("method").GetString());
        JsonElement pause = mutations[1].GetProperty("parameters");
        CollectionAssert.AreEquivalent(new[] { "threadId", "status", "origin" }, pause.EnumerateObject().Select(property => property.Name).ToArray());
        Assert.AreEqual("paused", pause.GetProperty("status").GetString());
        Assert.AreEqual("user", pause.GetProperty("origin").GetString());
        if (expectTurn)
        {
            Assert.AreEqual("turn/interrupt", mutations[2].GetProperty("method").GetString());
            Assert.AreEqual(autonomousTurnId, mutations[2].GetProperty("parameters").GetProperty("turnId").GetString());
        }
        Assert.AreEqual(0, state.GetProperty("activeTurns").GetInt32());
        JsonElement continuation = await connection.SendRequestAsync("fake/goal/continue", new { threadId = thread.Id }, TimeSpan.FromSeconds(10), CancellationToken.None);
        Assert.IsFalse(continuation.GetProperty("started").GetBoolean());
        Assert.IsFalse(state.GetProperty("methods").EnumerateArray().Any(method => method.GetString() == "turn/start"));
    }

    [TestMethod]
    public async Task FakeEmitsAuthoritativePlanAfterDeltasAndCanCompleteWithoutDeltas()
    {
        await using FakeAppServerProcess server = await FakeAppServerProcess.StartAsync("Issue155PlanDeltaFinal");
        await server.SendAsync(new { id = 1, method = "initialize", @params = new { } });
        using JsonDocument initialize = await server.ReadAsync();
        string expectedPlatform = OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsMacOS() ? "macos" : "linux";
        Assert.AreEqual(expectedPlatform, initialize.RootElement.GetProperty("result").GetProperty("platformOs").GetString());

        await server.SendAsync(new { id = 2, method = "thread/start", @params = new { } });
        using JsonDocument threadStarted = await server.ReadAsync();
        using JsonDocument idleStatus = await server.ReadAsync();
        string threadId = threadStarted.RootElement.GetProperty("result").GetProperty("thread").GetProperty("id").GetString()!;
        Assert.AreEqual("thread/status/changed", idleStatus.RootElement.GetProperty("method").GetString());
        Assert.AreEqual("idle", idleStatus.RootElement.GetProperty("params").GetProperty("status").GetProperty("type").GetString());

        await server.SendAsync(new { id = 3, method = "turn/start", @params = new { threadId, input = new object[] { new { type = "text", text = "make a plan" } } } });
        using JsonDocument turnStarted = await server.ReadAsync();
        Assert.IsTrue(turnStarted.RootElement.TryGetProperty("result", out _));

        var methods = new List<string>();
        string? finalPlanText = null;
        bool sawLateDelta = false;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (finalPlanText is null || !sawLateDelta)
        {
            using JsonDocument message = await server.ReadAsync(timeout.Token);
            string? method = message.RootElement.TryGetProperty("method", out JsonElement methodElement) ? methodElement.GetString() : null;
            if (method is null)
            {
                continue;
            }

            methods.Add(method);
            if (method == "item/completed" && message.RootElement.GetProperty("params").GetProperty("item").GetProperty("type").GetString() == "plan")
            {
                finalPlanText = message.RootElement.GetProperty("params").GetProperty("item").GetProperty("text").GetString();
            }
            else if (method == "item/plan/delta" && finalPlanText is not null)
            {
                sawLateDelta = true;
            }
        }

        Assert.IsTrue(methods.Contains("item/plan/delta", StringComparer.Ordinal));
        Assert.AreEqual("Authoritative final plan text.", finalPlanText);

        await using FakeAppServerProcess withoutDelta = await FakeAppServerProcess.StartAsync("Issue155PlanWithoutDelta");
        await withoutDelta.SendAsync(new { id = 1, method = "initialize", @params = new { } });
        using JsonDocument initWithoutDelta = await withoutDelta.ReadAsync();
        Assert.IsTrue(initWithoutDelta.RootElement.TryGetProperty("result", out _));
        await withoutDelta.SendAsync(new { id = 2, method = "thread/start", @params = new { } });
        using JsonDocument startedWithoutDelta = await withoutDelta.ReadAsync();
        using JsonDocument statusWithoutDelta = await withoutDelta.ReadAsync();
        Assert.AreEqual("thread/status/changed", statusWithoutDelta.RootElement.GetProperty("method").GetString());
        await withoutDelta.SendAsync(new { id = 3, method = "turn/start", @params = new { threadId = startedWithoutDelta.RootElement.GetProperty("result").GetProperty("thread").GetProperty("id").GetString(), input = new object[] { new { type = "text", text = "plan" } } } });
        using JsonDocument turnWithoutDelta = await withoutDelta.ReadAsync();
        Assert.IsTrue(turnWithoutDelta.RootElement.TryGetProperty("result", out _));
        bool sawPlanCompletion = false;
        while (!sawPlanCompletion)
        {
            using JsonDocument message = await withoutDelta.ReadAsync(timeout.Token);
            if (message.RootElement.TryGetProperty("method", out JsonElement method)
                && method.GetString() == "item/completed"
                && message.RootElement.GetProperty("params").GetProperty("item").GetProperty("type").GetString() == "plan")
            {
                sawPlanCompletion = true;
            }
        }
    }

    [TestMethod]
    public async Task FakeSavedAttachmentCanBeExistingAndAbsentRemovalSucceeds()
    {
        await using FakeAppServerProcess server = await FakeAppServerProcess.StartAsync("Issue155AttachmentExisting");
        await server.SendAsync(new { id = 1, method = "initialize", @params = new { } });
        using JsonDocument initialized = await server.ReadAsync();
        Assert.IsTrue(initialized.RootElement.TryGetProperty("result", out _));
        await server.SendAsync(new { id = 2, method = "thread/start", @params = new { } });
        using JsonDocument threadStarted = await server.ReadAsync();
        using JsonDocument idleStatus = await server.ReadAsync();
        Assert.AreEqual("thread/status/changed", idleStatus.RootElement.GetProperty("method").GetString());
        string threadId = threadStarted.RootElement.GetProperty("result").GetProperty("thread").GetProperty("id").GetString()!;
        string existingServerPath = CreateServerPath("notes.txt");
        string identityKey = SavedAttachmentPayloadRegistry.CreateIdentityKey(ServerPath.Create(existingServerPath), OperatingSystem.IsWindows());

        await server.SendAsync(new { id = 3, method = "thread/attachment/list", @params = new { threadId, limit = 50 } });
        using JsonDocument listed = await server.ReadAsync();
        JsonElement listedData = listed.RootElement.GetProperty("result").GetProperty("data");
        Assert.AreEqual(3, listedData.GetArrayLength());
        Assert.IsTrue(listedData.EnumerateArray().Any(attachment =>
            attachment.GetProperty("attachmentType").GetString() == SavedAttachmentPayloadRegistry.FileAttachmentType
            && attachment.GetProperty("identityKey").GetString() == identityKey
            && attachment.GetProperty("payload").GetProperty("serverPath").GetString() == existingServerPath));
        Assert.IsTrue(listedData.EnumerateArray().Any(attachment => attachment.GetProperty("attachmentType").GetString() == "future.attachment.v9"));
        Assert.IsTrue(listedData.EnumerateArray().Any(attachment =>
            attachment.GetProperty("id").GetString() == "fake-attachment-malformed"
            && attachment.GetProperty("payload").GetProperty("serverPath").GetString() != existingServerPath));

        await server.SendAsync(new
        {
            id = 4,
            method = "thread/attachment/add",
            @params = new
            {
                threadId,
                attachmentType = "relaycodex.file.v1",
                identityKey,
                payload = new { version = 1, serverPath = existingServerPath, mimeType = "text/plain", displayName = "notes.txt" },
            },
        });
        using JsonDocument existing = await server.ReadAsync();
        Assert.AreEqual("existing", existing.RootElement.GetProperty("result").GetProperty("outcome").GetString());

        string absentIdentityKey = SavedAttachmentPayloadRegistry.CreateIdentityKey(ServerPath.Create(CreateServerPath("absent.txt")), OperatingSystem.IsWindows());
        await server.SendAsync(new
        {
            id = 5,
            method = "thread/attachment/remove",
            @params = new { threadId, attachmentType = "relaycodex.file.v1", identityKey = absentIdentityKey },
        });
        using JsonDocument removed = await server.ReadAsync();
        Assert.AreEqual(0, removed.RootElement.GetProperty("result").EnumerateObject().Count());

        string newServerPath = CreateServerPath("new.txt");
        string newIdentityKey = SavedAttachmentPayloadRegistry.CreateIdentityKey(ServerPath.Create(newServerPath), OperatingSystem.IsWindows());
        await server.SendAsync(new
        {
            id = 6,
            method = "thread/attachment/add",
            @params = new
            {
                threadId,
                attachmentType = "relaycodex.file.v1",
                identityKey = newIdentityKey,
                payload = new { version = 1, serverPath = newServerPath, mimeType = "text/plain", displayName = "new.txt" },
            },
        });
        using JsonDocument created = await server.ReadAsync();
        Assert.AreEqual("created", created.RootElement.GetProperty("result").GetProperty("outcome").GetString());

        await server.SendAsync(new
        {
            id = 7,
            method = "thread/attachment/remove",
            @params = new { threadId, attachmentType = "relaycodex.file.v1", identityKey = newIdentityKey },
        });
        using JsonDocument deleted = await server.ReadAsync();
        Assert.AreEqual(0, deleted.RootElement.GetProperty("result").EnumerateObject().Count());

        await server.SendAsync(new { id = 8, method = "thread/attachment/list", @params = new { threadId, limit = 50 } });
        using JsonDocument reconciled = await server.ReadAsync();
        Assert.AreEqual(3, reconciled.RootElement.GetProperty("result").GetProperty("data").GetArrayLength());
    }

    [TestMethod]
    public async Task FakeShellAcknowledgementDoesNotClaimExecutionCorrelation()
    {
        await using FakeAppServerProcess server = await FakeAppServerProcess.StartAsync("Issue155ShellEvents");
        await server.SendAsync(new { id = 1, method = "initialize", @params = new { } });
        using JsonDocument initialized = await server.ReadAsync();
        Assert.IsTrue(initialized.RootElement.TryGetProperty("result", out _));
        await server.SendAsync(new { id = 2, method = "thread/start", @params = new { } });
        using JsonDocument threadStarted = await server.ReadAsync();
        using JsonDocument idleStatus = await server.ReadAsync();
        Assert.AreEqual("thread/status/changed", idleStatus.RootElement.GetProperty("method").GetString());
        string threadId = threadStarted.RootElement.GetProperty("result").GetProperty("thread").GetProperty("id").GetString()!;
        await server.SendAsync(new { id = 3, method = "thread/shellCommand", @params = new { threadId, command = "echo fixture", timeoutMs = 0 } });
        using JsonDocument acknowledgement = await server.ReadAsync();
        Assert.AreEqual(0, acknowledgement.RootElement.GetProperty("result").EnumerateObject().Count());

        using JsonDocument turnStarted = await server.ReadAsync();
        using JsonDocument output = await server.ReadAsync();
        using JsonDocument turnCompleted = await server.ReadAsync();
        Assert.AreEqual("turn/started", turnStarted.RootElement.GetProperty("method").GetString());
        Assert.AreEqual("item/commandExecution/outputDelta", output.RootElement.GetProperty("method").GetString());
        Assert.AreEqual("turn/completed", turnCompleted.RootElement.GetProperty("method").GetString());
        Assert.AreEqual(threadId, output.RootElement.GetProperty("params").GetProperty("threadId").GetString());
        Assert.AreEqual("fake-shell-event-turn-7", turnStarted.RootElement.GetProperty("params").GetProperty("turnId").GetString());
    }

    [TestMethod]
    public async Task FakeRootTurnIdCompletionReturnsActiveTurnToIdle()
    {
        await using FakeAppServerProcess server = await FakeAppServerProcess.StartAsync("Issue252RootTurnId");
        await using JsonLineRpcConnection connection = await server.ConnectAsync();
        await using var service = new CodexSessionService(new ApprovalPolicyEngine(new PathAccessPolicy()), new SecretRedactor());
        var completed = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.ConversationEventReceived += (value, _) =>
        {
            if (value.Kind == ConversationEventKind.TurnCompleted && value.TurnId is { } completedTurnId)
            {
                completed.TrySetResult(completedTurnId);
            }

            return Task.CompletedTask;
        };
        await service.InitializeAsync(connection, new WorkerOptions
        {
            WorkingDirectory = Environment.CurrentDirectory,
            ExtensionVersion = "issue-252-integration-test",
        }, CancellationToken.None);
        ThreadSummary thread = await service.StartThreadAsync(CancellationToken.None);

        string startedTurnId = await service.StartTurnAsync(
            new StartTurnRequest { ThreadId = thread.Id, Text = "exercise distinct root turn id" },
            CancellationToken.None);

        Assert.AreEqual("fake-turn-1", startedTurnId);
        Assert.AreEqual(startedTurnId, service.ActiveTurnId);
        Assert.AreEqual("fake-root-turn-2", await completed.Task.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.IsNull(service.ActiveTurnId);
    }

    [TestMethod]
    public async Task FakeEmitsMixedMcpAndArtifactItemsWithStableIds()
    {
        await using FakeAppServerProcess server = await FakeAppServerProcess.StartAsync("Issue155MixedArtifacts");
        await server.SendAsync(new { id = 1, method = "initialize", @params = new { } });
        using JsonDocument initialized = await server.ReadAsync();
        Assert.IsTrue(initialized.RootElement.TryGetProperty("result", out _));
        await server.SendAsync(new { id = 2, method = "thread/start", @params = new { } });
        using JsonDocument threadStarted = await server.ReadAsync();
        using JsonDocument idleStatus = await server.ReadAsync();
        Assert.AreEqual("thread/status/changed", idleStatus.RootElement.GetProperty("method").GetString());
        string threadId = threadStarted.RootElement.GetProperty("result").GetProperty("thread").GetProperty("id").GetString()!;
        await server.SendAsync(new { id = 3, method = "turn/start", @params = new { threadId, input = new object[] { new { type = "text", text = "collect artifacts" } } } });
        using JsonDocument turnStarted = await server.ReadAsync();
        Assert.IsTrue(turnStarted.RootElement.TryGetProperty("result", out _));

        var completedItemTypes = new HashSet<string>(StringComparer.Ordinal);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (completedItemTypes.Count < 4)
        {
            using JsonDocument message = await server.ReadAsync(timeout.Token);
            if (!message.RootElement.TryGetProperty("method", out JsonElement method) || method.GetString() != "item/completed")
            {
                continue;
            }

            JsonElement parameters = message.RootElement.GetProperty("params");
            Assert.AreEqual(threadId, parameters.GetProperty("threadId").GetString());
            Assert.IsTrue(parameters.TryGetProperty("turnId", out _));
            JsonElement item = parameters.GetProperty("item");
            completedItemTypes.Add(item.GetProperty("type").GetString()!);
            if (item.GetProperty("type").GetString() == "mcpToolCall")
            {
                Assert.AreEqual(2, item.GetProperty("result").GetProperty("content").GetArrayLength());
            }
        }

        CollectionAssert.AreEquivalent(
            new[] { "mcpToolCall", "imageView", "imageGeneration", "fileChange" },
            completedItemTypes.ToArray());
    }

    [TestMethod]
    public async Task FakeExposesModelModalitiesAndBoundedNoticeKinds()
    {
        await using FakeAppServerProcess server = await FakeAppServerProcess.StartAsync("Issue155ModelModalityAdmission,Issue155NoticeBurst");
        await server.SendAsync(new { id = 1, method = "initialize", @params = new { } });
        using JsonDocument initialized = await server.ReadAsync();
        Assert.IsTrue(initialized.RootElement.TryGetProperty("result", out _));

        await server.SendAsync(new { id = 2, method = "model/list", @params = new { } });
        using JsonDocument models = await server.ReadAsync();
        JsonElement modelData = models.RootElement.GetProperty("result").GetProperty("data");
        CollectionAssert.AreEqual(new[] { "text", "image" }, modelData[0].GetProperty("inputModalities").EnumerateArray().Select(value => value.GetString()).ToArray());
        Assert.AreEqual(0, modelData[1].GetProperty("inputModalities").GetArrayLength());
        Assert.IsTrue(modelData[0].GetProperty("availableAccessPrograms").GetProperty("cyber").GetArrayLength() > 0);

        await server.SendAsync(new { id = 3, method = "thread/start", @params = new { } });
        using JsonDocument threadStarted = await server.ReadAsync();
        using JsonDocument idleStatus = await server.ReadAsync();
        Assert.AreEqual("thread/status/changed", idleStatus.RootElement.GetProperty("method").GetString());
        string threadId = threadStarted.RootElement.GetProperty("result").GetProperty("thread").GetProperty("id").GetString()!;
        await server.SendAsync(new { id = 4, method = "turn/start", @params = new { threadId, input = new object[] { new { type = "text", text = "exercise status" } } } });
        using JsonDocument turnStarted = await server.ReadAsync();
        Assert.IsTrue(turnStarted.RootElement.TryGetProperty("result", out _));

        var noticeMethods = new HashSet<string>(StringComparer.Ordinal);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (noticeMethods.Count < 5)
        {
            using JsonDocument message = await server.ReadAsync(timeout.Token);
            if (message.RootElement.TryGetProperty("method", out JsonElement method))
            {
                string? methodName = method.GetString();
                if (methodName is "thread/status/changed" or "configWarning" or "model/rerouted" or "model/verification" or "item/mcpToolCall/progress")
                {
                    noticeMethods.Add(methodName);
                }
            }
        }

        Assert.AreEqual(5, noticeMethods.Count);
    }

    [TestMethod]
    public async Task FakeSandboxFixturesCoverCompletionBeforeAckAndFalseStartedResponse()
    {
        await using FakeAppServerProcess completionFirst = await FakeAppServerProcess.StartAsync("Issue155SandboxCompletionBeforeAck");
        await completionFirst.SendAsync(new { id = 1, method = "initialize", @params = new { } });
        using JsonDocument initialized = await completionFirst.ReadAsync();
        Assert.IsTrue(initialized.RootElement.TryGetProperty("result", out _));
        await completionFirst.SendAsync(new { id = 2, method = "windowsSandbox/setupStart", @params = new { mode = "unelevated", cwd = (string?)null } });
        using JsonDocument completed = await completionFirst.ReadAsync();
        using JsonDocument acknowledgement = await completionFirst.ReadAsync();
        Assert.AreEqual("windowsSandbox/setupCompleted", completed.RootElement.GetProperty("method").GetString());
        Assert.IsTrue(completed.RootElement.GetProperty("params").GetProperty("success").GetBoolean());
        Assert.IsTrue(acknowledgement.RootElement.GetProperty("result").GetProperty("started").GetBoolean());

        await using FakeAppServerProcess rejected = await FakeAppServerProcess.StartAsync("Issue155SandboxStartedFalse");
        await rejected.SendAsync(new { id = 1, method = "initialize", @params = new { } });
        using JsonDocument secondInitialize = await rejected.ReadAsync();
        Assert.IsTrue(secondInitialize.RootElement.TryGetProperty("result", out _));
        await rejected.SendAsync(new { id = 2, method = "windowsSandbox/setupStart", @params = new { mode = "elevated" } });
        using JsonDocument falseAcknowledgement = await rejected.ReadAsync();
        using JsonDocument failed = await rejected.ReadAsync();
        Assert.IsFalse(falseAcknowledgement.RootElement.GetProperty("result").GetProperty("started").GetBoolean());
        Assert.IsFalse(failed.RootElement.GetProperty("params").GetProperty("success").GetBoolean());

        await using FakeAppServerProcess stale = await FakeAppServerProcess.StartAsync("Issue155SandboxStaleCompletion");
        await stale.SendAsync(new { id = 1, method = "initialize", @params = new { } });
        using JsonDocument thirdInitialize = await stale.ReadAsync();
        Assert.IsTrue(thirdInitialize.RootElement.TryGetProperty("result", out _));
        using JsonDocument unsolicitedCompletion = await stale.ReadAsync();
        Assert.AreEqual("windowsSandbox/setupCompleted", unsolicitedCompletion.RootElement.GetProperty("method").GetString());
        await stale.SendAsync(new { id = 2, method = "windowsSandbox/setupStart", @params = new { mode = "elevated" } });
        using JsonDocument staleScenarioAck = await stale.ReadAsync();
        Assert.IsTrue(staleScenarioAck.RootElement.GetProperty("result").GetProperty("started").GetBoolean());

        await using FakeAppServerProcess unsupported = await FakeAppServerProcess.StartAsync("Issue155SandboxUnsupported");
        await unsupported.SendAsync(new { id = 1, method = "initialize", @params = new { } });
        using JsonDocument fourthInitialize = await unsupported.ReadAsync();
        Assert.IsTrue(fourthInitialize.RootElement.TryGetProperty("result", out _));
        await unsupported.SendAsync(new { id = 2, method = "windowsSandbox/readiness", @params = new { } });
        using JsonDocument readiness = await unsupported.ReadAsync();
        Assert.AreEqual("updateRequired", readiness.RootElement.GetProperty("result").GetProperty("status").GetString());
    }

    private static string CreateServerPath(string fileName)
        => OperatingSystem.IsWindows() ? $@"C:\workspace\{fileName}" : $"/workspace/{fileName}";

    private sealed class FakeAppServerProcess : IAsyncDisposable
    {
        private readonly Process process;

        private FakeAppServerProcess(Process process) => this.process = process;

        public static Task<FakeAppServerProcess> StartAsync(string scenario)
        {
            string root = FindRepositoryRoot();
#if DEBUG
            const string configuration = "Debug";
#else
            const string configuration = "Release";
#endif
            string fakeOutput = Path.Combine(root, "src", "Codex.AppServer.Fake", "bin", configuration, "net10.0", "Codex.AppServer.Fake.dll");
            string runtimeConfig = Path.Combine(root, "src", "Codex.AppServer.Fake", "bin", configuration, "net10.0", "Codex.AppServer.Fake.runtimeconfig.json");
            if (!File.Exists(fakeOutput) || !File.Exists(runtimeConfig))
            {
                throw new FileNotFoundException("The Fake AppServer build output is missing. Ensure the Core test project builds the Fake project.");
            }

            var startInfo = new ProcessStartInfo("dotnet")
            {
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                WorkingDirectory = root,
            };
            startInfo.ArgumentList.Add("exec");
            startInfo.ArgumentList.Add("--runtimeconfig");
            startInfo.ArgumentList.Add(runtimeConfig);
            startInfo.ArgumentList.Add(fakeOutput);
            startInfo.Environment["CODEX_FAKE_SCENARIO"] = scenario;

            Process process = Process.Start(startInfo) ?? throw new InvalidOperationException("Could not start Fake AppServer.");
            process.StandardInput.AutoFlush = true;
            return Task.FromResult(new FakeAppServerProcess(process));
        }

        public Task SendAsync<T>(T request)
            => process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(request, WireJsonOptions));

        public async Task<JsonLineRpcConnection> ConnectAsync()
        {
            var connection = new JsonLineRpcConnection(process.StandardOutput.BaseStream, process.StandardInput.BaseStream);
            await connection.StartAsync(CancellationToken.None);
            return connection;
        }

        public async Task<JsonDocument> ReadAsync(CancellationToken cancellationToken = default)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            string? line = await process.StandardOutput.ReadLineAsync(timeout.Token);
            return line is null
                ? throw new EndOfStreamException(await process.StandardError.ReadToEndAsync(timeout.Token))
                : JsonDocument.Parse(line);
        }

        public async ValueTask DisposeAsync()
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
            }

            process.Dispose();
        }

        private static string FindRepositoryRoot()
        {
            for (DirectoryInfo? directory = new(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            {
                if (File.Exists(Path.Combine(directory.FullName, "src", "Codex.AppServer.Fake", "Codex.AppServer.Fake.csproj")))
                {
                    return directory.FullName;
                }
            }

            throw new DirectoryNotFoundException("Could not find the repository root containing the Fake AppServer project.");
        }
    }

    private sealed class RecordingJsonRpcConnection(IJsonRpcConnection inner) : IJsonRpcConnection
    {
        public List<string> Requests { get; } = [];

        public List<string> Notifications { get; } = [];

        public event Func<JsonRpcMessage, CancellationToken, Task>? NotificationReceived
        {
            add => inner.NotificationReceived += value;
            remove => inner.NotificationReceived -= value;
        }

        public event Func<JsonRpcMessage, CancellationToken, Task<JsonElement>>? RequestReceived
        {
            add => inner.RequestReceived += value;
            remove => inner.RequestReceived -= value;
        }

        public event EventHandler<Exception?>? Closed
        {
            add => inner.Closed += value;
            remove => inner.Closed -= value;
        }

        public Task StartAsync(CancellationToken cancellationToken) => inner.StartAsync(cancellationToken);

        public Task<JsonElement> SendRequestAsync(string method, object? parameters, TimeSpan timeout, CancellationToken cancellationToken)
        {
            Requests.Add(method);
            return inner.SendRequestAsync(method, parameters, timeout, cancellationToken);
        }

        public Task SendNotificationAsync(string method, object? parameters, CancellationToken cancellationToken)
        {
            Notifications.Add(method);
            return inner.SendNotificationAsync(method, parameters, cancellationToken);
        }

        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }
}
