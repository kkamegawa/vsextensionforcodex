using System.Globalization;
using System.Reflection;
using System.Text.Json;
using Codex.AppServer.Protocol;
using Codex.VisualStudio.Contracts;
using Codex.VisualStudio.Worker;

namespace Codex.VisualStudio.Core.Tests;

public sealed partial class CodexSessionServiceTests
{
    [TestMethod]
    public void RedactAndBoundDisplayTextHonorsLengthAndSurrogateBoundaries()
    {
        const int maximumLength = 2_048;
        const string secret = "private-token";
        var redactor = new SecretRedactor();
        using IDisposable registeredSecret = redactor.RegisterSecret(secret);

        string exactlyAtLimit = string.Concat(new string('x', maximumLength - "[REDACTED]".Length), secret);
        string? exactResult = CodexSessionService.RedactAndBoundDisplayText(redactor, exactlyAtLimit);
        Assert.AreEqual(maximumLength, exactResult?.Length);
        Assert.IsTrue(exactResult!.EndsWith("[REDACTED]", StringComparison.Ordinal));
        Assert.IsFalse(exactResult.Contains(secret, StringComparison.Ordinal));

        string? longResult = CodexSessionService.RedactAndBoundDisplayText(redactor, new string('x', maximumLength + 20));
        Assert.AreEqual(maximumLength, longResult?.Length);
        Assert.IsTrue(longResult!.EndsWith('…'));

        string splitSurrogate = string.Concat(new string('x', maximumLength - 2), "😀", "tail");
        string? safeResult = CodexSessionService.RedactAndBoundDisplayText(redactor, splitSurrogate);
        Assert.AreEqual(maximumLength - 1, safeResult?.Length);
        Assert.IsTrue(safeResult!.EndsWith('…'));
        Assert.IsFalse(safeResult.Any(char.IsSurrogate), "Truncation must not leave half of a Unicode surrogate pair.");

        string? sanitized = CodexSessionService.RedactAndBoundDisplayText(redactor, "first line\r\nsecond line\u202E spoofed");
        Assert.IsFalse(sanitized!.Any(char.IsControl));
        Assert.IsFalse(sanitized!.Any(character => char.GetUnicodeCategory(character) == System.Globalization.UnicodeCategory.Format));
        StringAssert.Contains(sanitized!, "first line  second line  spoofed");
        Assert.IsNull(CodexSessionService.RedactAndBoundDisplayText(redactor, null));
        Assert.IsNull(CodexSessionService.RedactAndBoundDisplayText(redactor, string.Empty));
        Assert.AreEqual("…", CodexSessionService.RedactAndBoundDisplayText(redactor, "long", maximumLength: 1));
    }

