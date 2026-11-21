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
    public async Task McpEnvironmentIdentity_RoutesSameNameLoginsAndCompletionByRegistration()
    {
        var connection = EnvironmentIdentityConnection(
            new { name = "docs", environmentId = "env-a" },
            new { name = "docs", environmentId = "env-b" });
        var statuses = new ConcurrentQueue<InteractionAuthStatus>();
        await using var service = CreateService();
        service.InteractionAuthStatusChanged += (status, _) =>
        {
            statuses.Enqueue(status);
            return Task.CompletedTask;
        };

        await service.InitializeAsync(connection, Options(experimentalApi: true), CancellationToken.None);
        McpServerListResult inventory = await service.ListMcpServersAsync(
            new ListMcpServersRequest { ThreadId = "thread-1" },
            CancellationToken.None);
        Assert.AreEqual(2, inventory.Servers.Count);
        CollectionAssert.AreEquivalent(new[] { "env-a", "env-b" }, inventory.Servers.Select(server => server.EnvironmentId).ToArray());
        Assert.IsTrue(inventory.Servers.All(server => server.HasRegistrationIdentity));
        Assert.IsTrue(inventory.Servers.All(server => server.ToolCount == 1 && server.ResourceCount == 1));
        await service.ListMcpServersAsync(
            new ListMcpServersRequest
            {
                ThreadId = "thread-1",
                ServerName = "docs",
                HasRegistrationIdentity = true,
                EnvironmentId = "env-b",
            },
            CancellationToken.None);
        JsonElement statusFilter = JsonSerializer.SerializeToElement(
            connection.Requests.Last(request => request.Method == "mcpServerStatus/list").Parameters);
        Assert.AreEqual("thread-1", statusFilter.GetProperty("threadId").GetString());
        Assert.AreEqual("docs", statusFilter.GetProperty("serverName").GetString());
        Assert.AreEqual("env-b", statusFilter.GetProperty("environmentId").GetString());

        McpOAuthLoginStatus loginA = await service.StartMcpOAuthLoginAsync(
            new StartMcpOAuthLoginRequest { ServerName = "docs", ThreadId = "thread-1", HasRegistrationIdentity = true, EnvironmentId = "env-a" },
            CancellationToken.None);
        McpOAuthLoginStatus loginB = await service.StartMcpOAuthLoginAsync(
            new StartMcpOAuthLoginRequest { ServerName = "docs", ThreadId = "thread-1", HasRegistrationIdentity = true, EnvironmentId = "env-b" },
            CancellationToken.None);

        JsonElement[] loginParameters = connection.Requests
            .Where(request => request.Method == "mcpServer/oauth/login")
            .Select(request => JsonSerializer.SerializeToElement(request.Parameters))
            .ToArray();
        CollectionAssert.AreEqual(new[] { "env-a", "env-b" }, loginParameters.Select(parameters => parameters.GetProperty("environmentId").GetString()).ToArray());
        Assert.IsTrue(loginParameters.All(parameters => parameters.GetProperty("threadId").GetString() == "thread-1"));

        await connection.EmitNotificationAsync("mcpServer/oauthLogin/completed", new
        {
            name = "docs",
            threadId = "thread-1",
            environmentId = "env-b",
            success = true,
            loginId = "env-b",
        });

        await connection.EmitNotificationAsync("mcpServer/startupStatus/updated", new
        {
            name = "docs",
            threadId = "thread-1",
            environmentId = "env-a",
            status = "failed",
        });
        await connection.EmitNotificationAsync("mcpServer/startupStatus/updated", new
        {
            name = "docs",
            threadId = "thread-1",
            status = "cancelled",
        });

        McpServerAuthStatus[] latest = statuses.Last().McpServers.ToArray();
        Assert.AreEqual(InteractionAuthState.Failed, latest.Single(server => server.EnvironmentId == "env-a").State);
        Assert.AreEqual(InteractionAuthState.Authenticated, latest.Single(server => server.EnvironmentId == "env-b").State);
        Assert.AreEqual("env-a", loginA.EnvironmentId);
        Assert.AreEqual("env-b", loginB.EnvironmentId);
    }

    [TestMethod]
    public async Task McpEnvironmentIdentity_ThreadWideNullIsForwardedAndAmbiguousNameIsRejected()
    {
        var threadWideConnection = EnvironmentIdentityConnection(new { name = "docs", environmentId = (string?)null });
        await using (var service = CreateService())
        {
            await service.InitializeAsync(threadWideConnection, Options(experimentalApi: true), CancellationToken.None);
            McpOAuthLoginStatus status = await service.StartMcpOAuthLoginAsync(
                new StartMcpOAuthLoginRequest { ServerName = "docs", ThreadId = "thread-1", HasRegistrationIdentity = true },
                CancellationToken.None);
            JsonElement parameters = JsonSerializer.SerializeToElement(threadWideConnection.Requests.Single(request => request.Method == "mcpServer/oauth/login").Parameters);
            Assert.IsTrue(parameters.TryGetProperty("environmentId", out JsonElement environmentId));
            Assert.AreEqual(JsonValueKind.Null, environmentId.ValueKind);
            Assert.IsTrue(status.HasRegistrationIdentity);
            Assert.IsNull(status.EnvironmentId);
            await service.ListMcpServersAsync(
                new ListMcpServersRequest
                {
                    ThreadId = "thread-1",
                    ServerName = "docs",
                    HasRegistrationIdentity = true,
                },
                CancellationToken.None);
            JsonElement statusFilter = JsonSerializer.SerializeToElement(
                threadWideConnection.Requests.Last(request => request.Method == "mcpServerStatus/list").Parameters);
            Assert.AreEqual("thread-1", statusFilter.GetProperty("threadId").GetString());
            Assert.AreEqual("docs", statusFilter.GetProperty("serverName").GetString());
            Assert.AreEqual(JsonValueKind.Null, statusFilter.GetProperty("environmentId").ValueKind);
        }

        var ambiguousConnection = EnvironmentIdentityConnection(
            new { name = "docs", environmentId = "env-a" },
            new { name = "docs", environmentId = "env-b" });
        await using var ambiguousService = CreateService();
        await ambiguousService.InitializeAsync(ambiguousConnection, Options(experimentalApi: true), CancellationToken.None);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => ambiguousService.StartMcpOAuthLoginAsync(
            new StartMcpOAuthLoginRequest { ServerName = "docs", ThreadId = "thread-1" },
            CancellationToken.None));
        Assert.IsFalse(ambiguousConnection.Requests.Any(request => request.Method == "mcpServer/oauth/login"));
    }

    [TestMethod]
    public async Task McpEnvironmentIdentity_UniqueMissingIdentityResolvesAndOlderTierKeepsLegacyRoute()
    {
        var uniqueConnection = EnvironmentIdentityConnection(new { name = "docs", environmentId = "only" });
        await using (var service = CreateService())
        {
            await service.InitializeAsync(uniqueConnection, Options(experimentalApi: true), CancellationToken.None);
            await service.StartMcpOAuthLoginAsync(
                new StartMcpOAuthLoginRequest { ServerName = "docs", ThreadId = "thread-1" },
                CancellationToken.None);
            JsonElement parameters = JsonSerializer.SerializeToElement(uniqueConnection.Requests.Single(request => request.Method == "mcpServer/oauth/login").Parameters);
            Assert.AreEqual("only", parameters.GetProperty("environmentId").GetString());
        }

        var olderConnection = EnvironmentIdentityConnection(new { name = "docs", environmentId = "should-be-ignored" }, version: "0.170.2");
        await using var olderService = CreateService();
        await olderService.InitializeAsync(olderConnection, Options(experimentalApi: true), CancellationToken.None);
        McpServerListResult olderInventory = await olderService.ListMcpServersAsync(new ListMcpServersRequest(), CancellationToken.None);
        Assert.IsFalse(olderInventory.Servers.Single().HasRegistrationIdentity);
        await olderService.StartMcpOAuthLoginAsync(new StartMcpOAuthLoginRequest { ServerName = "docs" }, CancellationToken.None);
        JsonElement olderParameters = JsonSerializer.SerializeToElement(olderConnection.Requests.Single(request => request.Method == "mcpServer/oauth/login").Parameters);
        Assert.IsFalse(olderParameters.TryGetProperty("environmentId", out _));
    }

    [TestMethod]
    public async Task McpEnvironmentIdentity_ReconnectRetiresPreviousRegistrationState()
    {
        var first = EnvironmentIdentityConnection(new { name = "docs", environmentId = "env-old" });
        var second = EnvironmentIdentityConnection(new { name = "docs", environmentId = "env-new" });
        await using var service = CreateService();
        await service.InitializeAsync(first, Options(experimentalApi: true), CancellationToken.None);
        await service.StartMcpOAuthLoginAsync(
            new StartMcpOAuthLoginRequest { ServerName = "docs", ThreadId = "thread-1", HasRegistrationIdentity = true, EnvironmentId = "env-old" },
            CancellationToken.None);

        await service.InitializeAsync(second, Options(experimentalApi: true), CancellationToken.None);
        await first.EmitNotificationAsync("mcpServer/oauthLogin/completed", new
        {
            name = "docs",
            threadId = "thread-1",
            environmentId = "env-old",
            success = true,
            loginId = "env-old",
        });

        InteractionAuthStatus status = await service.ReadGatewayOAuthAsync(CancellationToken.None);
        Assert.AreEqual(0, status.McpServers.Count, "A retired connection must not leak its registration state into the new connection.");
        await service.StartMcpOAuthLoginAsync(
            new StartMcpOAuthLoginRequest { ServerName = "docs", ThreadId = "thread-1", HasRegistrationIdentity = true, EnvironmentId = "env-new" },
            CancellationToken.None);
        JsonElement parameters = JsonSerializer.SerializeToElement(second.Requests.Single(request => request.Method == "mcpServer/oauth/login").Parameters);
        Assert.AreEqual("env-new", parameters.GetProperty("environmentId").GetString());
    }

    [TestMethod]
    public async Task McpEnvironmentIdentity_RequiresVersionAndExperimentalCapability()
    {
        var connection = EnvironmentIdentityConnection(new { name = "docs", environmentId = "ignored" });
        await using var service = CreateService();
        await service.InitializeAsync(connection, Options(experimentalApi: false), CancellationToken.None);
        McpServerListResult result = await service.ListMcpServersAsync(new ListMcpServersRequest(), CancellationToken.None);
        Assert.IsFalse(result.Servers.Single().HasRegistrationIdentity);
        await service.StartMcpOAuthLoginAsync(new StartMcpOAuthLoginRequest { ServerName = "docs" }, CancellationToken.None);
        JsonElement parameters = JsonSerializer.SerializeToElement(connection.Requests.Single(request => request.Method == "mcpServer/oauth/login").Parameters);
        Assert.IsFalse(parameters.TryGetProperty("environmentId", out _));
    }

    [TestMethod]
    public async Task McpEnvironmentIdentity_ElicitationApprovalCarriesRegistrationScope()
    {
        var connection = EnvironmentIdentityConnection(new { name = "docs", environmentId = "env-a" }, new { name = "docs", environmentId = "env-b" });
        var prompts = new ConcurrentQueue<McpElicitationRequest>();
        var bothPromptsReceived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var service = CreateService();
        service.McpElicitationRequested += (request, _) =>
        {
            prompts.Enqueue(request);
            if (prompts.Count == 2)
            {
                bothPromptsReceived.TrySetResult();
            }

            return Task.CompletedTask;
        };
        await service.InitializeAsync(connection, Options(experimentalApi: true), CancellationToken.None);

        Task<JsonElement> first = connection.EmitRequestAsync("elicitation-a", "mcpServer/elicitation/request", new
        {
            serverName = "docs",
            threadId = "thread-1",
            environmentId = "env-a",
            mode = "url",
            elicitationId = "elicitation-a",
            message = "Approve access for A",
            url = "https://login.example.test/a",
        });
        Task<JsonElement> second = connection.EmitRequestAsync("elicitation-b", "mcpServer/elicitation/request", new
        {
            serverName = "docs",
            threadId = "thread-1",
            environmentId = "env-b",
            mode = "url",
            elicitationId = "elicitation-b",
            message = "Approve access for B",
            url = "https://login.example.test/b",
        });
        await bothPromptsReceived.Task.WaitAsync(TimeSpan.FromSeconds(2));

        McpElicitationRequest promptA = prompts.Single(request => request.EnvironmentId == "env-a");
        McpElicitationRequest promptB = prompts.Single(request => request.EnvironmentId == "env-b");
        Assert.IsTrue(promptA.HasRegistrationIdentity);
        Assert.AreEqual("env-a", promptA.EnvironmentId);
        Assert.AreEqual("thread-1", promptA.ThreadId);
        Assert.AreEqual("env-b", promptB.EnvironmentId);

        await service.ResolveMcpElicitationAsync(new ResolveMcpElicitationRequest
        {
            RequestId = promptB.RequestId,
            Action = McpElicitationAction.Accept,
        }, CancellationToken.None);
        await service.ResolveMcpElicitationAsync(new ResolveMcpElicitationRequest
        {
            RequestId = promptA.RequestId,
            Action = McpElicitationAction.Decline,
        }, CancellationToken.None);
        Assert.AreEqual("accept", (await second.WaitAsync(TimeSpan.FromSeconds(2))).GetProperty("action").GetString());
        Assert.AreEqual("decline", (await first.WaitAsync(TimeSpan.FromSeconds(2))).GetProperty("action").GetString());
    }

    [TestMethod]
    public async Task McpOAuthCompletionWithoutThreadId_UsesLoginIdAcrossSameEnvironmentThreads()
    {
        var connection = new RecordingConnection
        {
            InitializeResponse = JsonSerializer.SerializeToElement(new { userAgent = "codex-cli/0.171.0" }),
        };
        connection.AsyncHandler = (method, parameters, _) =>
        {
            if (method == "mcpServer/oauth/login")
            {
                JsonElement request = JsonSerializer.SerializeToElement(parameters);
                return Task.FromResult(JsonSerializer.SerializeToElement(new
                {
                    loginId = request.GetProperty("threadId").GetString(),
                    authorizationUrl = "https://login.example.test/authorize",
                }));
            }

            return Task.FromResult(JsonSerializer.SerializeToElement(new { }));
        };
        var statuses = new ConcurrentQueue<InteractionAuthStatus>();
        await using var service = CreateService();
        service.InteractionAuthStatusChanged += (status, _) =>
        {
            statuses.Enqueue(status);
            return Task.CompletedTask;
        };
        await service.InitializeAsync(connection, Options(experimentalApi: true), CancellationToken.None);

        await service.StartMcpOAuthLoginAsync(new StartMcpOAuthLoginRequest
        {
            ServerName = "docs",
            ThreadId = "thread-1",
            HasRegistrationIdentity = true,
            EnvironmentId = "env-a",
        }, CancellationToken.None);
        await service.StartMcpOAuthLoginAsync(new StartMcpOAuthLoginRequest
        {
            ServerName = "docs",
            ThreadId = "thread-2",
            HasRegistrationIdentity = true,
            EnvironmentId = "env-a",
        }, CancellationToken.None);

        await connection.EmitNotificationAsync("mcpServer/oauthLogin/completed", new
        {
            name = "docs",
            environmentId = "env-a",
            success = true,
            loginId = "thread-2",
        });

        McpServerAuthStatus[] latest = statuses.Last().McpServers.ToArray();
        Assert.AreEqual(InteractionAuthState.LoginPending, latest.Single(server => server.ThreadId == "thread-1").State);
        Assert.AreEqual(InteractionAuthState.Authenticated, latest.Single(server => server.ThreadId == "thread-2").State);
    }

    [TestMethod]
    public async Task McpRegistrationKey_TreatsUnitSeparatorAsDataInServerAndThreadIds()
    {
        var connection = new RecordingConnection
        {
            InitializeResponse = JsonSerializer.SerializeToElement(new { userAgent = "codex-cli/0.171.0" }),
        };
        connection.AsyncHandler = (method, _, _) => method == "mcpServer/oauth/login"
            ? Task.FromResult(JsonSerializer.SerializeToElement(new { loginId = Guid.NewGuid().ToString("N"), authorizationUrl = "https://login.example.test/authorize" }))
            : Task.FromResult(JsonSerializer.SerializeToElement(new { }));
        await using var service = CreateService();
        await service.InitializeAsync(connection, Options(experimentalApi: true), CancellationToken.None);

        McpOAuthLoginStatus first = await service.StartMcpOAuthLoginAsync(new StartMcpOAuthLoginRequest
        {
            ServerName = "b\u001fc",
            ThreadId = "a",
            HasRegistrationIdentity = true,
            EnvironmentId = "d",
        }, CancellationToken.None);
        McpOAuthLoginStatus second = await service.StartMcpOAuthLoginAsync(new StartMcpOAuthLoginRequest
        {
            ServerName = "c",
            ThreadId = "a\u001fb",
            HasRegistrationIdentity = true,
            EnvironmentId = "d",
        }, CancellationToken.None);

        Assert.AreNotEqual(first.OperationId, second.OperationId);
        InteractionAuthStatus status = await service.ReadGatewayOAuthAsync(CancellationToken.None);
        Assert.AreEqual(2, status.McpServers.Count);
        Assert.AreEqual("b\u001fc", status.McpServers.Single(item => item.ThreadId == "a").ServerName);
        Assert.AreEqual("c", status.McpServers.Single(item => item.ThreadId == "a\u001fb").ServerName);
    }

    [TestMethod]
    public async Task McpRetirementWithThreadAndRegistrationScope_LeavesOtherThreadElicitationPending()
    {
        var connection = new RecordingConnection
        {
            InitializeResponse = JsonSerializer.SerializeToElement(new { userAgent = "codex-cli/0.171.0" }),
        };
        await using var service = CreateService();
        var prompts = new ConcurrentQueue<McpElicitationRequest>();
        service.McpElicitationRequested += (request, _) =>
        {
            prompts.Enqueue(request);
            return Task.CompletedTask;
        };
        await service.InitializeAsync(connection, Options(experimentalApi: true), CancellationToken.None);

        Task<JsonElement> first = connection.EmitRequestAsync("elicitation-thread-1", "mcpServer/elicitation/request", new
        {
            serverName = "docs", mode = "url", elicitationId = "el-1", message = "Sign in", url = "https://login.example.test/a", threadId = "thread-1", environmentId = "env-a",
        });
        Task<JsonElement> second = connection.EmitRequestAsync("elicitation-thread-2", "mcpServer/elicitation/request", new
        {
            serverName = "docs", mode = "url", elicitationId = "el-2", message = "Sign in", url = "https://login.example.test/b", threadId = "thread-2", environmentId = "env-a",
        });
        DateTime deadline = DateTime.UtcNow.AddSeconds(2);
        while (prompts.Count < 2 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10);
        }
        Assert.AreEqual(2, prompts.Count);

        await connection.EmitNotificationAsync("mcpServer/startupStatus/updated", new
        {
            name = "docs", threadId = "thread-1", environmentId = "env-a", status = "failed", failureReason = "reauthenticationRequired",
        });
        Assert.AreEqual("cancel", (await first.WaitAsync(TimeSpan.FromSeconds(2))).GetProperty("action").GetString());
        Assert.IsFalse(second.IsCompleted, "A same-name registration in another thread must remain pending.");

        McpElicitationRequest surviving = prompts.Single(request => request.ThreadId == "thread-2");
        await service.ResolveMcpElicitationAsync(new ResolveMcpElicitationRequest
        {
            RequestId = surviving.RequestId,
            Action = McpElicitationAction.Cancel,
        }, CancellationToken.None);
        Assert.AreEqual("cancel", (await second.WaitAsync(TimeSpan.FromSeconds(2))).GetProperty("action").GetString());
    }

    [TestMethod]
    public async Task McpRoutingKeepsRawIdentityAndRedactsDisplayCopies()
    {
        var redactor = new SecretRedactor();
        using IDisposable nameSecret = redactor.RegisterSecret("docs-private");
        using IDisposable environmentSecret = redactor.RegisterSecret("environment-private");
        var connection = new RecordingConnection
        {
            InitializeResponse = JsonSerializer.SerializeToElement(new { userAgent = "codex-cli/0.171.0" }),
        };
        connection.AsyncHandler = (method, _, _) => method == "mcpServer/oauth/login"
            ? Task.FromResult(JsonSerializer.SerializeToElement(new { loginId = "login-private", authorizationUrl = "https://login.example.test/authorize" }))
            : Task.FromResult(JsonSerializer.SerializeToElement(new { }));
        await using var service = CreateService(redactor: redactor);
        await service.InitializeAsync(connection, Options(experimentalApi: true), CancellationToken.None);

        McpOAuthLoginStatus login = await service.StartMcpOAuthLoginAsync(new StartMcpOAuthLoginRequest
        {
            ServerName = "docs-private",
            ThreadId = "thread-1",
            HasRegistrationIdentity = true,
            EnvironmentId = "environment-private",
        }, CancellationToken.None);

        JsonElement parameters = JsonSerializer.SerializeToElement(connection.Requests.Single(request => request.Method == "mcpServer/oauth/login").Parameters);
        Assert.AreEqual("docs-private", parameters.GetProperty("name").GetString());
        Assert.AreEqual("environment-private", parameters.GetProperty("environmentId").GetString());
        Assert.AreEqual("docs-private", login.ServerName);
        Assert.AreEqual("environment-private", login.EnvironmentId);
        Assert.AreEqual("[REDACTED]", login.DisplayServerName);
        Assert.AreEqual("[REDACTED]", login.DisplayEnvironmentId);
    }

    private static RecordingConnection EnvironmentIdentityConnection(object first, object? second = null, string version = "0.171.0")
    {
        var connection = new RecordingConnection
        {
            InitializeResponse = JsonSerializer.SerializeToElement(new { userAgent = $"codex-cli/{version}" }),
        };
        object[] registrations = second is null ? [first] : [first, second];
        connection.AsyncHandler = (method, parameters, _) =>
        {
            if (method == "mcpServerStatus/list")
            {
                var servers = registrations.Select(registration =>
                {
                    JsonElement registrationJson = JsonSerializer.SerializeToElement(registration);
                    return new Dictionary<string, object?>(StringComparer.Ordinal)
                    {
                        ["name"] = registrationJson.GetProperty("name").GetString(),
                        ["environmentId"] = registrationJson.GetProperty("environmentId").ValueKind == JsonValueKind.Null
                            ? null
                            : registrationJson.GetProperty("environmentId").GetString(),
                        ["authStatus"] = "oAuth",
                        ["tools"] = new { search = new { name = "search", inputSchema = new { type = "object" } } },
                        ["resources"] = new[] { new { name = "root", uri = "file:///root" } },
                        ["resourceTemplates"] = Array.Empty<object>(),
                    };
                }).ToArray();
                return Task.FromResult(JsonSerializer.SerializeToElement(new { data = servers }));
            }

            if (method == "mcpServer/oauth/login")
            {
                JsonElement request = JsonSerializer.SerializeToElement(parameters);
                string? environmentId = request.TryGetProperty("environmentId", out JsonElement environment)
                    && environment.ValueKind == JsonValueKind.String ? environment.GetString() : null;
                return Task.FromResult(JsonSerializer.SerializeToElement(new
                {
                    loginId = environmentId ?? "thread-wide",
                    authorizationUrl = "https://login.example.test/authorize",
                }));
            }

            return Task.FromResult(JsonSerializer.SerializeToElement(new { }));
        };
        return connection;
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
        await connection.EmitNotificationAsync("mcpServer/oauthLogin/completed", new { name = "example-mcp", success = true, loginId = "login-1" });
        finishLogin.TrySetResult(JsonSerializer.SerializeToElement(new { authorizationUrl = "https://login.example.test/old", loginId = "login-1" }));
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
    public async Task McpCompletionWithMatchingLoginId_CompletesTheAttempt()
    {
        (RecordingConnection connection, CodexSessionService service, ConcurrentQueue<InteractionAuthStatus> statuses) =
            await StartMcpLoginWithLoginIdAsync("login-1");
        await using (service)
        {
            await connection.EmitNotificationAsync("mcpServer/oauthLogin/completed", new { name = "example-mcp", success = true, loginId = "login-1" });

            McpServerAuthStatus status = LastMcpStatus(statuses);
            Assert.AreEqual(InteractionAuthState.Authenticated, status.State);
            Assert.IsNull(status.OpenAuthorizationActionId);
        }
    }

    [TestMethod]
    public async Task McpCompletionWithStaleLoginId_DoesNotCompleteTheCurrentAttempt()
    {
        (RecordingConnection connection, CodexSessionService service, ConcurrentQueue<InteractionAuthStatus> statuses) =
            await StartMcpLoginWithLoginIdAsync("login-2");
        await using (service)
        {
            int before = statuses.Count;
            await connection.EmitNotificationAsync("mcpServer/oauthLogin/completed", new { name = "example-mcp", success = false, loginId = "login-1" });

            Assert.AreEqual(before, statuses.Count, "A stale completion must not publish a new state.");
            McpServerAuthStatus status = LastMcpStatus(statuses);
            Assert.AreEqual(InteractionAuthState.LoginPending, status.State);
            Assert.IsNotNull(status.OpenAuthorizationActionId, "The current attempt keeps its authorization link.");
        }
    }

    [TestMethod]
    public async Task McpCompletionWithLoginIdAfterTheAttemptCompleted_DoesNotOverwriteStatus()
    {
        (RecordingConnection connection, CodexSessionService service, ConcurrentQueue<InteractionAuthStatus> statuses) =
            await StartMcpLoginWithLoginIdAsync("login-1");
        await using (service)
        {
            await connection.EmitNotificationAsync("mcpServer/oauthLogin/completed", new { name = "example-mcp", success = true, loginId = "login-1" });
            Assert.AreEqual(InteractionAuthState.Authenticated, LastMcpStatus(statuses).State);
            int before = statuses.Count;

            await connection.EmitNotificationAsync("mcpServer/oauthLogin/completed", new { name = "example-mcp", success = false, loginId = "login-1" });

            Assert.AreEqual(before, statuses.Count, "A duplicate completion is dropped.");
            Assert.AreEqual(InteractionAuthState.Authenticated, LastMcpStatus(statuses).State);
        }
    }

    [TestMethod]
    public async Task McpCompletionRacingTheLoginResponse_AlwaysCompletesTheAttempt()
    {
        for (int attempt = 0; attempt < 100; attempt++)
        {
            var connection = new RecordingConnection();
            var loginStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var finishLogin = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
            connection.AsyncHandler = (method, _, _) => method == "mcpServer/oauth/login"
                ? StartPendingLoginAsync()
                : Task.FromResult(JsonSerializer.SerializeToElement(new { }));
            await using var service = CreateService(_ => { });
            var statuses = new ConcurrentQueue<InteractionAuthStatus>();
            service.InteractionAuthStatusChanged += (status, _) =>
            {
                statuses.Enqueue(status);
                return Task.CompletedTask;
            };
            await service.InitializeAsync(connection, Options(), CancellationToken.None);

            Task<McpOAuthLoginStatus> loginTask = service.StartMcpOAuthLoginAsync(
                new StartMcpOAuthLoginRequest { ServerName = "example-mcp" },
                CancellationToken.None);
            await loginStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Task notification = Task.Run(() => connection.EmitNotificationAsync(
                "mcpServer/oauthLogin/completed",
                new { name = "example-mcp", success = true, loginId = "login-1" }));
            Task response = Task.Run(() => finishLogin.TrySetResult(JsonSerializer.SerializeToElement(
                new { authorizationUrl = "https://login.example.test/authorize", loginId = "login-1" })));
            await Task.WhenAll(notification, response, loginTask).WaitAsync(TimeSpan.FromSeconds(5));

            Assert.AreEqual(InteractionAuthState.Authenticated, LastMcpStatus(statuses).State, $"attempt {attempt}");

            Task<JsonElement> StartPendingLoginAsync()
            {
                loginStarted.TrySetResult();
                return finishLogin.Task;
            }
        }
    }

    [TestMethod]
    public async Task McpLoginResponseWithInvalidLoginId_DoesNotFallBackToServerName()
    {
        var connection = new RecordingConnection();
        connection.AsyncHandler = (method, _, _) => method == "mcpServer/oauth/login"
            ? Task.FromResult(JsonSerializer.SerializeToElement(new { authorizationUrl = "https://login.example.test/authorize", loginId = 42 }))
            : Task.FromResult(JsonSerializer.SerializeToElement(new { }));
        await using var service = CreateService(_ => { });
        var statuses = new ConcurrentQueue<InteractionAuthStatus>();
        service.InteractionAuthStatusChanged += (status, _) =>
        {
            statuses.Enqueue(status);
            return Task.CompletedTask;
        };
        await service.InitializeAsync(connection, Options(), CancellationToken.None);
        await service.StartMcpOAuthLoginAsync(new StartMcpOAuthLoginRequest { ServerName = "example-mcp" }, CancellationToken.None);

        await connection.EmitNotificationAsync("mcpServer/oauthLogin/completed", new { name = "example-mcp", success = true, loginId = "other-attempt" });

        Assert.AreEqual(InteractionAuthState.LoginPending, LastMcpStatus(statuses).State);
    }

    [TestMethod]
    public async Task McpCompletionWithInvalidLoginId_IsIgnored()
    {
        (RecordingConnection connection, CodexSessionService service, ConcurrentQueue<InteractionAuthStatus> statuses) =
            await StartMcpLoginWithLoginIdAsync("login-1");
        await using (service)
        {
            int before = statuses.Count;
            await connection.EmitNotificationAsync("mcpServer/oauthLogin/completed", new { name = "example-mcp", success = true, loginId = 42 });

            Assert.AreEqual(before, statuses.Count);
            Assert.AreEqual(InteractionAuthState.LoginPending, LastMcpStatus(statuses).State);
        }
    }

    [TestMethod]
    [DataRow("login-1", "login-1", InteractionAuthState.Authenticated)]
    [DataRow("login-0", "login-1", InteractionAuthState.LoginPending)]
    // A response without loginId cannot be matched, so no completion is applied by server name.
    [DataRow("login-0", null, InteractionAuthState.LoginPending)]
    public async Task McpCompletionBeforeLoginResponse_IsAppliedOnlyForTheSameLoginId(string notifiedLoginId, string? responseLoginId, InteractionAuthState expected)
    {
        var connection = new RecordingConnection();
        var loginStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finishLogin = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        connection.AsyncHandler = (method, _, _) => method == "mcpServer/oauth/login"
            ? StartPendingLoginAsync()
            : Task.FromResult(JsonSerializer.SerializeToElement(new { }));
        await using var service = CreateService(_ => { });
        await service.InitializeAsync(connection, Options(), CancellationToken.None);

        Task<McpOAuthLoginStatus> loginTask = service.StartMcpOAuthLoginAsync(
            new StartMcpOAuthLoginRequest { ServerName = "example-mcp", ThreadId = "thread-1" },
            CancellationToken.None);
        await loginStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await connection.EmitNotificationAsync("mcpServer/oauthLogin/completed", new { name = "example-mcp", success = true, loginId = notifiedLoginId });
        finishLogin.TrySetResult(JsonSerializer.SerializeToElement(new { authorizationUrl = "https://login.example.test/authorize", loginId = responseLoginId }));
        McpOAuthLoginStatus loginStatus = await loginTask.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.AreEqual(expected, loginStatus.State);
        Assert.AreEqual(expected == InteractionAuthState.LoginPending, loginStatus.OpenAuthorizationActionId is not null);

        Task<JsonElement> StartPendingLoginAsync()
        {
            loginStarted.TrySetResult();
            return finishLogin.Task;
        }
    }

    private static async Task<(RecordingConnection Connection, CodexSessionService Service, ConcurrentQueue<InteractionAuthStatus> Statuses)> StartMcpLoginWithLoginIdAsync(string loginId)
    {
        var connection = new RecordingConnection();
        connection.AsyncHandler = (method, _, _) => method == "mcpServer/oauth/login"
            ? Task.FromResult(JsonSerializer.SerializeToElement(new { authorizationUrl = "https://login.example.test/authorize", loginId }))
            : Task.FromResult(JsonSerializer.SerializeToElement(new { }));
        CodexSessionService service = CreateService(_ => { });
        var statuses = new ConcurrentQueue<InteractionAuthStatus>();
        service.InteractionAuthStatusChanged += (status, _) =>
        {
            statuses.Enqueue(status);
            return Task.CompletedTask;
        };
        await service.InitializeAsync(connection, Options(), CancellationToken.None);
        McpOAuthLoginStatus started = await service.StartMcpOAuthLoginAsync(
            new StartMcpOAuthLoginRequest { ServerName = "example-mcp" },
            CancellationToken.None);
        Assert.AreEqual(InteractionAuthState.LoginPending, started.State);
        return (connection, service, statuses);
    }

    private static McpServerAuthStatus LastMcpStatus(ConcurrentQueue<InteractionAuthStatus> statuses) =>
        statuses.Last().McpServers.Single(server => server.ServerName == "example-mcp");

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

    private static CodexSessionService CreateService(Action<Uri>? authorizationUrlOpener = null, ISecretRedactor? redactor = null)
        => new(
            new ApprovalPolicyEngine(new PathAccessPolicy()),
            redactor ?? new SecretRedactor(),
            null,
            null,
            null,
            null,
            authorizationUrlOpener);

    private static WorkerOptions Options(bool experimentalApi = false) => new()
    {
        WorkingDirectory = Path.GetTempPath(),
        ExtensionVersion = "test",
        ExperimentalApi = experimentalApi,
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

        public JsonElement? InitializeResponse { get; set; }

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

            JsonElement response = AsyncHandler is not null
                ? await AsyncHandler(method, parameters, cancellationToken).ConfigureAwait(false)
                : JsonSerializer.SerializeToElement(new { });
            if (method == "initialize" && InitializeResponse is JsonElement initializeResponse)
            {
                return initializeResponse.Clone();
            }

            return method == "initialize" && response.ValueKind == JsonValueKind.Object && !response.EnumerateObject().Any()
                ? JsonSerializer.SerializeToElement(new { userAgent = $"codex-cli/{SupportedCodexVersions.Target}" })
                : response;
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
