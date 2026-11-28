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
    public async Task EnterpriseMcpOAuthCompletionUsesOnlyPublicStatusAndRegistration()
    {
        const string privateToken = "synthetic-id-token-do-not-project";
        const string privateHeader = "synthetic-telemetry-header-do-not-project";
        var connection = EnvironmentIdentityConnection(
            new { name = "docs", environmentId = "managed" },
            new { name = "docs", environmentId = "other" },
            oauthLoginIdFactory: static environmentId => $"oauth-login-{environmentId}");
        var statuses = new ConcurrentQueue<InteractionAuthStatus>();
        var events = new ConcurrentQueue<ConversationEvent>();
        await using var service = CreateService();
        service.InteractionAuthStatusChanged += (status, _) =>
        {
            statuses.Enqueue(status);
            return Task.CompletedTask;
        };
        service.ConversationEventReceived += (value, _) =>
        {
            events.Enqueue(value);
            return Task.CompletedTask;
        };

        await service.InitializeAsync(connection, Options(experimentalApi: true), CancellationToken.None);
        McpOAuthLoginStatus pending = await service.StartMcpOAuthLoginAsync(
            new StartMcpOAuthLoginRequest
            {
                ServerName = "docs",
                ThreadId = "thread-1",
                HasRegistrationIdentity = true,
                EnvironmentId = "managed",
            },
            CancellationToken.None);
        Assert.AreEqual(InteractionAuthState.LoginPending, pending.State);
        Assert.IsNotNull(pending.OpenAuthorizationActionId);
        int statusCountBeforeMismatch = statuses.Count;
        McpServerAuthStatus pendingServerStatus = statuses.Last().McpServers.Single();
        Assert.AreEqual(pending.OpenAuthorizationActionId, pendingServerStatus.OpenAuthorizationActionId);

        await connection.EmitNotificationAsync("mcpServer/oauthLogin/completed", new
        {
            name = "docs",
            threadId = "thread-1",
            environmentId = "managed",
            // This distinct ID belongs to the other same-named registration.
            loginId = "oauth-login-other",
            success = true,
            idToken = privateToken,
            headers = new { authorization = privateHeader },
        });

        Assert.AreEqual(statusCountBeforeMismatch, statuses.Count,
            "A completion for another OAuth login must not publish a status update.");
        McpServerAuthStatus stillPending = statuses.Last().McpServers.Single();
        Assert.AreEqual(InteractionAuthState.LoginPending, stillPending.State,
            "A completion for another OAuth login must not complete this registration's pending action.");
        Assert.AreEqual(pending.OpenAuthorizationActionId, stillPending.OpenAuthorizationActionId,
            "A mismatched completion must not clear the pending authorization action.");

        // A CLI-owned token exchange ends with public status and contains no refresh token.
        await connection.EmitNotificationAsync("mcpServer/oauthLogin/completed", new
        {
            name = "docs",
            threadId = "thread-1",
            environmentId = "managed",
            loginId = "oauth-login-managed",
            success = true,
            idToken = privateToken,
            headers = new { authorization = privateHeader },
        });

        McpServerAuthStatus completed = statuses.Last().McpServers.Single();
        Assert.AreEqual(InteractionAuthState.Authenticated, completed.State);
        Assert.IsNull(completed.OpenAuthorizationActionId);
        Assert.AreEqual("managed", completed.EnvironmentId);
        Assert.AreEqual("thread-1", completed.ThreadId);
        Assert.AreEqual("docs", completed.ServerName);
        Assert.AreEqual(1, connection.Requests.Count(request => request.Method == "mcpServer/oauth/login"));
        string projected = JsonSerializer.Serialize(new { Statuses = statuses.ToArray(), Events = events.ToArray() });
        Assert.IsFalse(projected.Contains(privateToken, StringComparison.Ordinal));
        Assert.IsFalse(projected.Contains(privateHeader, StringComparison.Ordinal));
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
    public async Task McpEnvironmentIdentity_Tier171WithoutExperimentalCapabilityUsesUnprimedLegacyLiteralRoute()
    {
        var connection = EnvironmentIdentityConnection(new { name = "docs", environmentId = "ignored" });
        await using var service = CreateService();
        await service.InitializeAsync(connection, Options(experimentalApi: false), CancellationToken.None);

        await service.StartMcpOAuthLoginAsync(new StartMcpOAuthLoginRequest { ServerName = "docs" }, CancellationToken.None);

        JsonElement parameters = JsonSerializer.SerializeToElement(connection.Requests.Single(request => request.Method == "mcpServer/oauth/login").Parameters);
        Assert.AreEqual("docs", parameters.GetProperty("name").GetString(),
            "With the negotiated capability disabled, Tier 171 uses the existing literal-name route even without inventory priming.");
        Assert.IsFalse(parameters.TryGetProperty("environmentId", out _));
        Assert.IsFalse(connection.Requests.Any(request => request.Method == "mcpServerStatus/list"),
            "Capability-off legacy routing must not issue the 0.171 environment inventory RPC.");
    }

    [TestMethod]
    public async Task McpServerInventory_ExposesRoutingIdentityOnlyForNegotiatedTier171()
    {
        foreach ((string version, bool experimentalApi, bool identityEnabled) in new[]
        {
            ("0.169.0", true, false),
            ("0.170.2", true, false),
            ("0.171.0", false, false),
            ("0.171.0", true, true),
        })
        {
            var redactor = new SecretRedactor();
            using IDisposable nameSecret = redactor.RegisterSecret("docs-private");
            using IDisposable environmentSecret = redactor.RegisterSecret("environment-private");
            var connection = EnvironmentIdentityConnection(
                new { name = "docs-private", environmentId = "environment-private" },
                version: version);
            await using var service = CreateService(redactor: redactor);
            await service.InitializeAsync(connection, Options(experimentalApi), CancellationToken.None);

            McpServerStatusInfo server = (await service.ListMcpServersAsync(new ListMcpServersRequest(), CancellationToken.None)).Servers.Single();

            Assert.AreEqual(identityEnabled ? "docs-private" : "[REDACTED]", server.Name, $"Routing identity follows the negotiated capability for CLI {version}.");
            Assert.AreEqual(identityEnabled, server.HasRegistrationIdentity);
            Assert.AreEqual(identityEnabled ? "environment-private" : null, server.EnvironmentId);
            Assert.AreEqual("[REDACTED]", server.DisplayName);
            Assert.AreEqual(identityEnabled ? "[REDACTED]" : null, server.DisplayEnvironmentId);
        }
    }

    [TestMethod]
    public async Task McpServerInventory_LegacyIgnoresUnusedInvalidEnvironmentId()
    {
        var connection = new RecordingConnection
        {
            InitializeResponse = JsonSerializer.SerializeToElement(new { userAgent = "codex-cli/0.170.2" }),
            AsyncHandler = (method, _, _) => method == "mcpServerStatus/list"
                ? Task.FromResult(JsonSerializer.SerializeToElement(new
                {
                    data = new[] { new { name = "docs", environmentId = "environment\u001fbad" } },
                }))
                : Task.FromResult(JsonSerializer.SerializeToElement(new { })),
        };
        await using var service = CreateService();
        await service.InitializeAsync(connection, Options(experimentalApi: true), CancellationToken.None);

        McpServerStatusInfo server = (await service.ListMcpServersAsync(new ListMcpServersRequest(), CancellationToken.None)).Servers.Single();

        Assert.AreEqual("docs", server.Name);
        Assert.IsFalse(server.HasRegistrationIdentity);
        Assert.IsNull(server.EnvironmentId);
    }

    [TestMethod]
    public async Task McpAuthStatusContracts_RedactLegacyNamesForPendingCompletionAndReauthentication()
    {
        foreach ((string version, bool experimentalApi, bool identityEnabled) in new[]
        {
            ("0.169.0", true, false),
            ("0.170.2", true, false),
            ("0.171.0", false, false),
            ("0.171.0", true, true),
        })
        {
            var redactor = new SecretRedactor();
            using IDisposable nameSecret = redactor.RegisterSecret("docs-private");
            var connection = new RecordingConnection
            {
                InitializeResponse = JsonSerializer.SerializeToElement(new { userAgent = $"codex-cli/{version}" }),
                AsyncHandler = (method, _, _) => method == "mcpServer/oauth/login"
                    ? Task.FromResult(JsonSerializer.SerializeToElement(new { loginId = "login-private", authorizationUrl = "https://login.example.test/authorize" }))
                    : Task.FromResult(JsonSerializer.SerializeToElement(new { })),
            };
            var observed = new ConcurrentQueue<InteractionAuthStatus>();
            await using var service = CreateService(redactor: redactor);
            service.InteractionAuthStatusChanged += (status, _) =>
            {
                observed.Enqueue(status);
                return Task.CompletedTask;
            };
            await service.InitializeAsync(connection, Options(experimentalApi), CancellationToken.None);
            await PrimeLegacyMcpServerNameAsync(service, connection, "docs-private");

            McpOAuthLoginStatus pending = await service.StartMcpOAuthLoginAsync(
                new StartMcpOAuthLoginRequest { ServerName = identityEnabled ? "docs-private" : "[REDACTED]" },
                CancellationToken.None);
            Assert.AreEqual("docs-private", JsonSerializer.SerializeToElement(connection.Requests.Single(request => request.Method == "mcpServer/oauth/login").Parameters).GetProperty("name").GetString());
            Assert.AreEqual(identityEnabled ? "docs-private" : "[REDACTED]", pending.ServerName);
            Assert.AreEqual("[REDACTED]", pending.DisplayServerName);
            Assert.AreEqual(identityEnabled ? "docs-private" : "[REDACTED]", observed.Last().McpServers.Single().ServerName);
            Assert.AreEqual("[REDACTED]", observed.Last().McpServers.Single().DisplayServerName,
                "Routing identity may cross the Worker contract, while the UI display copy remains redacted.");

            await connection.EmitNotificationAsync("mcpServer/oauthLogin/completed", new { name = "docs-private", success = true, loginId = "login-private" });
            Assert.AreEqual(InteractionAuthState.Authenticated, observed.Last().McpServers.Single().State);
            Assert.AreEqual(identityEnabled ? "docs-private" : "[REDACTED]", observed.Last().McpServers.Single().ServerName);
            Assert.AreEqual("[REDACTED]", observed.Last().McpServers.Single().DisplayServerName);

            await connection.EmitNotificationAsync("mcpServer/startupStatus/updated", new
            {
                name = "docs-private",
                status = "failed",
                failureReason = "reauthenticationRequired",
            });
            Assert.AreEqual(InteractionAuthState.ReauthenticationRequired, observed.Last().McpServers.Single().State);
            Assert.AreEqual(identityEnabled ? "docs-private" : "[REDACTED]", observed.Last().McpServers.Single().ServerName);
            Assert.AreEqual("[REDACTED]", observed.Last().McpServers.Single().DisplayServerName);
        }
    }

    [TestMethod]
    public async Task McpStartupReauthenticationObservedBeforeLoginResponseSurvivesEarlySuccessCompletion()
    {
        var connection = new RecordingConnection
        {
            InitializeResponse = JsonSerializer.SerializeToElement(new { userAgent = "codex-cli/0.171.0" }),
        };
        var loginStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var loginResponse = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
#pragma warning disable VSTHRD003 // The test-controlled response represents the pending OAuth RPC.
        connection.AsyncHandler = (method, _, _) =>
        {
            if (method == "mcpServer/oauth/login")
            {
                loginStarted.TrySetResult();
                return loginResponse.Task;
            }

            return Task.FromResult(JsonSerializer.SerializeToElement(new { }));
        };
#pragma warning restore VSTHRD003
        var statuses = new ConcurrentQueue<InteractionAuthStatus>();
        await using var service = CreateService();
        service.InteractionAuthStatusChanged += (status, _) =>
        {
            statuses.Enqueue(status);
            return Task.CompletedTask;
        };
        await service.InitializeAsync(connection, Options(experimentalApi: true), CancellationToken.None);

        Task<McpOAuthLoginStatus> login = service.StartMcpOAuthLoginAsync(new StartMcpOAuthLoginRequest
        {
            ServerName = "docs", ThreadId = "thread-1", HasRegistrationIdentity = true, EnvironmentId = "env-a",
        }, CancellationToken.None);
#pragma warning disable VSTHRD003 // The signal is completed by the test-controlled OAuth RPC handler.
        await loginStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
#pragma warning restore VSTHRD003
        await connection.EmitNotificationAsync("mcpServer/startupStatus/updated", new
        {
            name = "docs", threadId = "thread-1", environmentId = "env-a", status = "failed", failureReason = "reauthenticationRequired",
        });
        await connection.EmitNotificationAsync("mcpServer/oauthLogin/completed", new
        {
            name = "docs", threadId = "thread-1", environmentId = "env-a", success = true, loginId = "login-a",
        });

        loginResponse.TrySetResult(JsonSerializer.SerializeToElement(new
        {
            loginId = "login-a", authorizationUrl = "https://login.example.test/authorize",
        }));
#pragma warning disable VSTHRD003 // This task is the test-controlled OAuth RPC started above.
        await login.WaitAsync(TimeSpan.FromSeconds(2));
#pragma warning restore VSTHRD003

        Assert.AreEqual(InteractionAuthState.ReauthenticationRequired, statuses.Last().McpServers.Single().State);
    }

    [TestMethod]
    public async Task McpStartupFailureIncludesRedactedBoundedServerError()
    {
        const string secret = "startup-private-token";
        var redactor = new SecretRedactor();
        using IDisposable registeredSecret = redactor.RegisterSecret(secret);
        var connection = EnvironmentIdentityConnection(new { name = "docs", environmentId = "env-a" });
        var statuses = new ConcurrentQueue<InteractionAuthStatus>();
        await using var service = CreateService(redactor: redactor);
        service.InteractionAuthStatusChanged += (status, _) =>
        {
            statuses.Enqueue(status);
            return Task.CompletedTask;
        };
        await service.InitializeAsync(connection, Options(experimentalApi: true), CancellationToken.None);
        _ = await service.ListMcpServersAsync(new ListMcpServersRequest(), CancellationToken.None);

        await connection.EmitNotificationAsync("mcpServer/startupStatus/updated", new
        {
            name = "docs",
            environmentId = "env-a",
            status = "failed",
            error = $"startup failed: {secret} {new string('x', 2_100)}",
        });

        string message = statuses.Last().McpServers.Single().Message ?? string.Empty;
        string unboundedServerMessage = $"startup failed: [REDACTED] {new string('x', 2_100)}";
        string expectedServerMessage = unboundedServerMessage[..2_047] + "…";
        string expectedMessage = $"{LocalizedStrings.Get("Diagnostics.McpStartup.Failed")}\n{LocalizedStrings.Format("Diagnostics.ServerMessage", expectedServerMessage)}";
        Assert.AreEqual(expectedMessage, message);
        Assert.AreEqual(2_048, expectedServerMessage.Length);
        Assert.IsFalse(message.Contains(secret, StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task McpStartupStatusWithoutEnvironmentIdDoesNotResolveToScopedOAuthRegistration()
    {
        var connection = EnvironmentIdentityConnection(new { name = "docs", environmentId = "env-a" });
        connection.AsyncHandler = (method, _, _) => method == "mcpServer/oauth/login"
            ? Task.FromResult(JsonSerializer.SerializeToElement(new { loginId = "login-a", authorizationUrl = "https://login.example.test/authorize" }))
            : Task.FromResult(JsonSerializer.SerializeToElement(new { }));
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
            ServerName = "docs", ThreadId = "thread-1", HasRegistrationIdentity = true, EnvironmentId = "env-a",
        }, CancellationToken.None);
        await connection.EmitNotificationAsync("mcpServer/startupStatus/updated", new
        {
            name = "docs", threadId = "thread-1", status = "ready",
        });

        McpServerAuthStatus scopedStatus = statuses.Last().McpServers.Single(status => status.EnvironmentId == "env-a");
        Assert.AreEqual(InteractionAuthState.LoginPending, scopedStatus.State,
            "A notification without an environment identity cannot be inferred to belong to an existing scoped registration.");
    }

    [TestMethod]
    public async Task McpStartupStatusWithoutThreadIdDoesNotResolveToThreadScopedRegistration()
    {
        var connection = EnvironmentIdentityConnection(new { name = "docs", environmentId = (string?)null });
        await using var service = CreateService();
        await service.InitializeAsync(connection, Options(experimentalApi: true), CancellationToken.None);
        await connection.EmitNotificationAsync("mcpServer/startupStatus/updated", new
        {
            name = "docs", threadId = "thread-a", status = "failed",
        });

        await connection.EmitNotificationAsync("mcpServer/startupStatus/updated", new
        {
            name = "docs", status = "ready",
        });

        InteractionAuthStatus status = await service.ReadGatewayOAuthAsync(CancellationToken.None);
        Assert.AreEqual(InteractionAuthState.Failed,
            status.McpServers.Single(server => server.ThreadId == "thread-a").State,
            "A threadless notification cannot use a unique same-name candidate from another thread.");
    }

    [TestMethod]
    public async Task McpStartupStatusWithEnvironmentButWithoutThreadIdDoesNotResolveScopedOAuthOperation()
    {
        var connection = EnvironmentIdentityConnection(new { name = "docs", environmentId = "env-a" });
        connection.AsyncHandler = (method, _, _) => method == "mcpServer/oauth/login"
            ? Task.FromResult(JsonSerializer.SerializeToElement(new { loginId = "login-a", authorizationUrl = "https://login.example.test/authorize" }))
            : Task.FromResult(JsonSerializer.SerializeToElement(new { }));
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
            ServerName = "docs", ThreadId = "thread-a", HasRegistrationIdentity = true, EnvironmentId = "env-a",
        }, CancellationToken.None);

        await connection.EmitNotificationAsync("mcpServer/startupStatus/updated", new
        {
            name = "docs", environmentId = "env-a", status = "ready",
        });
        await connection.EmitNotificationAsync("mcpServer/startupStatus/updated", new
        {
            name = "docs", environmentId = "env-b", status = "ready",
        });
        await connection.EmitNotificationAsync("mcpServer/startupStatus/updated", new
        {
            name = "docs", status = "ready",
        });

        McpServerAuthStatus scopedStatus = statuses.Last().McpServers.Single(server => server.ThreadId == "thread-a");
        Assert.AreEqual(InteractionAuthState.LoginPending, scopedStatus.State,
            "An environment identity without the required thread context cannot bind to a thread-scoped OAuth operation.");

        int previousStatusCount = statuses.Count;
        await connection.EmitNotificationAsync("mcpServer/startupStatus/updated", new
        {
            name = "docs", threadId = "thread-a", environmentId = "env-a", status = "ready",
        });
        Assert.IsTrue(statuses.Count > previousStatusCount,
            "A valid thread+environment notification is the positive control proving the notification handler ran.");
        Assert.AreEqual(InteractionAuthState.Authenticated,
            statuses.Last().McpServers.Single(server => server.ThreadId == "thread-a").State);
    }

    [TestMethod]
    public async Task McpOAuthPreflightOnDetachedConnection_DoesNotContinueAfterInventoryAwait()
    {
        var firstConnection = new RecordingConnection
        {
            InitializeResponse = JsonSerializer.SerializeToElement(new { userAgent = "codex-cli/0.171.0" }),
        };
        var inventoryStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var inventoryResponse = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
#pragma warning disable VSTHRD003 // The test-controlled inventory response represents a pending app-server RPC.
        firstConnection.AsyncHandler = (method, _, _) =>
        {
            if (method == "mcpServerStatus/list")
            {
                inventoryStarted.TrySetResult();
                return inventoryResponse.Task;
            }

            return Task.FromResult(JsonSerializer.SerializeToElement(new { }));
        };
#pragma warning restore VSTHRD003
        var secondConnection = new RecordingConnection
        {
            InitializeResponse = JsonSerializer.SerializeToElement(new { userAgent = "codex-cli/0.171.0" }),
        };
        await using var service = CreateService();
        await service.InitializeAsync(firstConnection, Options(experimentalApi: true), CancellationToken.None);
        Task<McpOAuthLoginStatus> login = service.StartMcpOAuthLoginAsync(
            new StartMcpOAuthLoginRequest { ServerName = "docs", ThreadId = "thread-a" }, CancellationToken.None);
#pragma warning disable VSTHRD003 // The signal is completed by the test-controlled inventory handler.
        await inventoryStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
#pragma warning restore VSTHRD003

        await service.InitializeAsync(secondConnection, Options(experimentalApi: true), CancellationToken.None);
        inventoryResponse.TrySetResult(JsonSerializer.SerializeToElement(new
        {
            data = new[] { new { name = "docs", environmentId = "environment-a" } },
        }));
#pragma warning disable VSTHRD003 // This task represents the preflight operation started above.
        await Assert.ThrowsExactlyAsync<JsonRpcConnectionClosedException>(() => login);
#pragma warning restore VSTHRD003

        Assert.IsFalse(firstConnection.Requests.Any(request => request.Method == "mcpServer/oauth/login"));
        Assert.IsFalse(secondConnection.Requests.Any(request => request.Method == "mcpServer/oauth/login"));
        Assert.AreEqual(0, (await service.ReadGatewayOAuthAsync(CancellationToken.None)).McpServers.Count);
    }

    [TestMethod]
    public async Task McpOAuthCompletionPrecedence_PreservesReauthenticationAcrossResponseAndNotificationOrders()
    {
        foreach (string order in new[]
        {
            "response-reauth-success",
            "success-reauth-response",
            "response-reauth",
            "failure-reauth-response",
            "starting-success-response",
        })
        {
            var connection = new RecordingConnection
            {
                InitializeResponse = JsonSerializer.SerializeToElement(new { userAgent = "codex-cli/0.171.0" }),
            };
            var loginStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var loginResponse = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
#pragma warning disable VSTHRD003 // The test-controlled response represents the pending OAuth RPC.
            connection.AsyncHandler = (method, _, _) =>
            {
                if (method == "mcpServer/oauth/login")
                {
                    loginStarted.TrySetResult();
                    return loginResponse.Task;
                }

                return Task.FromResult(JsonSerializer.SerializeToElement(new { }));
            };
#pragma warning restore VSTHRD003
            var statuses = new ConcurrentQueue<InteractionAuthStatus>();
            await using var service = CreateService();
            service.InteractionAuthStatusChanged += (status, _) =>
            {
                statuses.Enqueue(status);
                return Task.CompletedTask;
            };
            await service.InitializeAsync(connection, Options(experimentalApi: true), CancellationToken.None);
            Task<McpOAuthLoginStatus> login = service.StartMcpOAuthLoginAsync(new StartMcpOAuthLoginRequest
            {
                ServerName = "docs", ThreadId = "thread-1", HasRegistrationIdentity = true, EnvironmentId = "env-a",
            }, CancellationToken.None);
#pragma warning disable VSTHRD003 // The signal is completed by the test-controlled OAuth RPC handler.
            await loginStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
#pragma warning restore VSTHRD003

            async Task CompleteResponseAsync()
            {
                loginResponse.TrySetResult(JsonSerializer.SerializeToElement(new
                {
                    loginId = "login-a", authorizationUrl = "https://login.example.test/authorize",
                }));
#pragma warning disable VSTHRD003 // The response task was started by the test above.
                await login.WaitAsync(TimeSpan.FromSeconds(2));
#pragma warning restore VSTHRD003
            }

            Task EmitStartupAsync(string status, string? failureReason = null)
                => connection.EmitNotificationAsync("mcpServer/startupStatus/updated", new
                {
                    name = "docs", threadId = "thread-1", environmentId = "env-a", status, failureReason,
                });

            Task EmitCompletionAsync(bool succeeded = true)
                => connection.EmitNotificationAsync("mcpServer/oauthLogin/completed", new
                {
                    name = "docs", threadId = "thread-1", environmentId = "env-a", success = succeeded, loginId = "login-a",
                });

            if (order.StartsWith("response-", StringComparison.Ordinal))
            {
                await CompleteResponseAsync();
                await EmitStartupAsync("failed", "reauthenticationRequired");
                if (order == "response-reauth-success")
                {
                    await EmitCompletionAsync();
                }
            }
            else
            {
                if (order == "starting-success-response")
                {
                    await EmitStartupAsync("starting");
                    await EmitCompletionAsync();
                }
                else if (order == "failure-reauth-response")
                {
                    await EmitCompletionAsync(succeeded: false);
                    await EmitStartupAsync("failed", "reauthenticationRequired");
                }
                else
                {
                    await EmitCompletionAsync();
                    await EmitStartupAsync("failed", "reauthenticationRequired");
                }

                await CompleteResponseAsync();
            }

            InteractionAuthState expected = order == "starting-success-response"
                ? InteractionAuthState.Authenticated
                : InteractionAuthState.ReauthenticationRequired;
            Assert.AreEqual(expected, statuses.Last().McpServers.Single().State,
                order + ": " + string.Join(" -> ", statuses.SelectMany(status => status.McpServers.Select(server => server.State.ToString()))));
        }
    }

    [TestMethod]
    public async Task McpLegacyOAuth_AllowsLiteralNameButNeverSendsUnresolvedRedactedPlaceholder()
    {
        foreach ((string version, bool allowsLegacyLiteral) in new[]
        {
            ("0.169.0", true), ("0.170.2", true), ("0.171.0", true),
        })
        {
            var connection = new RecordingConnection
            {
                InitializeResponse = JsonSerializer.SerializeToElement(new { userAgent = $"codex-cli/{version}" }),
                AsyncHandler = (method, _, _) => method == "mcpServer/oauth/login"
                    ? Task.FromResult(JsonSerializer.SerializeToElement(new { loginId = "literal-login" }))
                    : Task.FromResult(JsonSerializer.SerializeToElement(new { })),
            };
            await using var service = CreateService();
            await service.InitializeAsync(connection, Options(experimentalApi: false), CancellationToken.None);

            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => service.StartMcpOAuthLoginAsync(
                new StartMcpOAuthLoginRequest { ServerName = "[REDACTED]" }, CancellationToken.None), version);
            Assert.IsFalse(connection.Requests.Any(request => request.Method == "mcpServer/oauth/login"), version);

            if (allowsLegacyLiteral)
            {
                await service.StartMcpOAuthLoginAsync(new StartMcpOAuthLoginRequest { ServerName = "docs" }, CancellationToken.None);
                JsonElement parameters = JsonSerializer.SerializeToElement(connection.Requests.Single(request => request.Method == "mcpServer/oauth/login").Parameters);
                Assert.AreEqual("docs", parameters.GetProperty("name").GetString(), version);
            }
            else
            {
                await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => service.StartMcpOAuthLoginAsync(
                    new StartMcpOAuthLoginRequest { ServerName = "docs" }, CancellationToken.None), version);
            }
        }
    }

    [TestMethod]
    public async Task McpLegacyDisplayNameCache_ExpiresBeforeItCanResolveRedactedLoginName()
    {
        var redactor = new DistinctDisplayNameRedactor();
        var timeProvider = new MutableTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var connection = new RecordingConnection
        {
            InitializeResponse = JsonSerializer.SerializeToElement(new { userAgent = "codex-cli/0.170.2" }),
        };
        connection.AsyncHandler = (method, parameters, _) => method switch
        {
            "mcpServerStatus/list" => Task.FromResult(JsonSerializer.SerializeToElement(new
            {
                data = new[]
                {
                    new { name = JsonSerializer.SerializeToElement(parameters).GetProperty("threadId").GetString() == "thread-live" ? "docs-private-live" : "docs-private-expired" },
                },
                nextCursor = (string?)null,
            })),
            "mcpServer/oauth/login" => Task.FromResult(JsonSerializer.SerializeToElement(new
            {
                loginId = "legacy-live", authorizationUrl = "https://login.example.test/authorize",
            })),
            _ => Task.FromResult(JsonSerializer.SerializeToElement(new { })),
        };
        await using var service = new CodexSessionService(
            new ApprovalPolicyEngine(new PathAccessPolicy()), redactor, null, null, timeProvider);
        await service.InitializeAsync(connection, Options(experimentalApi: false), CancellationToken.None);
        await service.ListMcpServersAsync(new ListMcpServersRequest { ThreadId = "thread-live" }, CancellationToken.None);
        await service.ListMcpServersAsync(new ListMcpServersRequest { ThreadId = "thread-expired" }, CancellationToken.None);
        // The live login leaves a status-cache candidate. A distinct display label ensures the second assertion specifically exercises expiry of its untouched inventory mapping.
        await service.StartMcpOAuthLoginAsync(new StartMcpOAuthLoginRequest
        {
            ServerName = "[LIVE]", ThreadId = "thread-live",
        }, CancellationToken.None);
        JsonElement liveParameters = JsonSerializer.SerializeToElement(
            connection.Requests.Single(request => request.Method == "mcpServer/oauth/login").Parameters);
        Assert.AreEqual("docs-private-live", liveParameters.GetProperty("name").GetString(),
            "The fresh mapping resolves successfully before the clock advances.");
        timeProvider.Advance(TimeSpan.FromMinutes(11));

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => service.StartMcpOAuthLoginAsync(
            new StartMcpOAuthLoginRequest { ServerName = "[EXPIRED]", ThreadId = "thread-expired" }, CancellationToken.None));

        Assert.AreEqual(1, connection.Requests.Count(request => request.Method == "mcpServer/oauth/login"),
            "After TTL, the second mapping is rejected before another OAuth request reaches the CLI.");
    }

    private sealed class DistinctDisplayNameRedactor : ISecretRedactor
    {
        public string Redact(string? value) => value switch
        {
            "docs-private-live" => "[LIVE]",
            "docs-private-expired" => "[EXPIRED]",
            "[LIVE]" or "[EXPIRED]" => "[REDACTED]",
            _ => value ?? string.Empty,
        };
    }

    private sealed class SharedMcpDisplayNameRedactor : ISecretRedactor
    {
        public string Redact(string? value) => value is "docs-private-a" or "docs-private-b" or "[REDACTED]"
            ? "[REDACTED]"
            : value ?? string.Empty;
    }

    [TestMethod]
    public async Task McpLegacyDisplayNameCache_ResolvesRedactedLoginNameWithinLifetime()
    {
        var redactor = new SecretRedactor();
        using IDisposable nameSecret = redactor.RegisterSecret("docs-private");
        var timeProvider = new MutableTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var connection = new RecordingConnection
        {
            InitializeResponse = JsonSerializer.SerializeToElement(new { userAgent = "codex-cli/0.170.2" }),
        };
        connection.AsyncHandler = (method, _, _) => method switch
        {
            "mcpServerStatus/list" => Task.FromResult(JsonSerializer.SerializeToElement(new
            {
                data = new[] { new { name = "docs-private" } }, nextCursor = (string?)null,
            })),
            "mcpServer/oauth/login" => Task.FromResult(JsonSerializer.SerializeToElement(new
            {
                loginId = "legacy-login", authorizationUrl = "https://login.example.test/authorize",
            })),
            _ => Task.FromResult(JsonSerializer.SerializeToElement(new { })),
        };
        await using var service = new CodexSessionService(
            new ApprovalPolicyEngine(new PathAccessPolicy()), redactor, null, null, timeProvider);
        await service.InitializeAsync(connection, Options(experimentalApi: false), CancellationToken.None);
        await service.ListMcpServersAsync(new ListMcpServersRequest { ThreadId = "thread-1" }, CancellationToken.None);

        await service.StartMcpOAuthLoginAsync(new StartMcpOAuthLoginRequest
        {
            ServerName = "[REDACTED]", ThreadId = "thread-1",
        }, CancellationToken.None);

        JsonElement parameters = JsonSerializer.SerializeToElement(
            connection.Requests.Single(request => request.Method == "mcpServer/oauth/login").Parameters);
        Assert.AreEqual("docs-private", parameters.GetProperty("name").GetString(),
            "A unique, unexpired internal mapping resolves the display name before it reaches the CLI.");
    }

    [TestMethod]
    public async Task McpLegacyNameResolution_UsesCachedStatusThreadInsteadOfLegacyKeyThread()
    {
        var connection = new RecordingConnection
        {
            InitializeResponse = JsonSerializer.SerializeToElement(new { userAgent = "codex-cli/0.170.2" }),
        };
        connection.AsyncHandler = (method, parameters, _) =>
        {
            JsonElement request = JsonSerializer.SerializeToElement(parameters);
            if (method == "mcpServerStatus/list")
            {
                string rawName = request.GetProperty("threadId").GetString() == "thread-a" ? "docs-private-a" : "docs-private-b";
                return Task.FromResult(JsonSerializer.SerializeToElement(new
                {
                    data = new[] { new { name = rawName } },
                    nextCursor = (string?)null,
                }));
            }

            return Task.FromResult(JsonSerializer.SerializeToElement(new
            {
                loginId = "legacy-login",
                authorizationUrl = "https://login.example.test/authorize",
            }));
        };
        await using var service = CreateService(redactor: new SharedMcpDisplayNameRedactor());
        await service.InitializeAsync(connection, Options(experimentalApi: false), CancellationToken.None);

        await service.ListMcpServersAsync(new ListMcpServersRequest { ThreadId = "thread-a" }, CancellationToken.None);
        await service.StartMcpOAuthLoginAsync(
            new StartMcpOAuthLoginRequest { ServerName = "[REDACTED]", ThreadId = "thread-a" }, CancellationToken.None);
        await service.ListMcpServersAsync(new ListMcpServersRequest { ThreadId = "thread-b" }, CancellationToken.None);
        await service.StartMcpOAuthLoginAsync(
            new StartMcpOAuthLoginRequest { ServerName = "[REDACTED]", ThreadId = "thread-b" }, CancellationToken.None);

        JsonElement[] loginParameters = connection.Requests
            .Where(request => request.Method == "mcpServer/oauth/login")
            .Select(request => JsonSerializer.SerializeToElement(request.Parameters))
            .ToArray();
        CollectionAssert.AreEqual(new[] { "docs-private-a", "docs-private-b" },
            loginParameters.Select(parameters => parameters.GetProperty("name").GetString()).ToArray());
    }

    [TestMethod]
    public async Task McpLegacyDisplayNameCache_DoesNotPublishNamesUntilAllPagesValidate()
    {
        var redactor = new SecretRedactor();
        using IDisposable nameSecret = redactor.RegisterSecret("docs-private");
        var connection = new RecordingConnection
        {
            InitializeResponse = JsonSerializer.SerializeToElement(new { userAgent = "codex-cli/0.170.2" }),
        };
        connection.AsyncHandler = (method, parameters, _) =>
        {
            if (method == "mcpServerStatus/list")
            {
                JsonElement request = JsonSerializer.SerializeToElement(parameters);
                bool secondPage = request.GetProperty("cursor").ValueKind == JsonValueKind.String;
                return Task.FromResult(secondPage
                    ? JsonSerializer.SerializeToElement(new { invalid = true })
                    : JsonSerializer.SerializeToElement(new { data = new[] { new { name = "docs-private" } }, nextCursor = "next" }));
            }

            return Task.FromResult(JsonSerializer.SerializeToElement(new { loginId = "unexpected" }));
        };
        await using var service = CreateService(redactor: redactor);
        await service.InitializeAsync(connection, Options(experimentalApi: false), CancellationToken.None);

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => service.ListMcpServersAsync(
            new ListMcpServersRequest { ThreadId = "thread-1" }, CancellationToken.None));
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => service.StartMcpOAuthLoginAsync(
            new StartMcpOAuthLoginRequest { ServerName = "[REDACTED]", ThreadId = "thread-1" }, CancellationToken.None));

        Assert.IsFalse(connection.Requests.Any(request => request.Method == "mcpServer/oauth/login"));
    }

    [TestMethod]
    public async Task McpLegacyDisplayNameCache_RejectsUnicodeFormatCharactersInRawNames()
    {
        string invalidName = "docs\u202Eprivate";
        var redactor = new SecretRedactor();
        using IDisposable nameSecret = redactor.RegisterSecret(invalidName);
        var connection = new RecordingConnection
        {
            InitializeResponse = JsonSerializer.SerializeToElement(new { userAgent = "codex-cli/0.170.2" }),
            AsyncHandler = (method, _, _) => method == "mcpServerStatus/list"
                ? Task.FromResult(JsonSerializer.SerializeToElement(new { data = new[] { new { name = invalidName } }, nextCursor = (string?)null }))
                : Task.FromResult(JsonSerializer.SerializeToElement(new { loginId = "unexpected" })),
        };
        await using var service = CreateService(redactor: redactor);
        await service.InitializeAsync(connection, Options(experimentalApi: false), CancellationToken.None);

        await service.ListMcpServersAsync(new ListMcpServersRequest { ThreadId = "thread-1" }, CancellationToken.None);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => service.StartMcpOAuthLoginAsync(
            new StartMcpOAuthLoginRequest { ServerName = "[REDACTED]", ThreadId = "thread-1" }, CancellationToken.None));

        Assert.IsFalse(connection.Requests.Any(request => request.Method == "mcpServer/oauth/login"));
    }

    [TestMethod]
    public async Task McpOAuthPreflight_RejectsZeroMatchingRegistrations()
    {
        var connection = new RecordingConnection
        {
            InitializeResponse = JsonSerializer.SerializeToElement(new { userAgent = "codex-cli/0.171.0" }),
            AsyncHandler = (method, _, _) => method == "mcpServerStatus/list"
                ? Task.FromResult(JsonSerializer.SerializeToElement(new { data = Array.Empty<object>(), nextCursor = (string?)null }))
                : Task.FromResult(JsonSerializer.SerializeToElement(new { loginId = "unexpected" })),
        };
        await using var service = CreateService();
        await service.InitializeAsync(connection, Options(experimentalApi: true), CancellationToken.None);

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => service.StartMcpOAuthLoginAsync(
            new StartMcpOAuthLoginRequest { ServerName = "docs", ThreadId = "thread-1" },
            CancellationToken.None));

        Assert.IsFalse(connection.Requests.Any(request => request.Method == "mcpServer/oauth/login"));
    }

    [TestMethod]
    public async Task McpOAuthPreflight_UsesUniqueLegacyRouteForMissingEnvironmentIdAndThreadWideForNull()
    {
        foreach (bool includeEnvironmentId in new[] { false, true })
        {
            var registration = new Dictionary<string, object?>(StringComparer.Ordinal) { ["name"] = "docs" };
            if (includeEnvironmentId)
            {
                registration["environmentId"] = null;
            }

            var connection = new RecordingConnection
            {
                InitializeResponse = JsonSerializer.SerializeToElement(new { userAgent = "codex-cli/0.171.0" }),
            };
            connection.AsyncHandler = (method, _, _) => method switch
            {
                "mcpServerStatus/list" => Task.FromResult(JsonSerializer.SerializeToElement(new { data = new[] { registration }, nextCursor = (string?)null })),
                "mcpServer/oauth/login" => Task.FromResult(JsonSerializer.SerializeToElement(new { loginId = "thread-wide", authorizationUrl = "https://login.example.test/authorize" })),
                _ => Task.FromResult(JsonSerializer.SerializeToElement(new { })),
            };
            await using var service = CreateService();
            await service.InitializeAsync(connection, Options(experimentalApi: true), CancellationToken.None);

            McpOAuthLoginStatus login = await service.StartMcpOAuthLoginAsync(
                new StartMcpOAuthLoginRequest { ServerName = "docs", ThreadId = "thread-1" },
                CancellationToken.None);
            JsonElement parameters = JsonSerializer.SerializeToElement(connection.Requests.Single(request => request.Method == "mcpServer/oauth/login").Parameters);
            Assert.AreEqual("docs", parameters.GetProperty("name").GetString());
            Assert.AreEqual("thread-1", parameters.GetProperty("threadId").GetString(),
                "The pinned thread must accompany the name-based route so the Worker rechecks uniqueness in that thread.");
            JsonElement inventoryParameters = JsonSerializer.SerializeToElement(connection.Requests.Single(request => request.Method == "mcpServerStatus/list").Parameters);
            Assert.AreEqual("thread-1", inventoryParameters.GetProperty("threadId").GetString(),
                "The Worker must validate the unique candidate against a complete inventory scoped to the same thread.");
            if (includeEnvironmentId)
            {
                Assert.AreEqual(JsonValueKind.Null, parameters.GetProperty("environmentId").ValueKind);
                Assert.IsTrue(login.HasRegistrationIdentity, "An explicit null environmentId is a thread-wide registration.");
            }
            else
            {
                Assert.IsFalse(parameters.TryGetProperty("environmentId", out _));
                Assert.IsFalse(login.HasRegistrationIdentity, "One complete-inventory name match may use the legacy name/thread route when environmentId is absent.");
            }
        }
    }

    [TestMethod]
    public async Task McpOAuthPreflight_RejectsMultipleRegistrationsWhenEnvironmentIdIsMissing()
    {
        var first = new Dictionary<string, object?>(StringComparer.Ordinal) { ["name"] = "docs" };
        var second = new Dictionary<string, object?>(StringComparer.Ordinal) { ["name"] = "docs" };
        var connection = new RecordingConnection
        {
            InitializeResponse = JsonSerializer.SerializeToElement(new { userAgent = "codex-cli/0.171.0" }),
            AsyncHandler = (method, _, _) => method == "mcpServerStatus/list"
                ? Task.FromResult(JsonSerializer.SerializeToElement(new { data = new[] { first, second }, nextCursor = (string?)null }))
                : Task.FromResult(JsonSerializer.SerializeToElement(new { loginId = "unexpected" })),
        };
        await using var service = CreateService();
        await service.InitializeAsync(connection, Options(experimentalApi: true), CancellationToken.None);

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => service.StartMcpOAuthLoginAsync(
            new StartMcpOAuthLoginRequest { ServerName = "docs", ThreadId = "thread-1" },
            CancellationToken.None));
        Assert.IsFalse(connection.Requests.Any(request => request.Method == "mcpServer/oauth/login"));
    }

    [TestMethod]
    [DataRow("docs\u0000bad", "thread-1", null)]
    [DataRow("docs\nbad", "thread-1", null)]
    [DataRow("docs\tbad", "thread-1", null)]
    [DataRow("docs\u007fbad", "thread-1", null)]
    [DataRow("docs\u202ebad", "thread-1", null)]
    [DataRow("docs\u200bbad", "thread-1", null)]
    [DataRow("docs", "   ", null)]
    [DataRow("docs", "thread\u001fbad", null)]
    [DataRow("docs", "thread-1", "environment\u001fbad")]
    [DataRow("docs", "thread-1", "environment\u202ebad")]
    [DataRow("docs", "thread-1", "environment\u200bbad")]
    [DataRow("docs", "thread-1", "   ")]
    public async Task McpOAuthLogin_RejectsControlCharactersInIdentityFields(string serverName, string threadId, string? environmentId)
    {
        var connection = EnvironmentIdentityConnection(new { name = "docs", environmentId = "env-a" });
        await using var service = CreateService();
        await service.InitializeAsync(connection, Options(experimentalApi: true), CancellationToken.None);

        await Assert.ThrowsExactlyAsync<ArgumentException>(() => service.StartMcpOAuthLoginAsync(
            new StartMcpOAuthLoginRequest { ServerName = serverName, ThreadId = threadId, EnvironmentId = environmentId, HasRegistrationIdentity = environmentId is not null },
            CancellationToken.None));
        Assert.IsFalse(connection.Requests.Any(request => request.Method == "mcpServer/oauth/login"));
    }

    [TestMethod]
    public async Task McpUntrustedInventoryAndNotifications_RejectControlCharactersInIdentity()
    {
        var inventoryConnection = new RecordingConnection
        {
            InitializeResponse = JsonSerializer.SerializeToElement(new { userAgent = "codex-cli/0.171.0" }),
            AsyncHandler = (method, _, _) => method == "mcpServerStatus/list"
                ? Task.FromResult(JsonSerializer.SerializeToElement(new
                {
                    data = new object[]
                    {
                        new { name = "valid-server", environmentId = "valid-environment" },
                        new { name = "invalid\u001fserver", environmentId = "environment-a" },
                        new { name = "valid-server", environmentId = "environment\u001fbad" },
                        new { name = "valid-server", environmentId = "environment\u202ebad" },
                    },
                }))
                : Task.FromResult(JsonSerializer.SerializeToElement(new { })),
        };
        await using var inventoryService = CreateService();
        await inventoryService.InitializeAsync(inventoryConnection, Options(experimentalApi: true), CancellationToken.None);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => inventoryService.ListMcpServersAsync(
            new ListMcpServersRequest(), CancellationToken.None),
            "A malformed candidate must fail the complete inventory instead of making a filtered list appear unique.");
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => inventoryService.StartMcpOAuthLoginAsync(
            new StartMcpOAuthLoginRequest { ServerName = "valid-server", ThreadId = "thread-1" }, CancellationToken.None),
            "OAuth preflight must fail closed when a same-name registration has an invalid environment identity.");
        Assert.IsFalse(inventoryConnection.Requests.Any(request => request.Method == "mcpServer/oauth/login"));

        var connection = new RecordingConnection
        {
            InitializeResponse = JsonSerializer.SerializeToElement(new { userAgent = "codex-cli/0.171.0" }),
        };
        connection.AsyncHandler = (method, _, _) => method == "mcpServer/oauth/login"
            ? Task.FromResult(JsonSerializer.SerializeToElement(new { loginId = "login-valid", authorizationUrl = "https://login.example.test/authorize" }))
            : Task.FromResult(JsonSerializer.SerializeToElement(new { }));
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
            ServerName = "docs", ThreadId = "thread-valid", HasRegistrationIdentity = true, EnvironmentId = "environment-valid",
        }, CancellationToken.None);
        int statusCount = statuses.Count;

        await connection.EmitNotificationAsync("mcpServer/startupStatus/updated", new
        {
            name = "docs", threadId = "thread\u001fbad", environmentId = "environment-valid", status = "ready",
        });
        await connection.EmitNotificationAsync("mcpServer/startupStatus/updated", new
        {
            name = "docs", threadId = "thread\u202ebad", environmentId = "environment-valid", status = "ready",
        });
        await connection.EmitNotificationAsync("mcpServer/startupStatus/updated", new
        {
            name = "docs", threadId = "thread-valid", environmentId = "environment\u001fbad", status = "ready",
        });
        await connection.EmitNotificationAsync("mcpServer/startupStatus/updated", new
        {
            name = "docs", threadId = "thread-valid", environmentId = "environment\u200bbad", status = "ready",
        });
        await connection.EmitNotificationAsync("mcpServer/oauthLogin/completed", new
        {
            name = "docs\u001fbad", threadId = "thread-valid", environmentId = "environment-valid", success = true, loginId = "login-valid",
        });
        await connection.EmitNotificationAsync("mcpServer/oauthLogin/completed", new
        {
            name = "docs\u202ebad", threadId = "thread-valid", environmentId = "environment-valid", success = true, loginId = "login-valid",
        });
        await connection.EmitNotificationAsync("mcpServer/oauthLogin/completed", new
        {
            name = "docs\u200bbad", threadId = "thread-valid", environmentId = "environment-valid", success = true, loginId = "login-valid",
        });
        await connection.EmitNotificationAsync("mcpServer/oauthLogin/completed", new
        {
            name = "docs", threadId = "thread-valid", environmentId = "environment\u001fbad", success = true, loginId = "login-valid",
        });

        Assert.AreEqual(statusCount, statuses.Count, "Malformed notification identity must not update the pending auth status.");
        Assert.AreEqual(InteractionAuthState.LoginPending, statuses.Last().McpServers.Single().State);

        await connection.EmitNotificationAsync("mcpServer/oauthLogin/completed", new
        {
            name = "docs", threadId = "thread-valid", environmentId = "environment-valid", success = true, loginId = "login-valid",
        });
        Assert.AreEqual(statusCount + 1, statuses.Count, "A well-formed notification remains accepted after malformed identities are rejected.");
        Assert.AreEqual(InteractionAuthState.Authenticated, statuses.Last().McpServers.Single().State);
    }

    [TestMethod]
    public async Task McpElicitationRequest_RejectsControlCharactersInIdentityFields()
    {
        foreach ((string key, string value) in new[]
        {
            ("serverName", "docs\0bad"),
            ("serverName", "docs\nbad"),
            ("serverName", "docs\tbad"),
            ("serverName", "docs\u007fbad"),
            ("serverName", "docs\u202ebad"),
            ("serverName", "docs\u200bbad"),
            ("threadId", "thread\u001fbad"),
            ("threadId", "thread\u202ebad"),
            ("threadId", "thread\u200bbad"),
            ("environmentId", "environment\u001fbad"),
            ("environmentId", "environment\u202ebad"),
            ("environmentId", "environment\u200bbad"),
            ("elicitationId", "elicitation\u001fbad"),
        })
        {
            var connection = new RecordingConnection
            {
                InitializeResponse = JsonSerializer.SerializeToElement(new { userAgent = "codex-cli/0.171.0" }),
            };
            await using var service = CreateService();
            int requestEventCount = 0;
            var validRequestReceived = new TaskCompletionSource<McpElicitationRequest>(TaskCreationOptions.RunContinuationsAsynchronously);
            service.McpElicitationRequested += (request, _) =>
            {
                Interlocked.Increment(ref requestEventCount);
                validRequestReceived.TrySetResult(request);
                return Task.CompletedTask;
            };
            await service.InitializeAsync(connection, Options(experimentalApi: true), CancellationToken.None);
            var payload = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["serverName"] = "docs",
                ["threadId"] = "thread-valid",
                ["environmentId"] = "environment-valid",
                ["mode"] = "url",
                ["elicitationId"] = "elicitation-valid",
                ["message"] = "Sign in",
                ["url"] = "https://login.example.test/authorize",
            };
            payload[key] = value;

            JsonRpcRemoteException error = await Assert.ThrowsExactlyAsync<JsonRpcRemoteException>(() => connection.EmitRequestAsync(
                "elicitation-control", "mcpServer/elicitation/request", payload), key);
            Assert.AreNotEqual(0, error.Code, "The invalid request must receive a protocol validation error.");
            Assert.AreEqual(0, requestEventCount, "Invalid identity must be rejected before publishing an elicitation event.");

            var validPayload = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["serverName"] = "docs",
                ["threadId"] = "thread-valid",
                ["environmentId"] = "environment-valid",
                ["mode"] = "url",
                ["elicitationId"] = "elicitation-valid",
                ["message"] = "Sign in",
                ["url"] = "https://login.example.test/authorize",
            };
            Task<JsonElement> validRequestTask = connection.EmitRequestAsync(
                "elicitation-valid", "mcpServer/elicitation/request", validPayload);