    [TestMethod]
    public async Task CompletedAssistantItemCarriesOnlyPresentNonnegativeContentTokenCounts()
    {
        var connection = new RecordingConnection
        {
            Handler = (method, _) => method switch
            {
                "initialize" => JsonSerializer.SerializeToElement(new
                {
                    userAgent = $"codex-cli/{SupportedCodexVersions.Minimum}",
                }),
                "thread/start" => JsonSerializer.SerializeToElement(new { thread = new { id = "thread-token-count" } }),
                _ => JsonSerializer.SerializeToElement(new { }),
            },
        };
        await using var service = CreateService();
        await service.InitializeAsync(connection, Options(), CancellationToken.None);
        ThreadSummary thread = await service.StartThreadAsync(CancellationToken.None);
        var events = new List<ConversationEvent>();
        service.ConversationEventReceived += (value, _) =>
        {
            events.Add(value);
            return Task.CompletedTask;
        };

        await connection.EmitNotificationAsync("item/completed", new
        {
            threadId = thread.Id,
            turnId = "turn-count-zero",
            contentTokenCount = 0,
            item = new { id = "assistant-zero", type = "agentMessage", text = "zero" },
        });
        await connection.EmitNotificationAsync("item/completed", new
        {
            threadId = thread.Id,
            turnId = "turn-count-present",
            contentTokenCount = 17,
            item = new { id = "assistant-present", type = "agentMessage", text = "answer" },
        });
        await connection.EmitNotificationAsync("item/completed", new
        {
            threadId = thread.Id,
            turnId = "turn-count-missing",
            item = new { id = "assistant-missing", type = "agentMessage", text = "legacy" },
        });
        await connection.EmitNotificationAsync("item/completed", new
        {
            threadId = thread.Id,
            turnId = "turn-count-negative",
            contentTokenCount = -1,
            item = new { id = "assistant-negative", type = "agentMessage", text = "invalid" },
        });
        await connection.EmitNotificationAsync("item/completed", new
        {
            threadId = thread.Id,
            turnId = "turn-count-fraction",
            contentTokenCount = 1.5,
            item = new { id = "assistant-fraction", type = "agentMessage", text = "invalid fraction" },
        });
        await connection.EmitNotificationAsync("item/completed", new
        {
            threadId = thread.Id,
            turnId = "turn-count-string",
            contentTokenCount = "17",
            item = new { id = "assistant-string", type = "agentMessage", text = "invalid string" },
        });
        await connection.EmitNotificationAsync("item/completed", new
        {
            threadId = thread.Id,
            turnId = "turn-count-overflow",
            contentTokenCount = 9_223_372_036_854_775_808UL,
            item = new { id = "assistant-overflow", type = "agentMessage", text = "invalid overflow" },
        });
        await connection.EmitNotificationAsync("item/completed", new
        {
            threadId = thread.Id,
            turnId = "turn-count-tool",
            contentTokenCount = 99,
            item = new { id = "tool", type = "commandExecution", command = "echo test" },
        });

        ConversationEvent[] assistantEvents = events.Where(value => value.IsAgentMessage).ToArray();
        Assert.AreEqual(7, assistantEvents.Length);
        Assert.AreEqual(0, assistantEvents[0].ContentTokenCount);
        Assert.AreEqual(17, assistantEvents[1].ContentTokenCount);
        Assert.IsTrue(assistantEvents.Skip(2).All(value => value.ContentTokenCount is null), string.Join(", ", assistantEvents.Select(value => $"{value.ItemId}={value.ContentTokenCount?.ToString(CultureInfo.InvariantCulture) ?? "null"}")));
        Assert.IsTrue(events.Where(value => !value.IsAgentMessage).All(value => value.ContentTokenCount is null));
    }

    [TestMethod]
    public async Task AccountSecuritySetupNoticeRequiresBothCurrentIdentitiesAndIsReadOnce()
    {
        int accountReads = 0;
        var connection = new RecordingConnection
        {
            Handler = (method, _) => method switch
            {
                "initialize" => JsonSerializer.SerializeToElement(new
                {
                    userAgent = $"codex-cli/{SupportedCodexVersions.Minimum}",
                }),
                "account/read" => AccountRead(++accountReads),
                "account/securitySetup/read" => CreateNoticeResponse(
                    noticeAccountId: "account-1",
                    noticeUserId: "user-1",
                    url: "https://security.example.test/setup"),
                _ => JsonSerializer.SerializeToElement(new { }),
            },
        };
        await using var service = CreateService();
        await service.InitializeAsync(connection, Options(), CancellationToken.None);
        Task<AccountStatus> noticePublished = WaitForAccountSecurityNoticeAsync(service, "user-1");
        AccountStatus first = await service.GetAccountStatusAsync(CancellationToken.None);
        AccountStatus published = await noticePublished.WaitAsync(TimeSpan.FromSeconds(5));
        AccountStatus second = await service.GetAccountStatusAsync(CancellationToken.None);

        Assert.AreEqual(AccountState.SignedIn, first.State);
        Assert.IsNull(first.SecuritySetupNotice, "The account status must not wait for the optional notice read.");
        Assert.AreEqual("account-1", published.SecuritySetupNotice?.AccountId);
        Assert.AreEqual("user-1", published.SecuritySetupNotice?.UserId);
        Assert.AreEqual("Secure this account", published.SecuritySetupNotice?.Title);
        Assert.AreEqual("https://security.example.test/setup", published.SecuritySetupNotice?.ActionUrl);
        Assert.IsNotNull(second.SecuritySetupNotice);
        Assert.AreEqual(2, accountReads);
        Assert.AreEqual(1, connection.Requests.Count(request => request.Method == "account/securitySetup/read"));
    }

