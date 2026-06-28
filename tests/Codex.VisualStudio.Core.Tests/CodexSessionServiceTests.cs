using System.Text.Json;
using Codex.AppServer.Protocol;
using Codex.VisualStudio.Contracts;
using Codex.VisualStudio.Worker;
using StreamJsonRpc;

namespace Codex.VisualStudio.Core.Tests;

[TestClass]
public sealed class CodexSessionServiceTests
{
    private static readonly JsonSerializerOptions WireJsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    private static readonly string[] ExpectedModels = ["gpt-5-codex", "gpt-5"];
    private static readonly string[] CreativeOnly = ["Creative"];

    [TestMethod]
    public async Task ThreadListUsesPagingAndAllSupportedSources()
    {
        var connection = new RecordingConnection
        {
            Handler = (method, _) => method == "thread/list"
                ? JsonSerializer.SerializeToElement(new
                {
                    data = Array.Empty<object>(),
                    nextCursor = "next",
                })
                : JsonSerializer.SerializeToElement(new { }),
        };
        await using var service = CreateService();
        await service.InitializeAsync(connection, Options(), CancellationToken.None);

        ThreadPage page = await service.ListThreadsAsync("cursor", CancellationToken.None);

        var matching = connection.Requests.Where(item => item.Method == "thread/list").ToList();
        Assert.AreEqual(1, matching.Count);
        RecordedRequest request = matching[0];
        JsonElement parameters = JsonSerializer.SerializeToElement(request.Parameters);
        Assert.AreEqual(25, parameters.GetProperty("limit").GetInt32());
        Assert.AreEqual("cursor", parameters.GetProperty("cursor").GetString());
        CollectionAssert.AreEqual(
            new[] { "cli", "vscode", "appServer" },
            parameters.GetProperty("sourceKinds").EnumerateArray().Select(item => item.GetString()).ToArray());
        Assert.AreEqual("next", page.NextCursor);
    }

    [TestMethod]
    public async Task ListModelsReturnsModelsAndDefault()
    {
        var connection = new RecordingConnection
        {
            Handler = (method, _) => method == "model/list"
                ? JsonSerializer.SerializeToElement(new
                {
                    models = new[]
                    {
                        new { id = "gpt-5-codex", displayName = "GPT-5 Codex" },
                        new { id = "gpt-5", displayName = "GPT-5" },
                    },
                    defaultModel = "gpt-5",
                })
                : JsonSerializer.SerializeToElement(new { }),
        };
        await using var service = CreateService();
        await service.InitializeAsync(connection, Options(), CancellationToken.None);

        ListModelsResult result = await service.ListModelsAsync(CancellationToken.None);

        CollectionAssert.AreEqual(ExpectedModels, result.Models.Select(model => model.Id).ToArray());
        Assert.AreEqual("GPT-5 Codex", result.Models[0].DisplayName);
        Assert.AreEqual("gpt-5", result.DefaultModel);
    }

    [TestMethod]
    public async Task ListModelsDropsMalformedAndDuplicateModels()
    {
        var connection = new RecordingConnection
        {
            Handler = (method, _) => method == "model/list"
                ? JsonSerializer.SerializeToElement(new
                {
                    models = new object[]
                    {
                        new { id = "gpt-5-codex" },
                        new { id = "gpt-5-codex" },
                        new { id = "" },
                        new { id = "bad\rmodel" },
                        new { displayName = "No id" },
                        new { id = "gpt-5" },
                    },
                    defaultModel = "missing",
                })
                : JsonSerializer.SerializeToElement(new { }),
        };
        await using var service = CreateService();
        await service.InitializeAsync(connection, Options(), CancellationToken.None);

        ListModelsResult result = await service.ListModelsAsync(CancellationToken.None);

        CollectionAssert.AreEqual(ExpectedModels, result.Models.Select(model => model.Id).ToArray());
        Assert.IsNull(result.DefaultModel);
    }

    [TestMethod]
    public async Task ListModelsReturnsEmptyWhenAppServerDoesNotSupportMethod()
    {
        var connection = new RecordingConnection
        {
            Handler = (method, _) => method == "model/list"
                ? throw new JsonRpcRemoteException(-32601, "Method not found")
                : JsonSerializer.SerializeToElement(new { }),
        };
        await using var service = CreateService();
        await service.InitializeAsync(connection, Options(), CancellationToken.None);

        ListModelsResult result = await service.ListModelsAsync(CancellationToken.None);

        Assert.AreEqual(0, result.Models.Count);
        Assert.IsNull(result.DefaultModel);
    }

