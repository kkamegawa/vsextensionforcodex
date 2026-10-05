using System.Collections.Concurrent;
using System.Text.Json;
using Codex.AppServer.Protocol;
using Codex.VisualStudio.Contracts;
using Codex.VisualStudio.Worker;

namespace Codex.VisualStudio.Core.Tests;

[TestClass]
public sealed class InteractionAuthenticationTests
{
    [TestMethod]
    public async Task LocalInitialize_DeclaresExplicitGatewayCapabilityAndReadsStatusAfterInitialized()
    {
        var connection = new RecordingConnection();
        await using var service = CreateService();

        await service.InitializeAsync(connection, Options(), CancellationToken.None);

        Assert.AreEqual("initialize", connection.Requests[0].Method);
        Assert.AreEqual("account/gatewayOAuth/read", connection.Requests[1].Method);
        Assert.AreEqual("initialized", connection.Notifications[0].Method);
        CollectionAssert.AreEqual(new[] { "request:initialize", "notification:initialized", "request:account/gatewayOAuth/read" }, connection.Trace.ToArray());
        JsonElement capabilities = JsonSerializer.SerializeToElement(connection.Requests[0].Parameters).GetProperty("capabilities");
        Assert.IsTrue(capabilities.GetProperty("explicitGatewayOauth").GetBoolean());
    }