    [TestMethod]
    public async Task AccountSecuritySetupNoticeWaitsForUserIdentityAndRetriesOnRoutingReady()
    {
        int accountReads = 0;
        var connection = new RecordingConnection
        {
            Handler = (method, _) => method switch
            {
                "initialize" => JsonSerializer.SerializeToElement(new { userAgent = $"codex-cli/{SupportedCodexVersions.Tier171}" }),
                "account/read" => JsonSerializer.SerializeToElement(new
                {
                    account = new { type = "chatgpt", email = "owner@example.test", planType = "plus", workspaceName = "Workspace" },
                    userId = Interlocked.Increment(ref accountReads) == 1 ? null : "user-1",
                    workspaceRouting = new { chatgptAccountId = "account-1" },
                }),
                "account/securitySetup/read" => CreateNoticeResponse("account-1", "user-1", "https://security.example.test/setup"),
                _ => JsonSerializer.SerializeToElement(new { }),
            },
        };
        await using var service = CreateService();
        await service.InitializeAsync(connection, Options(), CancellationToken.None);
        var statuses = new List<AccountStatus>();
        var noticePublished = new TaskCompletionSource<AccountStatus>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.AccountStatusChanged += (value, _) =>
        {
            statuses.Add(value);
            if (value.SecuritySetupNotice is not null)
            {
                noticePublished.TrySetResult(value);
            }

            return Task.CompletedTask;
        };

        AccountStatus initial = await service.GetAccountStatusAsync(CancellationToken.None);
        Assert.IsNull(initial.SecuritySetupNotice);
        Assert.AreEqual(0, connection.Requests.Count(request => request.Method == "account/securitySetup/read"));

        await connection.EmitNotificationAsync("account/updated", new
        {
            reason = "workspaceRoutingReady",
            authMode = "chatgpt",
            planType = "plus",
        });

        AccountStatus published = await noticePublished.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.AreEqual(1, connection.Requests.Count(request => request.Method == "account/securitySetup/read"));
        Assert.AreEqual("user-1", published.SecuritySetupNotice?.UserId);
        Assert.AreEqual(2, accountReads);
    }

    [TestMethod]
    public async Task AccountSecuritySetupNoticeRetriesAfterTransientReadFailure()
    {
        int securityReads = 0;
        var connection = new RecordingConnection
        {
            Handler = (method, _) => method switch
            {
                "initialize" => JsonSerializer.SerializeToElement(new { userAgent = $"codex-cli/{SupportedCodexVersions.Minimum}" }),
                "account/read" => AccountRead(1),
                "account/securitySetup/read" => Interlocked.Increment(ref securityReads) == 1
                    ? throw new IOException("temporary notice read failure")
                    : CreateNoticeResponse("account-1", "user-1", "https://security.example.test/setup"),
                _ => JsonSerializer.SerializeToElement(new { }),
            },
        };
        await using var service = CreateService();
        await service.InitializeAsync(connection, Options(), CancellationToken.None);
        Task<AccountStatus> noticePublished = WaitForAccountSecurityNoticeAsync(service, "user-1");

        AccountStatus first = await service.GetAccountStatusAsync(CancellationToken.None);
        await WaitForSecuritySetupReadCompletionAsync(service, expectedAttempts: 1);
        AccountStatus second = await service.GetAccountStatusAsync(CancellationToken.None);
        AccountStatus published = await noticePublished.WaitAsync(TimeSpan.FromSeconds(5));
        await WaitForSecuritySetupReadCompletionAsync(service, expectedAttempts: 2);

        Assert.IsNull(first.SecuritySetupNotice);
        Assert.IsNotNull(published.SecuritySetupNotice);
        Assert.AreEqual(2, securityReads);
    }