    [TestMethod]
    public async Task StartTurnForwardsModelAndProfileWhenSet()
    {
        var connection = new RecordingConnection
        {
            Handler = (method, _) => method == "turn/start"
                ? JsonSerializer.SerializeToElement(new { turn = new { id = "turn-1" } })
                : JsonSerializer.SerializeToElement(new { }),
        };
        await using var service = CreateService();
        await service.InitializeAsync(connection, Options(), CancellationToken.None);

        await service.StartTurnAsync(
            new StartTurnRequest { ThreadId = "thread-1", Text = "hello", Model = "gpt-5", Profile = "chat" },
            CancellationToken.None);

        RecordedRequest request = connection.Requests.Single(item => item.Method == "turn/start");
        JsonElement parameters = JsonSerializer.SerializeToElement(request.Parameters, WireJsonOptions);
        Assert.AreEqual("gpt-5", parameters.GetProperty("model").GetString());
        Assert.AreEqual("chat", parameters.GetProperty("profile").GetString());
    }

    [TestMethod]
    public async Task StartTurnOmitsModelAndProfileWhenUnset()
    {
        var connection = new RecordingConnection
        {
            Handler = (method, _) => method == "turn/start"
                ? JsonSerializer.SerializeToElement(new { turn = new { id = "turn-1" } })
                : JsonSerializer.SerializeToElement(new { }),
        };
        await using var service = CreateService();
        await service.InitializeAsync(connection, Options(), CancellationToken.None);

        await service.StartTurnAsync(new StartTurnRequest { ThreadId = "thread-1", Text = "hello" }, CancellationToken.None);

        RecordedRequest request = connection.Requests.Single(item => item.Method == "turn/start");
        JsonElement parameters = JsonSerializer.SerializeToElement(request.Parameters, WireJsonOptions);
        Assert.IsFalse(parameters.TryGetProperty("model", out _));
        Assert.IsFalse(parameters.TryGetProperty("profile", out _));
    }

    [TestMethod]
    public async Task SteerRejectsStaleExpectedTurn()
    {
        var connection = new RecordingConnection
        {
            Handler = (method, _) => method == "turn/start"
                ? JsonSerializer.SerializeToElement(new { turn = new { id = "turn-1" } })
                : JsonSerializer.SerializeToElement(new { }),
        };
        await using var service = CreateService();
        await service.InitializeAsync(connection, Options(), CancellationToken.None);
        await service.StartTurnAsync(new StartTurnRequest { ThreadId = "thread-1", Text = "hello" }, CancellationToken.None);

        bool threw = false;
        try
        {
            await service.SteerTurnAsync(
                new SteerTurnRequest { ThreadId = "thread-1", ExpectedTurnId = "stale", Text = "more" },
                CancellationToken.None);
        }
        catch (InvalidOperationException)
        {
            threw = true;
        }

        Assert.IsTrue(threw, "Expected InvalidOperationException for stale turn.");
    }

    [TestMethod]
    public async Task AccountReadMapsSignedOutAndSignedInWithoutPersonalInformation()
    {
        bool signedIn = false;
        var connection = new RecordingConnection
        {
            Handler = (method, _) => method == "account/read"
                ? signedIn
                    ? JsonSerializer.SerializeToElement(new { account = new { type = "chatgpt", email = "secret@example.com", planType = "plus" } })
                    : JsonSerializer.SerializeToElement(new { account = (object?)null, requiresOpenaiAuth = true })
                : JsonSerializer.SerializeToElement(new { }),
        };
        await using var service = CreateService();
        await service.InitializeAsync(connection, Options(), CancellationToken.None);

        AccountStatus signedOut = await service.GetAccountStatusAsync(CancellationToken.None);
        signedIn = true;
        AccountStatus signedInStatus = await service.GetAccountStatusAsync(CancellationToken.None);

        Assert.AreEqual(AccountState.SignedOut, signedOut.State);
        Assert.AreEqual(AccountState.SignedIn, signedInStatus.State);
        Assert.AreEqual("plus", signedInStatus.PlanType);
    }

    [TestMethod]
    public async Task AccountReadAllowsMissingPlanAndUnknownFields()
    {
        var connection = new RecordingConnection
        {
            Handler = (method, _) => method == "account/read"
                ? JsonSerializer.SerializeToElement(new { account = new { type = "future", unknown = 42 } })
                : JsonSerializer.SerializeToElement(new { }),
        };
        await using var service = CreateService();
        await service.InitializeAsync(connection, Options(), CancellationToken.None);

        AccountStatus status = await service.GetAccountStatusAsync(CancellationToken.None);

        Assert.AreEqual(AccountState.SignedIn, status.State);
        Assert.IsNull(status.PlanType);
    }