    [TestMethod]
    public async Task RemoteInitialize_OmitsGatewayCapabilityAndRejectsLoginAndCancel()
    {
        var connection = new RecordingConnection();
        WorkerOptions options = Options();
        options.RemoteEndpoint = "https://remote.example.test";
        options.LocalRoot = Path.GetTempPath();
        options.ServerRoot = "/srv/workspace";
        await using var service = CreateService();

        await service.InitializeAsync(connection, options, CancellationToken.None);

        JsonElement capabilities = JsonSerializer.SerializeToElement(connection.Requests[0].Parameters).GetProperty("capabilities");
        Assert.IsFalse(capabilities.TryGetProperty("explicitGatewayOauth", out _));
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => service.LoginGatewayOAuthAsync(CancellationToken.None));
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => service.CancelGatewayOAuthAsync(CancellationToken.None));
        Assert.IsFalse(connection.Requests.Any(request => request.Method is "account/gatewayOAuth/login" or "account/gatewayOAuth/cancel"));
    }

    [TestMethod]
    public async Task MalformedOrFailedGatewayRead_GatesCredentialRpcBeforeTurnStart()
    {
        foreach (bool throwRead in new[] { false, true })
        {
            var connection = new RecordingConnection();
            if (throwRead)
            {
                connection.GatewayReadFailure = new IOException("sensitive-read-error");
            }
            else
            {
                connection.GatewayOAuthReadResponse = JsonSerializer.SerializeToElement(new
                {
                    providerId = "provider",
                    providerName = "Provider",
                    required = true,
                    status = "notReady",
                });
            }

            await using var service = CreateService();
            await service.InitializeAsync(connection, Options(), CancellationToken.None);

            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => service.StartTurnAsync(
                new StartTurnRequest { ThreadId = "thread", Text = "must be blocked" },
                CancellationToken.None));
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => service.GetRateLimitsAsync(CancellationToken.None));
            Assert.IsFalse(connection.Requests.Any(request => request.Method == "turn/start"));
            Assert.IsFalse(connection.Requests.Any(request => request.Method == "skills/list"));
            Assert.IsFalse(connection.Requests.Any(request => request.Method == "account/rateLimits/read"));
        }
    }

    [TestMethod]
    public async Task LoginWaitDoesNotBlockQuestionsAndChangedSuccessCanPrecedeLoginResponse()
    {
        var connection = new RecordingConnection
        {
            GatewayOAuthReadResponse = GatewayRead(required: true, status: "notReady"),
        };
        var loginStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finishLogin = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        connection.AsyncHandler = (method, _, _) => method == "account/gatewayOAuth/login"
            ? StartPendingLoginAsync()
            : Task.FromResult(JsonSerializer.SerializeToElement(new { }));
        Task<JsonElement>? questionTask = null;
        var questionAnswered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var service = CreateService();
        service.UserInputRequested += async (request, cancellationToken) =>
        {
            UserInputOption option = request.Questions.Single().Options.Single();
            await service.ResolveUserInputAsync(new ResolveUserInputRequest
            {
                RequestId = request.RequestId,
                Answers = new Dictionary<string, UserInputAnswer>
                {
                    [request.Questions[0].Id] = new()
                    {
                        Kind = UserInputAnswerKind.SelectedOptions,
                        OptionIds = [option.OptionId],
                    },
                },
            }, cancellationToken);
            questionAnswered.TrySetResult();
        };

        await service.InitializeAsync(connection, Options(), CancellationToken.None);
        InteractionAuthStatus loginStatus = await service.LoginGatewayOAuthAsync(CancellationToken.None);
        Assert.AreEqual(InteractionAuthState.LoginPending, loginStatus.State);
        await loginStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        questionTask = connection.EmitRequestAsync("question-1", "item/tool/requestUserInput", new
        {
            threadId = "thread-1",
            turnId = "turn-1",
            itemId = "item-1",
            isBlocking = true,
            questions = new[]
            {
                new { id = "confirmation", header = "Continue", question = "Continue?", options = new[] { new { label = "Yes", description = "Continue" } } },
            },
        });
        await questionAnswered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        JsonElement answer = await questionTask.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.AreEqual("Yes", answer.GetProperty("answers").GetProperty("confirmation").GetProperty("answers")[0].GetString());

        await connection.EmitNotificationAsync("account/gatewayOAuth/changed", new
        {
            providerId = "provider",
            status = "succeeded",
        });
        await service.GetRateLimitsAsync(CancellationToken.None);
        Assert.IsTrue(connection.Requests.Any(request => request.Method == "account/rateLimits/read"));

        finishLogin.TrySetResult(JsonSerializer.SerializeToElement(new { }));
        await Task.Delay(50);
        await service.GetRateLimitsAsync(CancellationToken.None);
        Assert.AreEqual(2, connection.Requests.Count(request => request.Method == "account/rateLimits/read"));

        Task<JsonElement> StartPendingLoginAsync()
        {
            loginStarted.TrySetResult();
            return finishLogin.Task;
        }
    }

    [TestMethod]
    public async Task CancelAndReconnect_IgnoresLateLoginAndNotificationsFromRetiredConnection()
    {
        var first = new RecordingConnection { GatewayOAuthReadResponse = GatewayRead(required: true, status: "notReady") };
        var loginStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finishLogin = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        first.AsyncHandler = (method, _, _) => method == "account/gatewayOAuth/login"
            ? StartPendingLoginAsync()
            : Task.FromResult(JsonSerializer.SerializeToElement(new { }));
        var second = new RecordingConnection
        {
            GatewayOAuthReadResponse = GatewayRead(required: false, status: null),
        };
        await using var service = CreateService();
        var observed = new ConcurrentQueue<InteractionAuthStatus>();
        service.InteractionAuthStatusChanged += (status, _) =>
        {
            observed.Enqueue(status);
            return Task.CompletedTask;
        };

        await service.InitializeAsync(first, Options(), CancellationToken.None);
        await service.LoginGatewayOAuthAsync(CancellationToken.None);
        await loginStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await service.CancelGatewayOAuthAsync(CancellationToken.None);
        await service.InitializeAsync(second, Options(), CancellationToken.None);
        await first.EmitNotificationAsync("account/gatewayOAuth/changed", new { providerId = "provider", status = "succeeded" });
        finishLogin.TrySetResult(JsonSerializer.SerializeToElement(new { }));

        InteractionAuthStatus status = await service.ReadGatewayOAuthAsync(CancellationToken.None);
        Assert.IsTrue(status.IsLocal);
        Assert.AreEqual(InteractionAuthState.Ready, status.State);
        Assert.IsFalse(observed.Any(item => item.State == InteractionAuthState.Authenticated && item.IsLocal == status.IsLocal));

        Task<JsonElement> StartPendingLoginAsync()
        {
            loginStarted.TrySetResult();
            return finishLogin.Task;
        }
    }

    [TestMethod]
    public async Task McpCompletionBeforeLoginResponse_DoesNotRestoreStaleAuthorizationUrl()
    {
        var connection = new RecordingConnection();
        var loginStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finishLogin = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        connection.AsyncHandler = (method, _, _) => method == "mcpServer/oauth/login"
            ? StartPendingLoginAsync()
            : Task.FromResult(JsonSerializer.SerializeToElement(new { }));
        var opened = new ConcurrentQueue<Uri>();
        await using var service = CreateService(uri => opened.Enqueue(uri));
        await service.InitializeAsync(connection, Options(), CancellationToken.None);

        Task<McpOAuthLoginStatus> loginTask = service.StartMcpOAuthLoginAsync(
            new StartMcpOAuthLoginRequest { ServerName = "example-mcp", ThreadId = "thread-1" },
            CancellationToken.None);
        await loginStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await connection.EmitNotificationAsync("mcpServer/oauthLogin/completed", new { name = "example-mcp", success = true });
        finishLogin.TrySetResult(JsonSerializer.SerializeToElement(new { authorizationUrl = "https://login.example.test/old" }));
        McpOAuthLoginStatus loginStatus = await loginTask.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.AreEqual(InteractionAuthState.Authenticated, loginStatus.State);
        Assert.IsNull(loginStatus.OpenAuthorizationActionId);
        Assert.IsFalse(connection.Requests.Any(request => request.Method == "mcpServer/tools/call"));
        Assert.AreEqual(0, opened.Count);

        Task<JsonElement> StartPendingLoginAsync()
        {
            loginStarted.TrySetResult();
            return finishLogin.Task;
        }
    }

    [TestMethod]
    public async Task AuthorizationUrlIsOpenedOnlyByExplicitActionAndDismissRevokesMcpLink()
    {
        var connection = new RecordingConnection();
        connection.AsyncHandler = (method, _, _) => method == "mcpServer/oauth/login"
            ? Task.FromResult(JsonSerializer.SerializeToElement(new { authorizationUrl = "https://login.example.test/authorize?secret=private" }))
            : Task.FromResult(JsonSerializer.SerializeToElement(new { }));
        var opened = new ConcurrentQueue<Uri>();
        await using var service = CreateService(uri => opened.Enqueue(uri));
        await service.InitializeAsync(connection, Options(), CancellationToken.None);

        McpOAuthLoginStatus status = await service.StartMcpOAuthLoginAsync(
            new StartMcpOAuthLoginRequest { ServerName = "example-mcp" },
            CancellationToken.None);
        Assert.AreEqual(InteractionAuthState.LoginPending, status.State);
        Assert.AreEqual("https://login.example.test", status.OriginDisplay);
        Assert.IsNotNull(status.OpenAuthorizationActionId);
        Assert.AreEqual(0, opened.Count);

        await service.OpenAuthorizationUrlAsync(new OpenAuthorizationUrlRequest { ActionId = status.OpenAuthorizationActionId! }, CancellationToken.None);
        Assert.AreEqual("https://login.example.test/authorize?secret=private", opened.Single().ToString());

        McpOAuthLoginStatus second = await service.StartMcpOAuthLoginAsync(
            new StartMcpOAuthLoginRequest { ServerName = "example-mcp" },
            CancellationToken.None);
        Assert.IsNotNull(second.OpenAuthorizationActionId);
        await service.DismissMcpOAuthLoginAsync(new DismissMcpOAuthLoginRequest { OperationId = second.OperationId }, CancellationToken.None);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => service.OpenAuthorizationUrlAsync(
            new OpenAuthorizationUrlRequest { ActionId = second.OpenAuthorizationActionId! },
            CancellationToken.None));
        Assert.AreEqual(1, opened.Count);
    }

    [TestMethod]
    public async Task ReauthenticationRequired_RetiresMcpUrlElicitationAndDoesNotReplayToolCalls()
    {
        var connection = new RecordingConnection();
        var opened = new ConcurrentQueue<Uri>();
        await using var service = CreateService(uri => opened.Enqueue(uri));
        var elicitationReceived = new TaskCompletionSource<McpElicitationRequest>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.McpElicitationRequested += (request, _) =>
        {
            elicitationReceived.TrySetResult(request);
            return Task.CompletedTask;
        };
        await service.InitializeAsync(connection, Options(), CancellationToken.None);

        Task<JsonElement> pending = connection.EmitRequestAsync("elicitation-1", "mcpServer/elicitation/request", new
        {
            serverName = "example-mcp",
            mode = "url",
            elicitationId = "elicitation-id",
            message = "Sign in",
            url = "https://login.example.test/path?secret=never-project",
        });
        McpElicitationRequest prompt = await elicitationReceived.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.IsNotNull(prompt.OpenAuthorizationActionId);
        Assert.IsFalse(JsonSerializer.Serialize(prompt).Contains("never-project", StringComparison.Ordinal));

        await connection.EmitNotificationAsync("mcpServer/startupStatus/updated", new
        {
            name = "example-mcp",
            status = "failed",
            failureReason = "reauthenticationRequired",
        });
        JsonElement result = await pending.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.AreEqual("cancel", result.GetProperty("action").GetString());
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => service.OpenAuthorizationUrlAsync(
            new OpenAuthorizationUrlRequest { ActionId = prompt.OpenAuthorizationActionId! },
            CancellationToken.None));
        Assert.AreEqual(0, opened.Count);
        Assert.IsFalse(connection.Requests.Any(request => request.Method == "mcpServer/tools/call"));
    }

    private static CodexSessionService CreateService(Action<Uri>? authorizationUrlOpener = null)
        => new(
            new ApprovalPolicyEngine(new PathAccessPolicy()),
            new SecretRedactor(),
            null,
            null,
            null,
            null,
            authorizationUrlOpener);

    private static WorkerOptions Options() => new()
    {
        WorkingDirectory = Path.GetTempPath(),
        ExtensionVersion = "test",
    };

    private static JsonElement GatewayRead(bool required, string? status)
        => JsonSerializer.SerializeToElement(new
        {
            providerId = "provider",
            providerName = "Test provider",
            required,
            status,
        });

    private sealed class RecordingConnection : IJsonRpcConnection
    {
        private readonly ConcurrentQueue<RecordedRequest> requests = new();
        private readonly ConcurrentQueue<RecordedNotification> notifications = new();
        private readonly ConcurrentQueue<string> trace = new();

        public event Func<JsonRpcMessage, CancellationToken, Task>? NotificationReceived;

        public event Func<JsonRpcMessage, CancellationToken, Task<JsonElement>>? RequestReceived;

        public event EventHandler<Exception?>? Closed;

        public Func<string, object?, CancellationToken, Task<JsonElement>>? AsyncHandler { get; set; }

        public JsonElement GatewayOAuthReadResponse { get; set; } = GatewayRead(required: false, status: null);

        public Exception? GatewayReadFailure { get; set; }

        public RecordedRequest[] Requests => requests.ToArray();

        public RecordedNotification[] Notifications => notifications.ToArray();

        public IReadOnlyList<string> Trace => trace.ToArray();

        public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public async Task<JsonElement> SendRequestAsync(string method, object? parameters, TimeSpan timeout, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            requests.Enqueue(new RecordedRequest(method, parameters));
            trace.Enqueue($"request:{method}");
            if (method == "account/gatewayOAuth/read")
            {
                if (GatewayReadFailure is not null)
                {
                    throw GatewayReadFailure;
                }

                return GatewayOAuthReadResponse.Clone();
            }

            if (AsyncHandler is not null)
            {
                return await AsyncHandler(method, parameters, cancellationToken).ConfigureAwait(false);
            }

            return JsonSerializer.SerializeToElement(new { });
        }

        public Task SendNotificationAsync(string method, object? parameters, CancellationToken cancellationToken)
        {
            notifications.Enqueue(new RecordedNotification(method, parameters));
            trace.Enqueue($"notification:{method}");
            return Task.CompletedTask;
        }

        public Task EmitNotificationAsync(string method, object parameters)
            => NotificationReceived?.Invoke(new JsonRpcMessage
            {
                Method = method,
                Params = JsonSerializer.SerializeToElement(parameters),
            }, CancellationToken.None) ?? Task.CompletedTask;

        public Task<JsonElement> EmitRequestAsync(string id, string method, object parameters)
            => RequestReceived?.Invoke(new JsonRpcMessage
            {
                Id = JsonSerializer.SerializeToElement(id),
                Method = method,
                Params = JsonSerializer.SerializeToElement(parameters),
            }, CancellationToken.None) ?? Task.FromResult(JsonSerializer.SerializeToElement(new { }));

        public void EmitClosed(Exception? exception = null) => Closed?.Invoke(this, exception);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed record RecordedRequest(string Method, object? Parameters);

    private sealed record RecordedNotification(string Method, object? Parameters);
}