    [TestMethod]
    public async Task AccountSecuritySetupTransientFailuresAreBoundedPerIdentity()
    {
        int securityReads = 0;
        var unexpectedThirdRead = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var connection = new RecordingConnection
        {
            AsyncHandler = (method, _, _) => method switch
            {
                "initialize" => Task.FromResult(JsonSerializer.SerializeToElement(new { userAgent = $"codex-cli/{SupportedCodexVersions.Minimum}" })),
                "account/read" => Task.FromResult(AccountRead(1)),
                "account/securitySetup/read" => FailNoticeReadAsync(),
                _ => Task.FromResult(JsonSerializer.SerializeToElement(new { })),
            },
        };
        Task<JsonElement> FailNoticeReadAsync()
        {
            if (Interlocked.Increment(ref securityReads) == 3)
            {
                unexpectedThirdRead.TrySetResult();
            }

            return Task.FromException<JsonElement>(new IOException("temporary notice read failure"));
        }

        await using var service = CreateService();
        await service.InitializeAsync(connection, Options(), CancellationToken.None);

        _ = await service.GetAccountStatusAsync(CancellationToken.None);
        await WaitForSecuritySetupReadCompletionAsync(service, expectedAttempts: 1);
        _ = await service.GetAccountStatusAsync(CancellationToken.None);
        await WaitForSecuritySetupReadCompletionAsync(service, expectedAttempts: 2);
        _ = await service.GetAccountStatusAsync(CancellationToken.None);
        await WaitForSecuritySetupReadCompletionAsync(service, expectedAttempts: 2);
        Task completedOrUnexpectedRead = await Task.WhenAny(
            unexpectedThirdRead.Task,
            Task.Delay(TimeSpan.FromMilliseconds(250)));

        Assert.AreNotSame(unexpectedThirdRead.Task, completedOrUnexpectedRead, "The exhausted identity retry budget must not schedule a third notice read.");
        Assert.AreEqual(2, securityReads);
        Assert.AreEqual(2, connection.Requests.Count(request => request.Method == "account/securitySetup/read"));
    }

    [TestMethod]
    public async Task AccountSecuritySetupNoticeRefreshesWhenCurrentUserIdentityChanges()
    {
        int accountReads = 0;
        int noticeReads = 0;
        var connection = new RecordingConnection
        {
            AsyncHandler = (method, _, _) => method switch
            {
                "initialize" => Task.FromResult(JsonSerializer.SerializeToElement(new { userAgent = $"codex-cli/{SupportedCodexVersions.Minimum}" })),
                "account/read" => Task.FromResult(JsonSerializer.SerializeToElement(new
                {
                    account = new { type = "chatgpt", email = "owner@example.test", planType = "plus", workspaceName = "Workspace" },
                    userId = Interlocked.Increment(ref accountReads) == 1 ? "user-1" : "user-2",
                    workspaceRouting = new { chatgptAccountId = "account-1" },
                })),
                "account/securitySetup/read" => Task.FromResult(CreateNoticeResponse(
                    "account-1",
                    Interlocked.Increment(ref noticeReads) == 1 ? "user-1" : "user-2",
                    "https://security.example.test/setup")),
                _ => Task.FromResult(JsonSerializer.SerializeToElement(new { })),
            },
        };
        await using var service = CreateService();
        await service.InitializeAsync(connection, Options(), CancellationToken.None);
        Task<AccountStatus> firstNotice = WaitForAccountSecurityNoticeAsync(service, "user-1");

        AccountStatus first = await service.GetAccountStatusAsync(CancellationToken.None);
        AccountStatus publishedFirst = await firstNotice.WaitAsync(TimeSpan.FromSeconds(5));
        Task<AccountStatus> secondNotice = WaitForAccountSecurityNoticeAsync(service, "user-2");
        AccountStatus second = await service.GetAccountStatusAsync(CancellationToken.None);
        AccountStatus publishedSecond = await secondNotice.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.IsNull(first.SecuritySetupNotice);
        Assert.AreEqual("user-1", publishedFirst.SecuritySetupNotice?.UserId);
        Assert.IsNull(second.SecuritySetupNotice);
        Assert.AreEqual("user-2", publishedSecond.SecuritySetupNotice?.UserId);
        Assert.AreEqual(2, connection.Requests.Count(request => request.Method == "account/securitySetup/read"));
    }