    [TestMethod]
    public async Task LoginStartUsesChatgptAndRejectsInsecureUrl()
    {
        var connection = new RecordingConnection
        {
            Handler = (method, _) => method == "account/login/start"
                ? JsonSerializer.SerializeToElement(new { type = "chatgpt", loginId = "login-1", authUrl = "http://example.com/login" })
                : JsonSerializer.SerializeToElement(new { }),
        };
        await using var service = CreateService();
        await service.InitializeAsync(connection, Options(), CancellationToken.None);

        StartAccountLoginResult result = await service.StartAccountLoginAsync(CancellationToken.None);

        RecordedRequest request = connection.Requests.Single(item => item.Method == "account/login/start");
        JsonElement parameters = JsonSerializer.SerializeToElement(request.Parameters);
        Assert.AreEqual("chatgpt", parameters.GetProperty("type").GetString());
        Assert.AreEqual(AccountState.Unavailable, result.Status.State);
        Assert.IsNull(result.AuthUrl);
    }

    [TestMethod]
    public async Task LoginStartReturnsSecureChatgptBrowserUrl()
    {
        var connection = new RecordingConnection
        {
            Handler = (method, _) => method == "account/login/start"
                ? JsonSerializer.SerializeToElement(new { type = "chatgpt", loginId = "login-1", authUrl = "https://example.com/login" })
                : JsonSerializer.SerializeToElement(new { }),
        };
        await using var service = CreateService();
        await service.InitializeAsync(connection, Options(), CancellationToken.None);

        StartAccountLoginResult result = await service.StartAccountLoginAsync(CancellationToken.None);

        Assert.AreEqual(AccountState.SigningIn, result.Status.State);
        Assert.AreEqual("login-1", result.LoginId);
        Assert.AreEqual("https://example.com/login", result.AuthUrl);
    }

    [TestMethod]
    public async Task LoginStartContinuesWhenAccountStatusObserverFails()
    {
        var connection = new RecordingConnection
        {
            Handler = (method, _) => method == "account/login/start"
                ? JsonSerializer.SerializeToElement(new { type = "chatgpt", loginId = "login-1", authUrl = "https://example.com/login" })
                : JsonSerializer.SerializeToElement(new { }),
        };
        await using var service = CreateService();
        service.AccountStatusChanged += (_, _) => throw new InvalidOperationException("observer failed");
        await service.InitializeAsync(connection, Options(), CancellationToken.None);

        StartAccountLoginResult result = await service.StartAccountLoginAsync(CancellationToken.None);

        Assert.AreEqual(AccountState.SigningIn, result.Status.State);
        Assert.IsTrue(connection.Requests.Any(item => item.Method == "account/login/start"));
    }

    [TestMethod]
    public async Task LogoutRequestsAccountLogoutAndRefreshesSignedOutStatus()
    {
        bool signedIn = true;
        var connection = new RecordingConnection
        {
            Handler = (method, _) =>
            {
                if (method == "account/logout")
                {
                    signedIn = false;
                    return JsonSerializer.SerializeToElement(new { });
                }

                return method == "account/read"
                    ? JsonSerializer.SerializeToElement(new
                    {
                        account = signedIn ? new { type = "chatgpt", planType = "plus" } : null,
                        requiresOpenaiAuth = true,
                    })
                    : JsonSerializer.SerializeToElement(new { });
            },
        };
        await using var service = CreateService();
        await service.InitializeAsync(connection, Options(), CancellationToken.None);

        AccountStatus result = await service.LogoutAccountAsync(CancellationToken.None);

        Assert.AreEqual(AccountState.SignedOut, result.State);
        CollectionAssert.AreEqual(
            new[] { "initialize", "account/logout", "account/read" },
            connection.Requests.Select(item => item.Method).ToArray());
    }

    [TestMethod]
    public async Task AccountNotificationsRefreshStatus()
    {
        int reads = 0;
        var connection = new RecordingConnection
        {
            Handler = (method, _) =>
            {
                if (method == "account/read")
                {
                    reads++;
                    return JsonSerializer.SerializeToElement(new { account = new { type = "chatgpt", planType = "pro" } });
                }

                return JsonSerializer.SerializeToElement(new { });
            },
        };
        await using var service = CreateService();
        await service.InitializeAsync(connection, Options(), CancellationToken.None);

        await connection.EmitNotificationAsync("account/login/completed", new { loginId = "login-1", success = true });
        await connection.EmitNotificationAsync("account/updated", new { authMode = "chatgpt", planType = "pro" });

        Assert.AreEqual(2, reads);
    }

    [TestMethod]
    public async Task AccountReadFailureReturnsUnavailable()
    {
        var connection = new RecordingConnection
        {
            Handler = (method, _) => method == "account/read"
                ? throw new InvalidOperationException("unsupported")
                : JsonSerializer.SerializeToElement(new { }),
        };
        await using var service = CreateService();
        await service.InitializeAsync(connection, Options(), CancellationToken.None);

        AccountStatus status = await service.GetAccountStatusAsync(CancellationToken.None);

        Assert.AreEqual(AccountState.Unavailable, status.State);
    }