#pragma warning disable VSTHRD003 // The signal is completed by the valid request emitted in this test.
            McpElicitationRequest prompt = await validRequestReceived.Task.WaitAsync(TimeSpan.FromSeconds(2));
#pragma warning restore VSTHRD003
            Assert.AreEqual(1, requestEventCount, "The equivalent well-formed payload reaches the request event.");
            await service.ResolveMcpElicitationAsync(new ResolveMcpElicitationRequest
            {
                RequestId = prompt.RequestId,
                Action = McpElicitationAction.Cancel,
            }, CancellationToken.None);
            Assert.AreEqual("cancel", (await validRequestTask.WaitAsync(TimeSpan.FromSeconds(2))).GetProperty("action").GetString());
        }
    }

    [TestMethod]
    public async Task McpOAuthPreflight_ReadsAllPagesBeforeRejectingSameNameAmbiguity()
    {
        var connection = new RecordingConnection
        {
            InitializeResponse = JsonSerializer.SerializeToElement(new { userAgent = "codex-cli/0.171.0" }),
        };
        connection.AsyncHandler = (method, parameters, _) =>
        {
            if (method == "mcpServerStatus/list")
            {
                JsonElement request = JsonSerializer.SerializeToElement(parameters);
                bool secondPage = request.TryGetProperty("cursor", out JsonElement cursor)
                    && cursor.ValueKind == JsonValueKind.String;
                object registration = new { name = "docs", environmentId = secondPage ? "env-b" : "env-a" };
                return Task.FromResult(JsonSerializer.SerializeToElement(new
                {
                    data = new[] { registration },
                    nextCursor = secondPage ? null : "page-two",
                }));
            }

            return Task.FromResult(JsonSerializer.SerializeToElement(new { loginId = "unexpected" }));
        };
        await using var service = CreateService();
        await service.InitializeAsync(connection, Options(experimentalApi: true), CancellationToken.None);

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => service.StartMcpOAuthLoginAsync(
            new StartMcpOAuthLoginRequest { ServerName = "docs", ThreadId = "thread-1" },
            CancellationToken.None));

        RecordedRequest[] listRequests = connection.Requests.Where(request => request.Method == "mcpServerStatus/list").ToArray();
        Assert.AreEqual(2, listRequests.Length);
        Assert.IsNull(JsonSerializer.SerializeToElement(listRequests[0].Parameters).GetProperty("cursor").GetString());
        Assert.AreEqual("page-two", JsonSerializer.SerializeToElement(listRequests[1].Parameters).GetProperty("cursor").GetString());
        Assert.IsTrue(listRequests.All(request => JsonSerializer.SerializeToElement(request.Parameters).GetProperty("limit").GetInt32() == 100));
        Assert.IsFalse(connection.Requests.Any(request => request.Method == "mcpServer/oauth/login"));

        McpServerListResult fullInventory = await service.ListMcpServersAsync(
            new ListMcpServersRequest { ThreadId = "thread-1" },
            CancellationToken.None);
        Assert.AreEqual(2, fullInventory.Servers.Count, "The public worker listing uses the same all-pages inventory helper.");
    }

    [TestMethod]
    public async Task McpOAuthPreflight_FailsClosedWhenALaterPageIsInvalid()
    {
        var connection = new RecordingConnection
        {
            InitializeResponse = JsonSerializer.SerializeToElement(new { userAgent = "codex-cli/0.171.0" }),
        };
        connection.AsyncHandler = (method, parameters, _) =>
        {
            if (method == "mcpServerStatus/list")
            {
                JsonElement request = JsonSerializer.SerializeToElement(parameters);
                bool secondPage = request.GetProperty("cursor").ValueKind == JsonValueKind.String;
                return Task.FromResult(secondPage
                    ? JsonSerializer.SerializeToElement(new { unexpected = true })
                    : JsonSerializer.SerializeToElement(new { data = new[] { new { name = "docs", environmentId = "env-a" } }, nextCursor = "page-two" }));
            }

            return Task.FromResult(JsonSerializer.SerializeToElement(new { loginId = "unexpected" }));
        };
        await using var service = CreateService();
        await service.InitializeAsync(connection, Options(experimentalApi: true), CancellationToken.None);

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => service.StartMcpOAuthLoginAsync(
            new StartMcpOAuthLoginRequest { ServerName = "docs", ThreadId = "thread-1" },
            CancellationToken.None));

        Assert.IsFalse(connection.Requests.Any(request => request.Method == "mcpServer/oauth/login"));
    }

    [TestMethod]
    public async Task McpOAuthPreflight_FailsClosedWhenPaginationCursorRepeats()
    {
        var connection = new RecordingConnection
        {
            InitializeResponse = JsonSerializer.SerializeToElement(new { userAgent = "codex-cli/0.171.0" }),
            AsyncHandler = (method, _, _) => method == "mcpServerStatus/list"
                ? Task.FromResult(JsonSerializer.SerializeToElement(new { data = Array.Empty<object>(), nextCursor = "repeat" }))
                : Task.FromResult(JsonSerializer.SerializeToElement(new { loginId = "unexpected" })),
        };
        await using var service = CreateService();
        await service.InitializeAsync(connection, Options(experimentalApi: true), CancellationToken.None);

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => service.StartMcpOAuthLoginAsync(
            new StartMcpOAuthLoginRequest { ServerName = "docs", ThreadId = "thread-1" },
            CancellationToken.None));

        Assert.AreEqual(2, connection.Requests.Count(request => request.Method == "mcpServerStatus/list"));
        Assert.IsFalse(connection.Requests.Any(request => request.Method == "mcpServer/oauth/login"));
    }

    [TestMethod]
    public async Task McpServerInventory_AcceptsFullFinalPageWithoutOptionalCursor()
    {
        var servers = Enumerable.Range(0, 100)
            .Select(index => new { name = $"server-{index}", environmentId = (string?)null })
            .ToArray();
        var connection = new RecordingConnection
        {
            InitializeResponse = JsonSerializer.SerializeToElement(new { userAgent = "codex-cli/0.171.0" }),
            AsyncHandler = (method, _, _) => method == "mcpServerStatus/list"
                ? Task.FromResult(JsonSerializer.SerializeToElement(new { data = servers }))
                : Task.FromResult(JsonSerializer.SerializeToElement(new { loginId = "unexpected" })),
        };
        await using var service = CreateService();
        await service.InitializeAsync(connection, Options(experimentalApi: true), CancellationToken.None);

        McpServerListResult inventory = await service.ListMcpServersAsync(
            new ListMcpServersRequest { ThreadId = "thread-1" },
            CancellationToken.None);

        Assert.AreEqual(100, inventory.Servers.Count);
        Assert.AreEqual(100, JsonSerializer.SerializeToElement(connection.Requests.Single(request => request.Method == "mcpServerStatus/list").Parameters).GetProperty("limit").GetInt32());
    }

    [TestMethod]
    public async Task McpOAuthLogin_RejectsControlCharactersInWireServerNamesAcrossTiers()
    {
        foreach ((string version, bool experimentalApi) in new[]
        {
            ("0.169.0", false),
            ("0.170.2", true),
            ("0.171.0", true),
        })
        {
            var connection = new RecordingConnection
            {
                InitializeResponse = JsonSerializer.SerializeToElement(new { userAgent = $"codex-cli/{version}" }),
            };
            await using var service = CreateService();
            await service.InitializeAsync(connection, Options(experimentalApi), CancellationToken.None);

            await Assert.ThrowsExactlyAsync<ArgumentException>(() => service.StartMcpOAuthLoginAsync(
                new StartMcpOAuthLoginRequest { ServerName = "docs\u001fprivate" },
                CancellationToken.None));

            Assert.IsFalse(connection.Requests.Any(request => request.Method is "mcpServerStatus/list" or "mcpServer/oauth/login"));
        }
    }

    [TestMethod]
    public async Task McpOAuthLogin_ValidServerNamesReachWireAcrossSupportedTiers()
    {
        foreach ((string version, bool experimentalApi) in new[]
        {
            ("0.169.0", false),
            ("0.170.2", false),
            ("0.171.0", true),
        })
        {
            var connection = new RecordingConnection
            {
                InitializeResponse = JsonSerializer.SerializeToElement(new { userAgent = $"codex-cli/{version}" }),
            };
            connection.AsyncHandler = (method, _, _) => method switch
            {
                "mcpServerStatus/list" => Task.FromResult(JsonSerializer.SerializeToElement(new
                {
                    data = new[] { new { name = "docs", environmentId = "environment-a" } },
                    nextCursor = (string?)null,
                })),
                "mcpServer/oauth/login" => Task.FromResult(JsonSerializer.SerializeToElement(new
                {
                    loginId = "valid-login", authorizationUrl = "https://login.example.test/authorize",
                })),
                _ => Task.FromResult(JsonSerializer.SerializeToElement(new { })),
            };
            await using var service = CreateService();
            await service.InitializeAsync(connection, Options(experimentalApi), CancellationToken.None);

            await service.StartMcpOAuthLoginAsync(new StartMcpOAuthLoginRequest
            {
                ServerName = "docs",
                ThreadId = "thread-valid",
            }, CancellationToken.None);

            JsonElement parameters = JsonSerializer.SerializeToElement(
                connection.Requests.Single(request => request.Method == "mcpServer/oauth/login").Parameters);
            Assert.AreEqual("docs", parameters.GetProperty("name").GetString(), version);
            Assert.AreEqual("thread-valid", parameters.GetProperty("threadId").GetString(), version);
            Assert.AreEqual(experimentalApi, connection.Requests.Any(request => request.Method == "mcpServerStatus/list"), version);
        }
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
    public async Task McpToolElicitation_ProjectsOnlyBoundedToolHintsForDisplay()
    {
        var connection = new RecordingConnection();
        await using var service = CreateService();
        McpElicitationRequest? captured = null;
        service.McpElicitationRequested += (request, _) =>
        {
            captured = request;
            return Task.CompletedTask;
        };
        await service.InitializeAsync(connection, Options(experimentalApi: true), CancellationToken.None);

        Task<JsonElement> pending = connection.EmitRequestAsync("tool-approval", "mcpServer/elicitation/request", new
        {
            serverName = "docs",
            threadId = "thread-1",
            mode = "url",
            elicitationId = "tool-approval",
            message = "Authorize this tool",
            url = "https://login.example.test/tool",
            _meta = new
            {
                tool_name = "read_file",
                tool_annotations = new
                {
                    readOnlyHint = true,
                    destructiveHint = false,
                    idempotentHint = true,
                    openWorldHint = "unknown",
                },
            },
        });

        Assert.IsNotNull(captured);
        Assert.AreEqual("read_file", captured.DisplayToolName);
        Assert.AreEqual(true, captured.ReadOnlyHint);
        Assert.AreEqual(false, captured.DestructiveHint);
        Assert.AreEqual(true, captured.IdempotentHint);
        Assert.IsNull(captured.OpenWorldHint, "A malformed annotation is not presented as a hint.");
        await service.ResolveMcpElicitationAsync(new ResolveMcpElicitationRequest
        {
            RequestId = captured.RequestId,
            Action = McpElicitationAction.Cancel,
        }, CancellationToken.None);
        await pending;
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
    public async Task McpCompletionRaceAcrossSameNameLogins_UsesLoginIdBeforeOrAfterResponses()
    {
        foreach ((bool notifyBeforeResponses, bool respondSecondFirst) in new[] { (true, true), (true, false), (false, true), (false, false) })
        {
            var connection = new RecordingConnection
            {
                InitializeResponse = JsonSerializer.SerializeToElement(new { userAgent = "codex-cli/0.171.0" }),
            };
            var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var secondStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var finishFirst = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
            var finishSecond = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
            connection.AsyncHandler = (method, parameters, _) =>
            {
                if (method != "mcpServer/oauth/login")
                {
                    return Task.FromResult(JsonSerializer.SerializeToElement(new { }));
                }

                JsonElement request = JsonSerializer.SerializeToElement(parameters);
                string threadId = request.GetProperty("threadId").GetString()!;
                if (threadId == "thread-a")
                {
                    firstStarted.TrySetResult();
                    return finishFirst.Task;
                }

                secondStarted.TrySetResult();
                return finishSecond.Task;
            };
            var statuses = new ConcurrentQueue<InteractionAuthStatus>();
            await using var service = CreateService();
            service.InteractionAuthStatusChanged += (status, _) =>
            {
                statuses.Enqueue(status);
                return Task.CompletedTask;
            };
            await service.InitializeAsync(connection, Options(experimentalApi: true), CancellationToken.None);

            Task<McpOAuthLoginStatus> firstLogin = service.StartMcpOAuthLoginAsync(new StartMcpOAuthLoginRequest
            {
                ServerName = "docs", ThreadId = "thread-a", HasRegistrationIdentity = true, EnvironmentId = "env-shared",
            }, CancellationToken.None);
            Task<McpOAuthLoginStatus> secondLogin = service.StartMcpOAuthLoginAsync(new StartMcpOAuthLoginRequest
            {
                ServerName = "docs", ThreadId = "thread-b", HasRegistrationIdentity = true, EnvironmentId = "env-shared",
            }, CancellationToken.None);
            await Task.WhenAll(firstStarted.Task, secondStarted.Task).WaitAsync(TimeSpan.FromSeconds(2));

            if (notifyBeforeResponses)
            {
                await connection.EmitNotificationAsync("mcpServer/oauthLogin/completed", new
                {
                    name = "docs", environmentId = "env-shared", success = true, loginId = "login-wrong",
                });
                await connection.EmitNotificationAsync("mcpServer/oauthLogin/completed", new
                {
                    name = "docs", environmentId = "env-shared", success = true, loginId = "login-b",
                });
            }

            if (respondSecondFirst)
            {
                finishSecond.TrySetResult(JsonSerializer.SerializeToElement(new { loginId = "login-b", authorizationUrl = "https://login.example.test/b" }));
                await secondLogin.WaitAsync(TimeSpan.FromSeconds(2));
                Assert.AreEqual(InteractionAuthState.LoginPending, statuses.Last().McpServers.Single(server => server.ThreadId == "thread-a").State);
                finishFirst.TrySetResult(JsonSerializer.SerializeToElement(new { loginId = "login-a", authorizationUrl = "https://login.example.test/a" }));
                await firstLogin.WaitAsync(TimeSpan.FromSeconds(2));
            }
            else
            {
                finishFirst.TrySetResult(JsonSerializer.SerializeToElement(new { loginId = "login-a", authorizationUrl = "https://login.example.test/a" }));
                await firstLogin.WaitAsync(TimeSpan.FromSeconds(2));
                Assert.AreEqual(InteractionAuthState.LoginPending, statuses.Last().McpServers.Single(server => server.ThreadId == "thread-a").State);
                finishSecond.TrySetResult(JsonSerializer.SerializeToElement(new { loginId = "login-b", authorizationUrl = "https://login.example.test/b" }));
                await secondLogin.WaitAsync(TimeSpan.FromSeconds(2));
            }
            if (!notifyBeforeResponses)
            {
                await connection.EmitNotificationAsync("mcpServer/oauthLogin/completed", new
                {
                    name = "docs", environmentId = "env-shared", success = true, loginId = "login-wrong",
                });
                await connection.EmitNotificationAsync("mcpServer/oauthLogin/completed", new
                {
                    name = "docs", environmentId = "env-shared", success = true, loginId = "login-b",
                });
            }

            InteractionAuthStatus final = statuses.Last();
            Assert.AreEqual(InteractionAuthState.LoginPending, final.McpServers.Single(server => server.ThreadId == "thread-a").State);
            Assert.AreEqual(InteractionAuthState.Authenticated, final.McpServers.Single(server => server.ThreadId == "thread-b").State);
        }
    }

    [TestMethod]
    public async Task McpDeferredOAuthCompletions_KeepSameLoginIdInSeparateEnvironmentScopes()
    {
        var connection = new RecordingConnection
        {
            InitializeResponse = JsonSerializer.SerializeToElement(new { userAgent = "codex-cli/0.171.0" }),
        };
        var starts = Enumerable.Range(0, 4)
            .ToDictionary(index => $"thread-{index}", _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously), StringComparer.Ordinal);
        var responses = Enumerable.Range(0, 4)
            .ToDictionary(index => $"thread-{index}", _ => new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously), StringComparer.Ordinal);
        connection.AsyncHandler = (method, parameters, _) =>
        {
            if (method != "mcpServer/oauth/login")
            {
                return Task.FromResult(JsonSerializer.SerializeToElement(new { }));
            }

            string threadId = JsonSerializer.SerializeToElement(parameters).GetProperty("threadId").GetString()!;
            starts[threadId].TrySetResult();
            return responses[threadId].Task;
        };
        var statuses = new ConcurrentQueue<InteractionAuthStatus>();
        await using var service = CreateService();
        service.InteractionAuthStatusChanged += (status, _) =>
        {
            statuses.Enqueue(status);
            return Task.CompletedTask;
        };
        await service.InitializeAsync(connection, Options(experimentalApi: true), CancellationToken.None);

        Task<McpOAuthLoginStatus>[] logins = Enumerable.Range(0, 4).Select(index => service.StartMcpOAuthLoginAsync(new StartMcpOAuthLoginRequest
        {
            ServerName = "docs",
            ThreadId = $"thread-{index}",
            HasRegistrationIdentity = true,
            EnvironmentId = index < 2 ? "environment-a" : "environment-b",
        }, CancellationToken.None)).ToArray();
        await Task.WhenAll(starts.Values.Select(start => start.Task)).WaitAsync(TimeSpan.FromSeconds(2));

        await connection.EmitNotificationAsync("mcpServer/oauthLogin/completed", new
        {
            name = "docs", environmentId = "environment-a", success = true, loginId = "shared-login-id",
        });
        await connection.EmitNotificationAsync("mcpServer/oauthLogin/completed", new
        {
            name = "docs", environmentId = "environment-b", success = false, loginId = "shared-login-id",
        });

        responses["thread-0"].TrySetResult(JsonSerializer.SerializeToElement(new { loginId = "shared-login-id", authorizationUrl = "https://login.example.test/a" }));
        await logins[0].WaitAsync(TimeSpan.FromSeconds(2));
        responses["thread-2"].TrySetResult(JsonSerializer.SerializeToElement(new { loginId = "shared-login-id", authorizationUrl = "https://login.example.test/b" }));
        await logins[2].WaitAsync(TimeSpan.FromSeconds(2));

        McpServerAuthStatus[] final = statuses.Last().McpServers.ToArray();
        Assert.AreEqual(InteractionAuthState.Authenticated, final.Single(server => server.ThreadId == "thread-0").State);
        Assert.AreEqual(InteractionAuthState.Failed, final.Single(server => server.ThreadId == "thread-2").State);

        responses["thread-1"].TrySetResult(JsonSerializer.SerializeToElement(new { loginId = "shared-login-id", authorizationUrl = "https://login.example.test/a2" }));
        responses["thread-3"].TrySetResult(JsonSerializer.SerializeToElement(new { loginId = "shared-login-id", authorizationUrl = "https://login.example.test/b2" }));
        await Task.WhenAll(logins).WaitAsync(TimeSpan.FromSeconds(2));
    }

    [TestMethod]
    public void McpRegistrationKey_UsesStructuredFieldsForDelimiterLikeInput()
    {
        var first = new CodexSessionService.McpRegistrationKey("b\u001fc", "a", true, "d");
        var second = new CodexSessionService.McpRegistrationKey("b", "c\u001fa", true, "d");
        var registrations = new Dictionary<CodexSessionService.McpRegistrationKey, string>
        {
            [first] = "first",
            [second] = "second",
        };

        Assert.AreNotEqual(first, second, "The structured key keeps each field boundary distinct even when values contain separator characters.");
        Assert.AreEqual(2, registrations.Count, "Delimiter-join-colliding tuples coexist in the structured-key dictionary.");
    }

    [TestMethod]
    public async Task McpAuthStatusCapacity_FailsClosedForIdentityFreeRoutingAfterOverflow()
    {
        var connection = new RecordingConnection
        {
            InitializeResponse = JsonSerializer.SerializeToElement(new { userAgent = "codex-cli/0.171.0" }),
        };
        await using var service = CreateService();
        await service.InitializeAsync(connection, Options(experimentalApi: true), CancellationToken.None);

        await service.StartMcpOAuthLoginAsync(new StartMcpOAuthLoginRequest
        {
            ServerName = "docs", ThreadId = "thread-0", HasRegistrationIdentity = true, EnvironmentId = "environment-0",
        }, CancellationToken.None);

        for (int index = 0; index < 1025; index++)
        {
            string serverName = index == 0 ? "docs" : $"server-{index}";
            await connection.EmitNotificationAsync("mcpServer/startupStatus/updated", new
            {
                name = serverName,
                threadId = $"thread-{index}",
                environmentId = $"environment-{index}",
                status = "failed",
            });
        }

        await connection.EmitNotificationAsync("mcpServer/startupStatus/updated", new
        {
            name = "docs", threadId = "thread-0", status = "ready",
        });
        InteractionAuthStatus afterAmbiguousUpdate = await service.ReadGatewayOAuthAsync(CancellationToken.None);
        Assert.AreEqual(1024, afterAmbiguousUpdate.McpServers.Count, "The worker retains no more than the configured number of registration statuses.");
        Assert.AreEqual(InteractionAuthState.Failed, afterAmbiguousUpdate.McpServers.Single(server => server.ServerName == "docs").State,
            "Once a status candidate may be missing, identity-free routing must not infer uniqueness from a partial cache.");

        await connection.EmitNotificationAsync("mcpServer/startupStatus/updated", new
        {
            name = "docs", threadId = "thread-0", environmentId = "environment-0", status = "ready",
        });
        InteractionAuthStatus afterIdentifiedUpdate = await service.ReadGatewayOAuthAsync(CancellationToken.None);
        Assert.AreEqual(InteractionAuthState.Authenticated, afterIdentifiedUpdate.McpServers.Single(server => server.ServerName == "docs").State,
            "An explicitly identified existing registration remains routable after overflow.");

        McpOAuthLoginStatus newExplicitLogin = await service.StartMcpOAuthLoginAsync(
            new StartMcpOAuthLoginRequest
            {
                ServerName = "new-server", ThreadId = "thread-new", HasRegistrationIdentity = true, EnvironmentId = "environment-new",
            }, CancellationToken.None);
        Assert.AreEqual(InteractionAuthState.Failed, newExplicitLogin.State,
            "The test connection returns no login ID, but the explicit identity operation is admitted after safe eviction.");
        Assert.IsTrue(connection.Requests.Any(request => request.Method == "mcpServer/oauth/login"),
            "A new explicit identity operation can continue after evicting an inactive status.");
        InteractionAuthStatus afterEviction = await service.ReadGatewayOAuthAsync(CancellationToken.None);
        Assert.AreEqual(1024, afterEviction.McpServers.Count, "The bounded status map remains at its configured capacity.");
        Assert.IsFalse(afterEviction.McpServers.Any(server => server.ServerName == "server-1"),
            "The least recently used inactive status was evicted to make room.");

        await connection.EmitNotificationAsync("mcpServer/startupStatus/updated", new
        {
            name = "new-server", threadId = "thread-new", status = "ready",
        });
        InteractionAuthStatus afterIdentityFreeUpdate = await service.ReadGatewayOAuthAsync(CancellationToken.None);
        Assert.AreEqual(InteractionAuthState.Failed, afterIdentityFreeUpdate.McpServers.Single(server => server.ServerName == "new-server").State,
            "The persistent uncertainty tombstone keeps identity-free routing fail-closed after eviction.");

        for (int index = 2; index < 1024; index++)
        {
            await service.StartMcpOAuthLoginAsync(new StartMcpOAuthLoginRequest
            {
                ServerName = $"server-{index}",
                ThreadId = $"thread-{index}",
                HasRegistrationIdentity = true,
                EnvironmentId = $"environment-{index}",
            }, CancellationToken.None);
        }

        await connection.EmitNotificationAsync("mcpServer/oauthLogin/completed", new
        {
            name = "overflow-server", threadId = "thread-overflow", environmentId = "environment-overflow", success = true,
        });
        InteractionAuthStatus afterUnstoredCompletion = await service.ReadGatewayOAuthAsync(CancellationToken.None);
        Assert.IsFalse(afterUnstoredCompletion.McpServers.Any(server =>
            server.ServerName == "overflow-server" && server.State == InteractionAuthState.Authenticated),
            "A completion whose authenticated status cannot be stored must not be published as authenticated.");
    }

    [TestMethod]
    public async Task McpOAuthLoginFailureOnDetachedConnection_DoesNotPublishOrMutateCurrentStatus()
    {
        var firstConnection = new RecordingConnection
        {
            InitializeResponse = JsonSerializer.SerializeToElement(new { userAgent = "codex-cli/0.171.0" }),
        };
        var requestStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finishRequest = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        firstConnection.AsyncHandler = (method, _, _) =>
        {
            if (method == "mcpServer/oauth/login")
            {
                requestStarted.TrySetResult();
                return finishRequest.Task;
            }

            return Task.FromResult(JsonSerializer.SerializeToElement(new { }));
        };
        var secondConnection = new RecordingConnection
        {
            InitializeResponse = JsonSerializer.SerializeToElement(new { userAgent = "codex-cli/0.171.0" }),
        };
        var statuses = new ConcurrentQueue<InteractionAuthStatus>();
        await using var service = CreateService();
        service.InteractionAuthStatusChanged += (status, _) =>
        {
            statuses.Enqueue(status);
            return Task.CompletedTask;
        };
        await service.InitializeAsync(firstConnection, Options(experimentalApi: true), CancellationToken.None);
        Task<McpOAuthLoginStatus> oldLogin = service.StartMcpOAuthLoginAsync(new StartMcpOAuthLoginRequest
        {
            ServerName = "docs", ThreadId = "thread-old", HasRegistrationIdentity = true, EnvironmentId = "environment-old",
        }, CancellationToken.None);
        await requestStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        await service.InitializeAsync(secondConnection, Options(experimentalApi: true), CancellationToken.None);
        int currentStatusEvents = statuses.Count;
        finishRequest.TrySetException(new InvalidOperationException("private server response detail"));
        await oldLogin.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.AreEqual(currentStatusEvents, statuses.Count,
            "A failure from a detached app-server context must not publish a stale auth status into the current UI.");
        InteractionAuthStatus current = await service.ReadGatewayOAuthAsync(CancellationToken.None);
        Assert.IsFalse(current.McpServers.Any(server => server.ThreadId == "thread-old"),
            "The replacement connection has no status mutation from the previous context.");
    }

    [TestMethod]
    public async Task McpAuthStatusCapacity_ProtectsLegacyThreadlessStatusWithPendingElicitation()
    {
        var connection = new RecordingConnection
        {
            InitializeResponse = JsonSerializer.SerializeToElement(new { userAgent = "codex-cli/0.170.2" }),
        };
        await using var service = CreateService();
        var promptReceived = new TaskCompletionSource<McpElicitationRequest>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.McpElicitationRequested += (request, _) =>
        {
            promptReceived.TrySetResult(request);
            return Task.CompletedTask;
        };
        await service.InitializeAsync(connection, Options(experimentalApi: false), CancellationToken.None);

        Task<JsonElement> pendingElicitation = connection.EmitRequestAsync("legacy-elicitation", "mcpServer/elicitation/request", new
        {
            serverName = "docs", mode = "url", elicitationId = "legacy-pending", message = "Sign in", url = "https://login.example.test/authorize", threadId = "thread-docs",
        });
        McpElicitationRequest prompt = await promptReceived.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.AreEqual("thread-docs", prompt.ThreadId);

        for (int index = 0; index < 1024; index++)
        {
            await connection.EmitNotificationAsync("mcpServer/startupStatus/updated", new
            {
                name = index == 0 ? "docs" : $"server-{index}",
                threadId = index == 0 ? "thread-docs" : $"thread-{index}",
                status = "ready",
            });
        }

        await service.StartMcpOAuthLoginAsync(new StartMcpOAuthLoginRequest { ServerName = "new-server" }, CancellationToken.None);
        InteractionAuthStatus afterEviction = await service.ReadGatewayOAuthAsync(CancellationToken.None);
        Assert.AreEqual(1024, afterEviction.McpServers.Count);
        Assert.IsTrue(afterEviction.McpServers.Any(server => server.ServerName == "docs"),
            "A pending legacy elicitation protects its normalized threadless auth-status key from LRU eviction.");
        Assert.IsFalse(pendingElicitation.IsCompleted, "The protected pending interaction remains unresolved after status eviction.");

        await service.ResolveMcpElicitationAsync(new ResolveMcpElicitationRequest
        {
            RequestId = prompt.RequestId,
            Action = McpElicitationAction.Cancel,
        }, CancellationToken.None);
        Assert.AreEqual("cancel", (await pendingElicitation.WaitAsync(TimeSpan.FromSeconds(2))).GetProperty("action").GetString());
    }

    [TestMethod]
    public async Task McpAuthStatusCapacity_LegacyOverflowKeepsNameRoutingAvailable()
    {
        var connection = new RecordingConnection
        {
            InitializeResponse = JsonSerializer.SerializeToElement(new { userAgent = "codex-cli/0.170.2" }),
        };
        await using var service = CreateService();
        await service.InitializeAsync(connection, Options(experimentalApi: false), CancellationToken.None);

        for (int index = 0; index < 1025; index++)
        {
            await connection.EmitNotificationAsync("mcpServer/startupStatus/updated", new
            {
                name = $"legacy-server-{index}",
                status = "failed",
            });
        }

        await connection.EmitNotificationAsync("mcpServer/startupStatus/updated", new
        {
            name = "legacy-server-1024", status = "ready",
        });

        InteractionAuthStatus status = await service.ReadGatewayOAuthAsync(CancellationToken.None);
        Assert.AreEqual(InteractionAuthState.Authenticated,
            status.McpServers.Single(server => server.ServerName == "legacy-server-1024").State,
            "Overflow uncertainty applies to environment-scoped identity-less routing, not the legacy name-only protocol.");
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
        var bothPrompts = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        service.McpElicitationRequested += (request, _) =>
        {
            prompts.Enqueue(request);
            if (prompts.Count == 2)
            {
                bothPrompts.TrySetResult();
            }

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
        await bothPrompts.Task.WaitAsync(TimeSpan.FromSeconds(2));

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
    public async Task McpRetirement_ThreadWideFailureRetiresOnlyThreadWideScope()
    {
        var connection = new RecordingConnection
        {
            InitializeResponse = JsonSerializer.SerializeToElement(new { userAgent = "codex-cli/0.171.0" }),
        };
        var prompts = new ConcurrentQueue<McpElicitationRequest>();
        var allPromptsReceived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var service = CreateService();
        service.McpElicitationRequested += (request, _) =>
        {
            prompts.Enqueue(request);
            if (prompts.Count == 3)
            {
                allPromptsReceived.TrySetResult();
            }

            return Task.CompletedTask;
        };
        await service.InitializeAsync(connection, Options(experimentalApi: true), CancellationToken.None);

        Task<JsonElement> threadWide = connection.EmitRequestAsync("threadwide-el", "mcpServer/elicitation/request", new
        {
            serverName = "docs", threadId = "thread-1", environmentId = (string?)null,
            mode = "url", elicitationId = "threadwide", message = "Sign in", url = "https://login.example.test/thread-wide",
        });
        Task<JsonElement> scoped = connection.EmitRequestAsync("scoped-el", "mcpServer/elicitation/request", new
        {
            serverName = "docs", threadId = "thread-1", environmentId = "env-a",
            mode = "url", elicitationId = "scoped", message = "Sign in", url = "https://login.example.test/scoped",
        });
        Task<JsonElement> otherThreadWide = connection.EmitRequestAsync("other-thread-el", "mcpServer/elicitation/request", new
        {
            serverName = "docs", threadId = "thread-2", environmentId = (string?)null,
            mode = "url", elicitationId = "other-thread-wide", message = "Sign in", url = "https://login.example.test/other-thread",
        });
        await allPromptsReceived.Task.WaitAsync(TimeSpan.FromSeconds(2));

        await connection.EmitNotificationAsync("mcpServer/startupStatus/updated", new
        {
            name = "docs", threadId = "thread-1",
            status = "failed", failureReason = "reauthenticationRequired",
        });

        Assert.IsFalse(threadWide.IsCompleted, "An omitted environmentId is not the explicit thread-wide registration identity.");
        Assert.IsFalse(scoped.IsCompleted);
        Assert.IsFalse(otherThreadWide.IsCompleted);

        await connection.EmitNotificationAsync("mcpServer/startupStatus/updated", new
        {
            name = "docs", threadId = "thread-1", environmentId = (string?)null,
            status = "failed", failureReason = "reauthenticationRequired",
        });

        Assert.AreEqual("cancel", (await threadWide.WaitAsync(TimeSpan.FromSeconds(2))).GetProperty("action").GetString());
        // EmitNotificationAsync awaits the Worker handler; resolving the unaffected forms below is a second, terminal-state barrier.
        Assert.IsFalse(scoped.IsCompleted, "A thread-wide failure must leave an environment-scoped elicitation pending in the same thread.");
        Assert.IsFalse(otherThreadWide.IsCompleted, "A thread-wide failure must not retire another thread's registration.");

        await service.ResolveMcpElicitationAsync(new ResolveMcpElicitationRequest
        {
            RequestId = prompts.Single(request => request.ThreadId == "thread-1" && request.EnvironmentId == "env-a").RequestId,
            Action = McpElicitationAction.Cancel,
        }, CancellationToken.None);
        await service.ResolveMcpElicitationAsync(new ResolveMcpElicitationRequest
        {
            RequestId = prompts.Single(request => request.ThreadId == "thread-2" && request.EnvironmentId is null).RequestId,
            Action = McpElicitationAction.Cancel,
        }, CancellationToken.None);
        Assert.AreEqual("cancel", (await scoped.WaitAsync(TimeSpan.FromSeconds(2))).GetProperty("action").GetString());
        Assert.AreEqual("cancel", (await otherThreadWide.WaitAsync(TimeSpan.FromSeconds(2))).GetProperty("action").GetString());
    }

    [TestMethod]
    public async Task McpRetirement_EnvironmentFailureWithoutThreadDoesNotCancelScopedElicitation()
    {
        var connection = new RecordingConnection
        {
            InitializeResponse = JsonSerializer.SerializeToElement(new { userAgent = "codex-cli/0.171.0" }),
        };
        var prompt = new TaskCompletionSource<McpElicitationRequest>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var service = CreateService();
        service.McpElicitationRequested += (request, _) =>
        {
            prompt.TrySetResult(request);
            return Task.CompletedTask;
        };
        await service.InitializeAsync(connection, Options(experimentalApi: true), CancellationToken.None);

        Task<JsonElement> elicitation = connection.EmitRequestAsync("thread-scoped-el", "mcpServer/elicitation/request", new
        {
            serverName = "docs", mode = "url", elicitationId = "thread-scoped", message = "Sign in",
            url = "https://login.example.test/scoped", threadId = "thread-a", environmentId = "env-a",
        });
        McpElicitationRequest request = await prompt.Task.WaitAsync(TimeSpan.FromSeconds(2));

        await connection.EmitNotificationAsync("mcpServer/startupStatus/updated", new
        {
            name = "docs", environmentId = "env-a", status = "failed", failureReason = "reauthenticationRequired",
        });

        Assert.IsFalse(elicitation.IsCompleted, "An environment-scoped status without threadId cannot infer the pending form's thread.");
        await service.ResolveMcpElicitationAsync(new ResolveMcpElicitationRequest
        {
            RequestId = request.RequestId,
            Action = McpElicitationAction.Cancel,
        }, CancellationToken.None);
        Assert.AreEqual("cancel", (await elicitation.WaitAsync(TimeSpan.FromSeconds(2))).GetProperty("action").GetString());
    }

    [TestMethod]
    public async Task McpRetirement_OlderTiersRetireAllSameNameElicitations()
    {
        var connection = new RecordingConnection
        {
            InitializeResponse = JsonSerializer.SerializeToElement(new { userAgent = "codex-cli/0.170.2" }),
        };
        var prompts = new ConcurrentQueue<McpElicitationRequest>();
        var bothPrompts = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var service = CreateService();
        service.McpElicitationRequested += (request, _) =>
        {
            prompts.Enqueue(request);
            if (prompts.Count == 2)
            {
                bothPrompts.TrySetResult();
            }

            return Task.CompletedTask;
        };
        await service.InitializeAsync(connection, Options(experimentalApi: true), CancellationToken.None);

        Task<JsonElement> first = connection.EmitRequestAsync("legacy-el-1", "mcpServer/elicitation/request", new
        {
            serverName = "docs", mode = "url", elicitationId = "legacy-1", message = "Sign in", url = "https://login.example.test/a", threadId = "thread-1",
        });
        Task<JsonElement> second = connection.EmitRequestAsync("legacy-el-2", "mcpServer/elicitation/request", new
        {
            serverName = "docs", mode = "url", elicitationId = "legacy-2", message = "Sign in", url = "https://login.example.test/b", threadId = "thread-2",
        });
        await bothPrompts.Task.WaitAsync(TimeSpan.FromSeconds(2));

        await connection.EmitNotificationAsync("mcpServer/startupStatus/updated", new
        {
            name = "docs", threadId = "thread-1", status = "failed", failureReason = "reauthenticationRequired",
        });

        Assert.AreEqual("cancel", (await first.WaitAsync(TimeSpan.FromSeconds(2))).GetProperty("action").GetString());
        Assert.IsFalse(second.IsCompleted, "A legacy notification with thread identity must not retire another thread's elicitation.");
        await service.ResolveMcpElicitationAsync(new ResolveMcpElicitationRequest
        {
            RequestId = prompts.Single(request => request.ThreadId == "thread-2").RequestId,
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
        InteractionAuthStatus status = await service.ReadGatewayOAuthAsync(CancellationToken.None);
        Assert.AreEqual("docs-private", status.McpServers.Single().ServerName);
        Assert.AreEqual("[REDACTED]", status.McpServers.Single().DisplayServerName);
    }

    private static RecordingConnection EnvironmentIdentityConnection(
        object first,
        object? second = null,
        string version = "0.171.0",
        Func<string?, string>? oauthLoginIdFactory = null)
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
                    loginId = oauthLoginIdFactory?.Invoke(environmentId) ?? environmentId ?? "thread-wide",
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
        await PrimeLegacyMcpServerNameAsync(service, connection, "example-mcp");

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
            await PrimeLegacyMcpServerNameAsync(service, connection, "example-mcp");

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
        await PrimeLegacyMcpServerNameAsync(service, connection, "example-mcp");
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
        await PrimeLegacyMcpServerNameAsync(service, connection, "example-mcp");

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
        await PrimeLegacyMcpServerNameAsync(service, connection, "example-mcp");
        McpOAuthLoginStatus started = await service.StartMcpOAuthLoginAsync(
            new StartMcpOAuthLoginRequest { ServerName = "example-mcp" },
            CancellationToken.None);
        Assert.AreEqual(InteractionAuthState.LoginPending, started.State);
        return (connection, service, statuses);
    }

    private static McpServerAuthStatus LastMcpStatus(ConcurrentQueue<InteractionAuthStatus> statuses) =>
        statuses.Last().McpServers.Single(server => server.ServerName == "example-mcp");

    private static async Task PrimeLegacyMcpServerNameAsync(CodexSessionService service, RecordingConnection connection, string serverName)
    {
        Func<string, object?, CancellationToken, Task<JsonElement>>? previousHandler = connection.AsyncHandler;
        connection.AsyncHandler = (method, parameters, cancellationToken) => method == "mcpServerStatus/list"
            ? Task.FromResult(JsonSerializer.SerializeToElement(new { data = new[] { new { name = serverName } }, nextCursor = (string?)null }))
            : previousHandler is null
                ? Task.FromResult(JsonSerializer.SerializeToElement(new { }))
                : previousHandler(method, parameters, cancellationToken);
        await service.ListMcpServersAsync(new ListMcpServersRequest(), CancellationToken.None);
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
        await PrimeLegacyMcpServerNameAsync(service, connection, "example-mcp");

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

    private sealed class MutableTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        private DateTimeOffset currentUtcNow = utcNow;

        public override DateTimeOffset GetUtcNow() => currentUtcNow;

        public void Advance(TimeSpan amount) => currentUtcNow += amount;
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