    [TestMethod]
    public async Task AccountSecuritySetupNoticeRetriesWhenIdentityChangesAfterNullNotice()
    {
        int accountReads = 0;
        int noticeReads = 0;
        var connection = new RecordingConnection
        {
            Handler = (method, _) => method switch
            {
                "initialize" => JsonSerializer.SerializeToElement(new { userAgent = $"codex-cli/{SupportedCodexVersions.Minimum}" }),
                "account/read" => JsonSerializer.SerializeToElement(new
                {
                    account = new { type = "chatgpt", email = "owner@example.test", planType = "plus", workspaceName = "Workspace" },
                    userId = Interlocked.Increment(ref accountReads) == 1 ? "user-1" : "user-2",
                    workspaceRouting = new { chatgptAccountId = "account-1" },
                }),
                "account/securitySetup/read" => Interlocked.Increment(ref noticeReads) == 1
                    ? JsonSerializer.SerializeToElement(new { notice = (object?)null })
                    : CreateNoticeResponse("account-1", "user-2", "https://security.example.test/setup"),
                _ => JsonSerializer.SerializeToElement(new { }),
            },
        };
        await using var service = CreateService();
        await service.InitializeAsync(connection, Options(), CancellationToken.None);

        Task<AccountStatus> noticePublished = WaitForAccountSecurityNoticeAsync(service, "user-2");
        AccountStatus first = await service.GetAccountStatusAsync(CancellationToken.None);
        await WaitForSecuritySetupReadCompletionAsync(service, expectedAttempts: 1);
        AccountStatus second = await service.GetAccountStatusAsync(CancellationToken.None);
        AccountStatus published = await noticePublished.WaitAsync(TimeSpan.FromSeconds(5));
        await WaitForSecuritySetupReadCompletionAsync(service, expectedAttempts: 1);

        Assert.IsNull(first.SecuritySetupNotice);
        Assert.IsNull(second.SecuritySetupNotice);
        Assert.AreEqual("user-2", published.SecuritySetupNotice?.UserId);
        Assert.AreEqual(2, noticeReads);
    }