    [TestMethod]
    public async Task ScopedApproval_EmitsGrantAndAutoApprovalAuditRecords()
    {
        var connection = new RecordingConnection();
        await using var service = CreateService();
        var audit = new List<ApprovalAuditRecord>();
        service.ApprovalAuditRecorded += (record, _) =>
        {
            audit.Add(record);
            return Task.CompletedTask;
        };
        service.ApprovalRequested += (request, cancellationToken) => service.ResolveApprovalAsync(
            new ResolveApprovalRequest
            {
                RequestId = request.RequestId,
                Decision = ApprovalDecision.AcceptForThread,
            },
            cancellationToken);
        await service.InitializeAsync(connection, Options(), CancellationToken.None);
        string cwd = Options().WorkingDirectory;

        await connection.EmitRequestAsync(
            "approval-1",
            "item/commandExecution/requestApproval",
            new { command = "dotnet build", cwd, threadId = "thread-1", turnId = "turn-1" });
        await connection.EmitRequestAsync(
            "approval-2",
            "item/commandExecution/requestApproval",
            new { command = "dotnet build", cwd, threadId = "thread-1", turnId = "turn-2" });

        Assert.AreEqual(2, audit.Count);
        Assert.AreEqual(ApprovalAuditAction.GrantCreated, audit[0].Action);
        Assert.AreEqual(ApprovalScope.Thread, audit[0].Scope);
        Assert.AreEqual(ApprovalAuditAction.AutoApproved, audit[1].Action);
        Assert.AreEqual(ApprovalScope.Thread, audit[1].Scope);
    }

    [TestMethod]
    public async Task UserInputRequest_ParsesQuestionsAndReturnsValidatedSelectedLabel()
    {
        var connection = new RecordingConnection();
        await using var service = CreateService();
        UserInputRequest? captured = null;
        service.UserInputRequested += (request, cancellationToken) =>
        {
            captured = request;

            // Simulate the user picking the second option, plus an invalid label that must be filtered.
            return service.ResolveUserInputAsync(
                new ResolveUserInputRequest
                {
                    RequestId = request.RequestId,
                    Answers = new Dictionary<string, string[]>
                    {
                        [request.Questions[0].Id] = [request.Questions[0].Options[1].Label, "not-an-option"],
                    },
                },
                cancellationToken);
        };
        await service.InitializeAsync(connection, Options(), CancellationToken.None);

        JsonElement result = await connection.EmitRequestAsync(
            "ui-1",
            "tool/requestUserInput",
            new
            {
                itemId = "item-1",
                threadId = "thread-1",
                turnId = "turn-1",
                questions = new[]
                {
                    new
                    {
                        id = "q1",
                        header = "Direction",
                        question = "Which portfolio style?",
                        options = new[]
                        {
                            new { label = "Sharp", description = "Black/white/red" },
                            new { label = "Creative", description = "Bold visuals" },
                        },
                    },
                },
            });

        Assert.IsNotNull(captured);
        Assert.AreEqual(1, captured!.Questions.Count);
        Assert.AreEqual(2, captured.Questions[0].Options.Count);

        // Response shape: { answers: { <id>: { answers: [<label>] } } } with only valid, single-select labels.
        string[] selected = result
            .GetProperty("answers")
            .GetProperty("q1")
            .GetProperty("answers")
            .EnumerateArray()
            .Select(item => item.GetString()!)
            .ToArray();
        CollectionAssert.AreEqual(CreativeOnly, selected);
    }

    private static CodexSessionService CreateService()
        => new(new ApprovalPolicyEngine(new PathAccessPolicy()), new SecretRedactor());

    private static WorkerOptions Options() => new()
    {
        WorkingDirectory = Path.GetTempPath(),
        ExtensionVersion = "test",
    };

    private sealed class RecordingConnection : IJsonRpcConnection
    {
        public event Func<JsonRpcMessage, CancellationToken, Task>? NotificationReceived;

        public event Func<JsonRpcMessage, CancellationToken, Task<JsonElement>>? RequestReceived;

        public event EventHandler<Exception?>? Closed
        {
            add { }
            remove { }
        }

        public Func<string, object?, JsonElement> Handler { get; set; } = (_, _) => JsonSerializer.SerializeToElement(new { });

        public List<RecordedRequest> Requests { get; } = new();

        public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<JsonElement> SendRequestAsync(string method, object? parameters, TimeSpan timeout, CancellationToken cancellationToken)
        {
            Requests.Add(new RecordedRequest(method, parameters));
            return Task.FromResult(Handler(method, parameters));
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

    private sealed record RecordedRequest(string Method, object? Parameters);
}