    [TestMethod]
    public async Task AccountNotificationInvalidatesPendingNoticeBeforeRoutingRefresh()
    {
        int accountReads = 0;
        int noticeReads = 0;
        int publishedNotices = 0;
        var firstNoticeStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstNoticeResponse = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        var refreshedNotice = new TaskCompletionSource<AccountStatus>(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<JsonElement> ReadNoticeAsync()
        {
            if (Interlocked.Increment(ref noticeReads) == 1)
            {
                firstNoticeStarted.TrySetResult();
                return firstNoticeResponse.Task;
            }

            return Task.FromResult(CreateNoticeResponse("account-1", "user-1", "https://security.example.test/setup"));
        }

        var connection = new RecordingConnection
        {
            AsyncHandler = (method, _, _) => method switch
            {
                "initialize" => Task.FromResult(JsonSerializer.SerializeToElement(new { userAgent = $"codex-cli/{SupportedCodexVersions.Tier171}" })),
                "account/read" => Task.FromResult(AccountRead(Interlocked.Increment(ref accountReads))),
                "account/securitySetup/read" => ReadNoticeAsync(),
                _ => Task.FromResult(JsonSerializer.SerializeToElement(new { })),
            },
        };
        await using var service = CreateService();
        await service.InitializeAsync(connection, Options(), CancellationToken.None);
        service.AccountStatusChanged += (value, _) =>
        {
            if (value.SecuritySetupNotice is not null)
            {
                Interlocked.Increment(ref publishedNotices);
                refreshedNotice.TrySetResult(value);
            }

            return Task.CompletedTask;
        };

        AccountStatus first = await service.GetAccountStatusAsync(CancellationToken.None);
        await firstNoticeStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await connection.EmitNotificationAsync("account/updated", new
        {
            reason = "workspaceRoutingReady",
            authMode = "chatgpt",
            planType = "plus",
        });
        AccountStatus notice = await refreshedNotice.Task.WaitAsync(TimeSpan.FromSeconds(5));

        firstNoticeResponse.SetResult(CreateNoticeResponse("account-1", "user-1", "https://security.example.test/stale"));
        await Task.Delay(50);

        Assert.IsNull(first.SecuritySetupNotice);
        Assert.AreEqual(2, accountReads);
        Assert.AreEqual(2, noticeReads);
        Assert.AreEqual("https://security.example.test/setup", notice.SecuritySetupNotice?.ActionUrl);
        Assert.AreEqual(1, publishedNotices, "The stale response from before account/updated must be discarded.");
    }
    [TestMethod]
    public async Task AccountSecuritySetupReadDoesNotBlockRefreshAndDiscardsStaleIdentityResponse()
    {
        int accountReads = 0;
        int noticeReads = 0;
        var firstNoticeStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstNoticeResponse = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        var staleNoticePublished = new TaskCompletionSource<AccountStatus>(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<JsonElement> ReadNoticeAsync()
        {
            if (Interlocked.Increment(ref noticeReads) == 1)
            {
                firstNoticeStarted.TrySetResult();
                return firstNoticeResponse.Task;
            }

            return Task.FromResult(CreateNoticeResponse("account-1", "user-2", "https://security.example.test/setup"));
        }

        var connection = new RecordingConnection
        {
            AsyncHandler = (method, _, _) => method switch
            {
                "initialize" => Task.FromResult(JsonSerializer.SerializeToElement(new { userAgent = $"codex-cli/{SupportedCodexVersions.Minimum}" })),
                "account/read" => Task.FromResult(JsonSerializer.SerializeToElement(new
                {
                    account = new { type = "chatgpt", email = "owner@example.test", planType = "plus", workspaceName = "Workspace" },
                    userId = Interlocked.Increment(ref accountReads) == 1 ? "user-1" : "user-2",
                    workspaceRouting = new { chatgptAccountId = "account-1" },
                })),
                "account/securitySetup/read" => ReadNoticeAsync(),
                _ => Task.FromResult(JsonSerializer.SerializeToElement(new { })),
            },
        };
        await using var service = CreateService();
        await service.InitializeAsync(connection, Options(), CancellationToken.None);
        service.AccountStatusChanged += (value, _) =>
        {
            if (string.Equals(value.SecuritySetupNotice?.UserId, "user-1", StringComparison.Ordinal))
            {
                staleNoticePublished.TrySetResult(value);
            }

            return Task.CompletedTask;
        };
        Task<AccountStatus> user2Notice = WaitForAccountSecurityNoticeAsync(service, "user-2");

        AccountStatus first = await service.GetAccountStatusAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2));
        await firstNoticeStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        AccountStatus second = await service.GetAccountStatusAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2));
        AccountStatus published = await user2Notice.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.AreEqual(2, connection.Requests.Count(request => request.Method == "account/read"));
        Assert.IsNull(first.SecuritySetupNotice);
        Assert.IsNull(second.SecuritySetupNotice);
        Assert.AreEqual("user-2", published.SecuritySetupNotice?.UserId);

        firstNoticeResponse.SetResult(CreateNoticeResponse("account-1", "user-1", "https://security.example.test/setup"));
        await Task.Delay(50);

        Assert.IsFalse(staleNoticePublished.Task.IsCompleted, "A notice for a retired user identity must be discarded.");
        Assert.AreEqual(2, connection.Requests.Count(request => request.Method == "account/securitySetup/read"));
    }

    [TestMethod]
    public async Task AccountSecuritySetupRemoteRejectionIsNotRetriedOnEveryStatusRead()
    {
        var securityReadStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var connection = new RecordingConnection
        {
            AsyncHandler = (method, _, _) => method switch
            {
                "initialize" => Task.FromResult(JsonSerializer.SerializeToElement(new { userAgent = $"codex-cli/{SupportedCodexVersions.Minimum}" })),
                "account/read" => Task.FromResult(AccountRead(1)),
                "account/securitySetup/read" => RejectSecuritySetupReadAsync(),
                _ => Task.FromResult(JsonSerializer.SerializeToElement(new { })),
            },
        };
        Task<JsonElement> RejectSecuritySetupReadAsync()
        {
            securityReadStarted.TrySetResult();
            return Task.FromException<JsonElement>(new JsonRpcRemoteException(-32601, "method not found"));
        }

        await using var service = CreateService();
        await service.InitializeAsync(connection, Options(), CancellationToken.None);

        _ = await service.GetAccountStatusAsync(CancellationToken.None);
        await securityReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        _ = await service.GetAccountStatusAsync(CancellationToken.None);

        Assert.AreEqual(1, connection.Requests.Count(request => request.Method == "account/securitySetup/read"));
    }

    [TestMethod]
    [DataRow("account-2", "user-1", "https://security.example.test/setup")]
    [DataRow("account-1", "user-2", "https://security.example.test/setup")]
    [DataRow("account-1", "user-1", "http://security.example.test/setup")]
    public async Task AccountSecuritySetupNoticeRejectsMismatchedIdentityAndInsecureAction(
        string noticeAccountId,
        string noticeUserId,
        string url)
    {
        var securityReadStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var noticePublished = new TaskCompletionSource<AccountStatus>(TaskCreationOptions.RunContinuationsAsynchronously);
        var connection = new RecordingConnection
        {
            AsyncHandler = (method, _, _) => method switch
            {
                "initialize" => Task.FromResult(JsonSerializer.SerializeToElement(new
                {
                    userAgent = $"codex-cli/{SupportedCodexVersions.Minimum}",
                })),
                "account/read" => Task.FromResult(AccountRead(1)),
                "account/securitySetup/read" => ReadSecuritySetupNoticeAsync(),
                _ => Task.FromResult(JsonSerializer.SerializeToElement(new { })),
            },
        };
        Task<JsonElement> ReadSecuritySetupNoticeAsync()
        {
            securityReadStarted.TrySetResult();
            return Task.FromResult(CreateNoticeResponse(noticeAccountId, noticeUserId, url));
        }

        await using var service = CreateService();
        await service.InitializeAsync(connection, Options(), CancellationToken.None);
        service.AccountStatusChanged += (value, _) =>
        {
            if (value.SecuritySetupNotice is not null)
            {
                noticePublished.TrySetResult(value);
            }

            return Task.CompletedTask;
        };
        AccountStatus status = await service.GetAccountStatusAsync(CancellationToken.None);
        await securityReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await WaitForSecuritySetupReadCompletionAsync(service, expectedAttempts: 1);

        Assert.AreEqual(AccountState.SignedIn, status.State);
        Assert.IsNull(status.SecuritySetupNotice);
        Assert.IsFalse(noticePublished.Task.IsCompleted, "Invalid identity or non-HTTPS notices must not be published.");
        Assert.AreEqual(1, connection.Requests.Count(request => request.Method == "account/securitySetup/read"));
    }

    [TestMethod]
    public async Task WorkspaceRoutingReadyRefreshesOnlyAfterReadingTheCurrentAccount()
    {
        string workspaceName = "Workspace before refresh";
        var connection = new RecordingConnection
        {
            AsyncHandler = (method, _, _) => method switch
            {
                "initialize" => Task.FromResult(JsonSerializer.SerializeToElement(new
                {
                    userAgent = $"codex-cli/{SupportedCodexVersions.Tier171}",
                })),
                "account/read" => Task.FromResult(JsonSerializer.SerializeToElement(new
                {
                    account = new { type = "chatgpt", email = "owner@example.test", planType = "plus", workspaceName },
                    userId = "user-1",
                    workspaceRouting = new { chatgptAccountId = "account-1" },
                })),
                "account/securitySetup/read" => ReadEmptySecuritySetupNoticeAsync(),
                _ => Task.FromResult(JsonSerializer.SerializeToElement(new { })),
            },
        };
        Task<JsonElement> ReadEmptySecuritySetupNoticeAsync()
            => Task.FromResult(JsonSerializer.SerializeToElement(new { notice = (object?)null }));
        await using var service = CreateService();
        await service.InitializeAsync(connection, Options(), CancellationToken.None);
        var refreshedStatus = new TaskCompletionSource<AccountStatus>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.AccountStatusChanged += (value, _) =>
        {
            if (string.Equals(value.WorkspaceName, "Workspace after routing", StringComparison.Ordinal))
            {
                refreshedStatus.TrySetResult(value);
            }

            return Task.CompletedTask;
        };
        _ = await service.GetAccountStatusAsync(CancellationToken.None);
        await WaitForSecuritySetupReadCompletionAsync(service, expectedAttempts: 1);

        workspaceName = "Workspace after routing";
        await connection.EmitNotificationAsync("account/updated", new
        {
            reason = "workspaceRoutingReady",
            authMode = "chatgpt",
            planType = "plus",
        });
        AccountStatus afterRouting = await refreshedStatus.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.AreEqual(2, connection.Requests.Count(request => request.Method == "account/read"));
        Assert.IsTrue(service.IsConnectionActive);
        Assert.AreEqual(AccountState.SignedIn, afterRouting.State);
        Assert.AreEqual("Workspace after routing", afterRouting.WorkspaceName);
        Assert.AreEqual(1, connection.Requests.Count(request => request.Method == "account/securitySetup/read"));
    }

    private static async Task WaitForSecuritySetupReadCompletionAsync(CodexSessionService service, int expectedAttempts)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        FieldInfo contextField = typeof(CodexSessionService).GetField("connectionContext", flags)
            ?? throw new AssertFailedException("The worker connection context field was not found.");
        object context = contextField.GetValue(service)
            ?? throw new AssertFailedException("The worker connection context was not initialized.");
        PropertyInfo inProgressProperty = context.GetType().GetProperty(
            "SecuritySetupReadInProgress",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            ?? throw new AssertFailedException("The account security read state was not found.");

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (GetSecuritySetupReadAttemptCount(context) < expectedAttempts)
        {
            await Task.Delay(10, timeout.Token);
        }

        Assert.AreEqual(expectedAttempts, GetSecuritySetupReadAttemptCount(context));
        while ((bool)(inProgressProperty.GetValue(context) ?? false))
        {
            await Task.Delay(10, timeout.Token);
        }

        Assert.AreEqual(expectedAttempts, GetSecuritySetupReadAttemptCount(context));
    }

    private static int GetSecuritySetupReadAttemptCount(CodexSessionService service)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        FieldInfo contextField = typeof(CodexSessionService).GetField("connectionContext", flags)
            ?? throw new AssertFailedException("The worker connection context field was not found.");
        object context = contextField.GetValue(service)
            ?? throw new AssertFailedException("The worker connection context was not initialized.");
        return GetSecuritySetupReadAttemptCount(context);
    }

    private static int GetSecuritySetupReadAttemptCount(object context)
    {
        PropertyInfo property = context.GetType().GetProperty(
            "SecuritySetupReadAttemptCount",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            ?? throw new AssertFailedException("The account security read attempt count was not found.");
        return (int)(property.GetValue(context) ?? 0);
    }
    private static JsonElement AccountRead(int readNumber)
        => JsonSerializer.SerializeToElement(new
        {
            account = new { type = "chatgpt", email = "owner@example.test", planType = "plus", workspaceName = $"Workspace {readNumber}" },
            userId = "user-1",
            workspaceRouting = new { chatgptAccountId = "account-1" },
        });

    private static JsonElement CreateNoticeResponse(string noticeAccountId, string noticeUserId, string url)
        => JsonSerializer.SerializeToElement(new
        {
            notice = new
            {
                accountId = noticeAccountId,
                userId = noticeUserId,
                title = "Secure this account",
                description = "Review your security settings.",
                action = new { label = "Review security", url },
            },
        });

    private static Task<AccountStatus> WaitForAccountSecurityNoticeAsync(CodexSessionService service, string expectedUserId)
    {
        var completion = new TaskCompletionSource<AccountStatus>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.AccountStatusChanged += (value, _) =>
        {
            if (string.Equals(value.SecuritySetupNotice?.UserId, expectedUserId, StringComparison.Ordinal))
            {
                completion.TrySetResult(value);
            }

            return Task.CompletedTask;
        };
        return completion.Task;
    }
}
