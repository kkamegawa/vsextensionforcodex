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
    public async Task RetiredSessionNeverPublishesQueuedStreamingDeltaIntoReplacementOwner()
    {
        var oldConnection = new RecordingConnection();
        var newConnection = new RecordingConnection();
        await using var service = CreateService();
        var publishedTexts = new System.Collections.Concurrent.ConcurrentQueue<string>();
        service.ConversationEventReceived += (value, _) =>
        {
            publishedTexts.Enqueue(value.Text ?? string.Empty);
            return Task.CompletedTask;
        };

        await service.InitializeAsync(oldConnection, Options(), CancellationToken.None);
        await oldConnection.EmitNotificationAsync(
            "item/agentMessage/delta",
            new { threadId = "old-thread", turnId = "old-turn", itemId = "old-item", delta = "retired-owner-secret" });
        await service.InitializeAsync(newConnection, Options(), CancellationToken.None);
        publishedTexts.Clear();
        await Task.Delay(TimeSpan.FromMilliseconds(180));

        Assert.IsFalse(publishedTexts.Any(text => text.Contains("retired-owner-secret", StringComparison.Ordinal)));
    }

    [TestMethod]
    public async Task OwnerWithoutAuthoritativeAccountIdNeverReadsOrWritesSharedSkillCache()
    {
        var store = new NullSkillCatalogStore();
        var connection = new RecordingConnection
        {
            Handler = (method, _) => method == "skills/list"
                ? JsonSerializer.SerializeToElement(new { data = Array.Empty<object>() })
                : JsonSerializer.SerializeToElement(new { }),
        };
        await using var service = CreateService(store);
        await service.InitializeAsync(connection, Options(), CancellationToken.None);

        ListSkillsResult first = await service.ListSkillsAsync(false, CancellationToken.None);
        ListSkillsResult cached = await service.ListSkillsAsync(false, CancellationToken.None);

        Assert.IsTrue(first.IsSupported);
        Assert.IsTrue(cached.IsSupported);
        Assert.AreEqual(0, store.ReadCount);
        Assert.AreEqual(0, store.WriteCount);
        Assert.AreEqual(0, store.DeleteCount);
    }

    [TestMethod]
    public async Task InitializeReadsVersionFromFirstUserAgentProduct()
    {
        var connection = new RecordingConnection
        {
            Handler = (method, _) => method == "initialize"
                ? JsonSerializer.SerializeToElement(new
                {
                    userAgent = "codex-cli/1.2.3-beta.4+build.5 helper/9.9.9",
                    serverInfo = new { version = "0.1.0" },
                })
                : JsonSerializer.SerializeToElement(new { }),
        };
        await using var service = CreateService();
        ConversationEvent? connected = null;
        service.ConversationEventReceived += (value, _) =>
        {
            connected = value;
            return Task.CompletedTask;
        };

        await service.InitializeAsync(connection, Options(), CancellationToken.None);

        Assert.AreEqual("1.2.3-beta.4+build.5", service.CodexVersion);
        Assert.AreEqual(
            "Connected to codex app-server v1.2.3-beta.4+build.5.",
            connected?.Text);
    }

    [TestMethod]
    public async Task InitializeFallsBackToValidatedLegacyServerVersion()
    {
        var connection = new RecordingConnection
        {
            Handler = (method, _) => method == "initialize"
                ? JsonSerializer.SerializeToElement(new
                {
                    userAgent = "malformed user agent",
                    serverInfo = new { version = "2.3.4-rc.1" },
                })
                : JsonSerializer.SerializeToElement(new { }),
        };
        await using var service = CreateService();

        await service.InitializeAsync(connection, Options(), CancellationToken.None);

        Assert.AreEqual("2.3.4-rc.1", service.CodexVersion);
    }

    [TestMethod]
    public async Task InitializeRejectsUnsafeVersionsAndClearsPreviousVersion()
    {
        string userAgent = "codex/1.2.3";
        var connection = new RecordingConnection
        {
            Handler = (method, _) => method == "initialize"
                ? JsonSerializer.SerializeToElement(new { userAgent, serverInfo = new { version = "invalid" } })
                : JsonSerializer.SerializeToElement(new { }),
        };
        await using var service = CreateService();
        await service.InitializeAsync(connection, Options(), CancellationToken.None);
        Assert.AreEqual("1.2.3", service.CodexVersion);

        string[] unsafeUserAgents =
        [
            "codex/1.2",
            "codex/01.2.3",
            "codex/1.2.3\u001b[31m",
            "codex/1.2.3-ベータ",
            $"codex/1.2.3+{new string('a', 59)}",
        ];
        foreach (string value in unsafeUserAgents)
        {
            userAgent = value;
            await service.InitializeAsync(connection, Options(), CancellationToken.None);
            Assert.IsNull(service.CodexVersion, value);
        }
    }

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
    public async Task ThreadListOmitsAnOversizedPreviewWithoutFailingThePage()
    {
        var connection = new RecordingConnection
        {
            Handler = (method, _) => method == "thread/list"
                ? JsonSerializer.SerializeToElement(new
                {
                    data = new[]
                    {
                        new { id = "large-preview", preview = new string('p', 8 * 1024 + 1) },
                        new { id = "normal-preview", preview = "available" },
                    },
                    nextCursor = (string?)null,
                })
                : JsonSerializer.SerializeToElement(new { }),
        };
        await using var service = CreateService();
        await service.InitializeAsync(connection, Options(), CancellationToken.None);

        ThreadPage page = await service.ListThreadsAsync(null, CancellationToken.None);

        Assert.AreEqual(2, page.Threads.Count);
        Assert.AreEqual("large-preview", page.Threads[0].Id);
        Assert.IsNull(page.Threads[0].Preview);
        Assert.AreEqual("available", page.Threads[1].Preview);
    }

    [TestMethod]
    public async Task HistoryReadsUseMetadataOnlyAndBoundedPagesWithoutReturningAttachmentPayloads()
    {
        string oversizedPayload = new('x', 65_537);
        var connection = new RecordingConnection
        {
            Handler = (method, _) => method switch
            {
                "thread/read" => JsonSerializer.SerializeToElement(new { thread = new { id = "thread-1" } }),
                "thread/turns/list" => JsonSerializer.SerializeToElement(new
                {
                    data = new[] { new { id = "turn-1", status = "completed", startedAt = 10L, completedAt = 20L } },
                    nextCursor = "turn-cursor",
                }),
                "thread/items/list" => JsonSerializer.SerializeToElement(new
                {
                    data = new[]
                    {
                        new
                        {
                            turnId = "turn-1",
                            startedAtMs = 11L,
                            item = new { id = "item-1", type = "agentMessage", text = "hello history" },
                        },
                    },
                    nextCursor = "item-cursor",
                }),
                "thread/attachment/list" => JsonSerializer.SerializeToElement(new
                {
                    data = new object[]
                    {
                        new { id = "attachment-1", attachmentType = "example", identityKey = "key-1", createdAt = 30L, payload = new { safe = true } },
                        new { id = "attachment-large", attachmentType = "example", identityKey = "key-large", createdAt = 31L, payload = oversizedPayload },
                    },
                    nextCursor = (string?)null,
                }),
                _ => JsonSerializer.SerializeToElement(new { }),
            },
        };
        await using var service = CreateService();
        await service.InitializeAsync(connection, Options(), CancellationToken.None);

        ThreadSummary thread = await service.ReadThreadAsync("thread-1", CancellationToken.None);
        ThreadTurnsPage turns = await service.ListThreadTurnsAsync("thread-1", null, 500, CancellationToken.None);
        ThreadItemsPage items = await service.ListThreadItemsAsync("thread-1", "turn-1", null, 500, CancellationToken.None);
        ThreadAttachmentsPage attachments = await service.ListThreadAttachmentsAsync("thread-1", null, 500, CancellationToken.None);

        Assert.AreEqual("thread-1", thread.Id);
        Assert.AreEqual("turn-1", turns.Turns.Single().Id);
        Assert.AreEqual("turn-cursor", turns.NextCursor);
        Assert.AreEqual("hello history", items.Items.Single().Text);
        Assert.AreEqual("item-cursor", items.NextCursor);
        Assert.AreEqual(2, attachments.Attachments.Count);
        Assert.IsNotNull(attachments.Attachments[0].UnavailableReason);
        Assert.IsNotNull(attachments.Attachments[1].UnavailableReason);
        Assert.IsFalse(JsonSerializer.Serialize(attachments).Contains("safe", StringComparison.Ordinal));
        Assert.IsFalse(JsonSerializer.Serialize(attachments).Contains(oversizedPayload, StringComparison.Ordinal));

        JsonElement readParameters = JsonSerializer.SerializeToElement(
            connection.Requests.Single(item => item.Method == "thread/read").Parameters);
        Assert.IsFalse(readParameters.GetProperty("includeTurns").GetBoolean());
        JsonElement turnParameters = JsonSerializer.SerializeToElement(
            connection.Requests.Single(item => item.Method == "thread/turns/list").Parameters);
        Assert.AreEqual(50, turnParameters.GetProperty("limit").GetInt32());
        Assert.AreEqual("summary", turnParameters.GetProperty("itemsView").GetString());
        Assert.AreEqual("desc", turnParameters.GetProperty("sortDirection").GetString());
        JsonElement itemParameters = JsonSerializer.SerializeToElement(
            connection.Requests.Single(item => item.Method == "thread/items/list").Parameters);
        Assert.AreEqual(100, itemParameters.GetProperty("limit").GetInt32());
        Assert.AreEqual("desc", itemParameters.GetProperty("sortDirection").GetString());
    }

    [TestMethod]
    public async Task ListThreadItemsSkipsEntriesFromAnotherTurn()
    {
        var connection = new RecordingConnection
        {
            Handler = (method, _) => method == "thread/items/list"
                ? JsonSerializer.SerializeToElement(new
                {
                    data = new[]
                    {
                        new { turnId = "turn-1", item = new { id = "item-1", type = "agentMessage", text = "requested turn" } },
                        new { turnId = "turn-2", item = new { id = "item-2", type = "agentMessage", text = "other turn" } },
                    },
                    nextCursor = (string?)null,
                })
                : JsonSerializer.SerializeToElement(new { }),
        };
        await using var service = CreateService();
        await service.InitializeAsync(connection, Options(), CancellationToken.None);

        ThreadItemsPage items = await service.ListThreadItemsAsync("thread-1", "turn-1", null, 50, CancellationToken.None);

        Assert.AreEqual("item-1", items.Items.Single().Id);
        Assert.AreEqual("turn-1", items.Items.Single().TurnId);
    }

    [TestMethod]
    public async Task ReadThreadRejectsMismatchedResponseId()
    {
        var connection = new RecordingConnection
        {
            Handler = (method, _) => method == "thread/read"
                ? JsonSerializer.SerializeToElement(new { thread = new { id = "different-thread" } })
                : JsonSerializer.SerializeToElement(new { }),
        };
        await using var service = CreateService();
        await service.InitializeAsync(connection, Options(), CancellationToken.None);

        await Assert.ThrowsExactlyAsync<InvalidDataException>(
            () => service.ReadThreadAsync("requested-thread", CancellationToken.None));
    }

    [TestMethod]
    public async Task ReadThreadOmitsOverlongPreviewAndRejectsOverlongResponseId()
    {
        JsonElement response = JsonSerializer.SerializeToElement(new
        {
            thread = new { id = "thread-1", preview = new string('p', 8 * 1024 + 1) },
        });
        var connection = new RecordingConnection
        {
            Handler = (method, _) => method == "thread/read" ? response : JsonSerializer.SerializeToElement(new { }),
        };
        await using var service = CreateService();
        await service.InitializeAsync(connection, Options(), CancellationToken.None);

        ThreadSummary summary = await service.ReadThreadAsync("thread-1", CancellationToken.None);
        Assert.AreEqual("thread-1", summary.Id);
        Assert.IsNull(summary.Preview);

        response = JsonSerializer.SerializeToElement(new
        {
            thread = new { id = new string('i', 257), preview = "bounded preview" },
        });
        await Assert.ThrowsExactlyAsync<InvalidDataException>(
            () => service.ReadThreadAsync("thread-1", CancellationToken.None));
    }

    [TestMethod]
    public async Task ResumeThreadAlwaysExcludesTurns()
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

        JsonElement parameters = JsonSerializer.SerializeToElement(
            connection.Requests.Single(item => item.Method == "thread/resume").Parameters);
        Assert.IsTrue(parameters.GetProperty("excludeTurns").GetBoolean());
    }

    [TestMethod]
    public async Task ListModelsReturnsModelsAndDefault()
    {
        var connection = new RecordingConnection
        {
            Handler = (method, _) => method == "model/list"
                ? JsonSerializer.SerializeToElement(new
                {
                    data = new object[]
                    {
                        new
                        {
                            model = "gpt-5-codex",
                            displayName = "GPT-5 Codex",
                            isDefault = false,
                            defaultReasoningEffort = "high",
                            supportedReasoningEfforts = new[]
                            {
                                new { reasoningEffort = "medium", description = "Balanced" },
                                new { reasoningEffort = "high", description = "Deep" },
                            },
                            supportsPersonality = true,
                            defaultServiceTier = "priority",
                            serviceTiers = new[]
                            {
                                new { id = "priority", name = "Priority", description = "Fast queue" },
                            },
                        },
                        new
                        {
                            model = "gpt-5",
                            displayName = "GPT-5",
                            isDefault = true,
                            defaultReasoningEffort = "medium",
                            supportedReasoningEfforts = Array.Empty<object>(),
                            supportsPersonality = false,
                            defaultServiceTier = (string?)null,
                            serviceTiers = Array.Empty<object>(),
                        },
                    },
                    nextCursor = (string?)null,
                })
                : JsonSerializer.SerializeToElement(new { }),
        };
        await using var service = CreateService();
        await service.InitializeAsync(connection, Options(), CancellationToken.None);

        ListModelsResult result = await service.ListModelsAsync(CancellationToken.None);

        CollectionAssert.AreEqual(ExpectedModels, result.Models.Select(model => model.Id).ToArray());
        Assert.AreEqual("GPT-5 Codex", result.Models[0].DisplayName);
        Assert.AreEqual("gpt-5", result.DefaultModel);
        Assert.AreEqual("high", result.Models[0].DefaultReasoningEffort);
        Assert.AreEqual(2, result.Models[0].SupportedReasoningEfforts.Count);
        Assert.AreEqual("medium", result.Models[0].SupportedReasoningEfforts[0].Id);
        Assert.IsTrue(result.Models[0].SupportsPersonality);
        Assert.AreEqual("priority", result.Models[0].DefaultServiceTier);
        Assert.AreEqual("priority", result.Models[0].ServiceTiers[0].Id);
    }

    [TestMethod]
    public async Task ListModelsDropsMalformedHiddenAndDuplicateModels()
    {
        var connection = new RecordingConnection
        {
            Handler = (method, _) => method == "model/list"
                ? JsonSerializer.SerializeToElement(new
                {
                    data = new object[]
                    {
                        new { model = "gpt-5-codex" },
                        new { model = "gpt-5-codex" },
                        new { model = "" },
                        new { model = "bad\rmodel" },
                        new { model = "hidden-model", hidden = true },
                        new { displayName = "No model id" },
                        new { model = "gpt-5" },
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
    public async Task ListModelsCapturesHiddenDefaultButExcludesItFromVisibleList()
    {
        var connection = new RecordingConnection
        {
            Handler = (method, _) => method == "model/list"
                ? JsonSerializer.SerializeToElement(new
                {
                    data = new object[]
                    {
                        new { model = "gpt-5-codex", displayName = "GPT-5 Codex", isDefault = false, hidden = false },
                        new { model = "gpt-5", displayName = "GPT-5", isDefault = false, hidden = false },
                        new
                        {
                            model = "gpt-5.1-codex-max",
                            displayName = "GPT-5.1 Codex Max",
                            isDefault = true,
                            hidden = true,
                            defaultReasoningEffort = "high",
                            supportedReasoningEfforts = new[]
                            {
                                new { reasoningEffort = "high", description = "Deep" },
                            },
                            defaultServiceTier = "standard",
                            serviceTiers = new[] { new { id = "fast", name = "Fast", description = "Lower latency" } },
                        },
                    },
                    nextCursor = (string?)null,
                })
                : JsonSerializer.SerializeToElement(new { }),
        };
        await using var service = CreateService();
        await service.InitializeAsync(connection, Options(), CancellationToken.None);

        ListModelsResult result = await service.ListModelsAsync(CancellationToken.None);

        CollectionAssert.AreEqual(ExpectedModels, result.Models.Select(model => model.Id).ToArray());
        Assert.AreEqual("gpt-5.1-codex-max", result.DefaultModel);
        Assert.AreEqual("gpt-5.1-codex-max", result.DefaultModelInfo!.Id);
        Assert.AreEqual("high", result.DefaultModelInfo.DefaultReasoningEffort);
        Assert.AreEqual("high", result.DefaultModelInfo.SupportedReasoningEfforts.Single().Id);
        Assert.AreEqual("standard", result.DefaultModelInfo.DefaultServiceTier);
        Assert.AreEqual("fast", result.DefaultModelInfo.ServiceTiers[0].Id);
    }

    [TestMethod]
    public async Task ListModelsCapturesTopLevelHiddenDefaultMetadata()
    {
        var connection = new RecordingConnection
        {
            Handler = (method, _) => method == "model/list"
                ? JsonSerializer.SerializeToElement(new
                {
                    data = new object[]
                    {
                        new { model = "gpt-5-codex" },
                        new
                        {
                            model = "hidden-default",
                            hidden = true,
                            defaultReasoningEffort = "high",
                            supportedReasoningEfforts = new[]
                            {
                                new { reasoningEffort = "high", description = "Deep" },
                            },
                        },
                    },
                    defaultModel = "hidden-default",
                })
                : JsonSerializer.SerializeToElement(new { }),
        };
        await using var service = CreateService();
        await service.InitializeAsync(connection, Options(), CancellationToken.None);

        ListModelsResult result = await service.ListModelsAsync(CancellationToken.None);

        CollectionAssert.AreEqual(new[] { "gpt-5-codex" }, result.Models.Select(model => model.Id).ToArray());
        Assert.AreEqual("hidden-default", result.DefaultModel);
        Assert.IsNotNull(result.DefaultModelInfo);
        Assert.AreEqual("hidden-default", result.DefaultModelInfo.Id);
        Assert.AreEqual("high", result.DefaultModelInfo.DefaultReasoningEffort);
        Assert.AreEqual("high", result.DefaultModelInfo.SupportedReasoningEfforts.Single().Id);
    }

    [TestMethod]
    public async Task ListModelsRequestsHiddenModels()
    {
        var connection = new RecordingConnection
        {
            Handler = (method, _) => method == "model/list"
                ? JsonSerializer.SerializeToElement(new { data = Array.Empty<object>(), nextCursor = (string?)null })
                : JsonSerializer.SerializeToElement(new { }),
        };
        await using var service = CreateService();
        await service.InitializeAsync(connection, Options(), CancellationToken.None);

        await service.ListModelsAsync(CancellationToken.None);

        RecordedRequest request = connection.Requests.Single(item => item.Method == "model/list");
        JsonElement parameters = JsonSerializer.SerializeToElement(request.Parameters);
        Assert.IsTrue(parameters.GetProperty("includeHidden").GetBoolean());
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
    public async Task StartTurnForwardsModelAndModeOverridesWhenSet()
    {
        string activeDocument = Path.GetTempFileName();
        string referencedDocument = Path.GetTempFileName();
        var connection = new RecordingConnection
        {
            Handler = (method, _) => method == "turn/start"
                ? JsonSerializer.SerializeToElement(new { turn = new { id = "turn-1" } })
                : JsonSerializer.SerializeToElement(new { }),
        };
        await using var service = CreateService();
        await service.InitializeAsync(connection, Options(), CancellationToken.None);

        await service.StartTurnAsync(
            new StartTurnRequest
            {
                ThreadId = "thread-1",
                Text = "hello",
                Model = "gpt-5",
                ApprovalPolicy = "never",
                ApprovalsReviewer = "user",
                SandboxMode = "readOnly",
                HasEffort = true,
                Effort = "high",
                Personality = "friendly",
                HasServiceTier = true,
                ServiceTier = "priority",
                CollaborationMode = new CollaborationModeInfo
                {
                    Mode = "plan",
                    Model = "gpt-5",
                    ReasoningEffort = "high",
                    DeveloperInstructions = "Return a plan.",
                },
                IdeContext = new IdeContextInfo
                {
                    ActiveDocumentPath = activeDocument,
                    ReferencedFilePaths = [referencedDocument],
                    SelectionFilePath = activeDocument,
                    SelectionText = "selected text",
                },
            },
            CancellationToken.None);

        RecordedRequest request = connection.Requests.Single(item => item.Method == "turn/start");
        JsonElement parameters = JsonSerializer.SerializeToElement(request.Parameters, WireJsonOptions);
        Assert.AreEqual("gpt-5", parameters.GetProperty("model").GetString());
        Assert.AreEqual("never", parameters.GetProperty("approvalPolicy").GetString());
        Assert.AreEqual("user", parameters.GetProperty("approvalsReviewer").GetString());
        Assert.AreEqual("readOnly", parameters.GetProperty("sandboxPolicy").GetProperty("type").GetString());
        Assert.AreEqual("high", parameters.GetProperty("effort").GetString());
        Assert.AreEqual("friendly", parameters.GetProperty("personality").GetString());
        Assert.AreEqual("priority", parameters.GetProperty("serviceTier").GetString());
        JsonElement collaborationMode = parameters.GetProperty("collaborationMode");
        Assert.AreEqual("plan", collaborationMode.GetProperty("mode").GetString());
        Assert.AreEqual("gpt-5", collaborationMode.GetProperty("settings").GetProperty("model").GetString());
        Assert.AreEqual(
            "high",
            collaborationMode.GetProperty("settings").GetProperty("reasoning_effort").GetString());
        JsonElement[] input = parameters.GetProperty("input").EnumerateArray().ToArray();
        Assert.AreEqual("text", input[0].GetProperty("type").GetString());
        Assert.AreEqual(4, input.Length);
        Assert.AreEqual("mention", input[1].GetProperty("type").GetString());
        Assert.AreEqual(activeDocument, input[1].GetProperty("path").GetString());
        Assert.AreEqual(referencedDocument, input[2].GetProperty("path").GetString());
        StringAssert.Contains(input[3].GetProperty("text").GetString(), "selected text");

        File.Delete(activeDocument);
        File.Delete(referencedDocument);
    }

    [TestMethod]
    public async Task StartTurnValidatesSkillIdentityAndSendsOnlyStructuredSkillFields()
    {
        var connection = new RecordingConnection
        {
            Handler = (method, _) => method switch
            {
                "skills/list" => JsonSerializer.SerializeToElement(new
                {
                    data = new object[]
                    {
                        new
                        {
                            cwd = "/repo",
                            errors = Array.Empty<object>(),
                            skills = new object[]
                            {
                                new
                                {
                                    name = "space.dot",
                                    description = "Run the skill.",
                                    enabled = true,
                                    path = "/repo/.codex/skills/space.dot/SKILL.md",
                                    scope = "repo",
                                },
                            },
                        },
                    },
                }),
                "turn/start" => JsonSerializer.SerializeToElement(new { turn = new { id = "turn-1" } }),
                _ => JsonSerializer.SerializeToElement(new { }),
            },
        };
        await using var service = CreateService();
        await service.InitializeAsync(connection, Options(), CancellationToken.None);

        await service.StartTurnAsync(
            new StartTurnRequest
            {
                ThreadId = "thread-1",
                Text = string.Empty,
                Skill = new SkillInvocationInfo
                {
                    Name = "space.dot",
                    Scope = "repo",
                    Path = "/repo/.codex/skills/space.dot/SKILL.md",
                },
            },
            CancellationToken.None);

        JsonElement parameters = ParametersFor(connection, "turn/start");
        JsonElement[] input = parameters.GetProperty("input").EnumerateArray().ToArray();
        Assert.AreEqual(2, input.Length);
        Assert.AreEqual("skill", input[1].GetProperty("type").GetString());
        Assert.AreEqual("space.dot", input[1].GetProperty("name").GetString());
        Assert.AreEqual("/repo/.codex/skills/space.dot/SKILL.md", input[1].GetProperty("path").GetString());
        Assert.IsFalse(input[1].TryGetProperty("scope", out _));
    }

    [TestMethod]
    public async Task StartTurnDistinguishesOmittedExplicitNullAndValueSettings()
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
            new StartTurnRequest { ThreadId = "thread-1", Text = "inherit" },
            CancellationToken.None);
        JsonElement omitted = ParametersFor(connection, "turn/start");
        Assert.IsFalse(omitted.TryGetProperty("effort", out _));
        Assert.IsFalse(omitted.TryGetProperty("serviceTier", out _));

        connection.Requests.Clear();
        await service.StartTurnAsync(
            new StartTurnRequest
            {
                ThreadId = "thread-1",
                Text = "clear",
                HasEffort = true,
                HasServiceTier = true,
            },
            CancellationToken.None);
        JsonElement cleared = ParametersFor(connection, "turn/start");
        Assert.AreEqual(JsonValueKind.Null, cleared.GetProperty("effort").ValueKind);
        Assert.AreEqual(JsonValueKind.Null, cleared.GetProperty("serviceTier").ValueKind);

        connection.Requests.Clear();
        await service.StartTurnAsync(
            new StartTurnRequest
            {
                ThreadId = "thread-1",
                Text = "override",
                HasEffort = true,
                Effort = "high",
                HasServiceTier = true,
                ServiceTier = "priority",
            },
            CancellationToken.None);
        JsonElement explicitValues = ParametersFor(connection, "turn/start");
        Assert.AreEqual("high", explicitValues.GetProperty("effort").GetString());
        Assert.AreEqual("priority", explicitValues.GetProperty("serviceTier").GetString());
    }

    [TestMethod]
    public async Task StartTurnBuildsValidatedAttachmentInputsAndDeduplicatesIdeContext()
    {
        string workspace = Path.Combine(Path.GetTempPath(), $"codex-attachments-{Guid.NewGuid():N}");
        Directory.CreateDirectory(workspace);
        string document = Path.Combine(workspace, "notes.md");
        string image = Path.Combine(workspace, "diagram.png");
        await File.WriteAllTextAsync(document, "notes");
        await File.WriteAllBytesAsync(image, [0x89, 0x50, 0x4e, 0x47]);
        var connection = new RecordingConnection
        {
            Handler = (method, _) => method == "turn/start"
                ? JsonSerializer.SerializeToElement(new { turn = new { id = "turn-1" } })
                : JsonSerializer.SerializeToElement(new { }),
        };
        await using var service = CreateService();
        await service.InitializeAsync(connection, Options(workspace), CancellationToken.None);

        await service.StartTurnAsync(
            new StartTurnRequest
            {
                ThreadId = "thread-1",
                Text = "inspect",
                Attachments =
                [
                    new AttachmentInfo(image, "image"),
                    new AttachmentInfo(document, "file"),
                    new AttachmentInfo(document, "mention"),
                    new AttachmentInfo(Path.Combine(workspace, ".", "notes.md"), "mention"),
                ],
                IdeContext = new IdeContextInfo
                {
                    ActiveDocumentPath = document,
                    ReferencedFilePaths = [image],
                },
            },
            CancellationToken.None);

        JsonElement parameters = ParametersFor(connection, "turn/start");
        JsonElement[] input = parameters.GetProperty("input").EnumerateArray().ToArray();
        Assert.AreEqual(3, input.Length);
        Assert.AreEqual("localImage", input[1].GetProperty("type").GetString());
        Assert.AreEqual(Path.GetFullPath(image), input[1].GetProperty("path").GetString());
        Assert.AreEqual("mention", input[2].GetProperty("type").GetString());
        Assert.AreEqual(Path.GetFullPath(document), input[2].GetProperty("path").GetString());

        Directory.Delete(workspace, recursive: true);
    }

    [TestMethod]
    public async Task StartTurnAllowsOutsideWorkspaceAttachmentsAndRejectsMoreThanTen()
    {
        string root = Path.Combine(Path.GetTempPath(), $"codex-attachment-policy-{Guid.NewGuid():N}");
        string workspace = Path.Combine(root, "workspace");
        string external = Path.Combine(root, "external");
        string protectedRoot = Path.Combine(root, "protected");
        Directory.CreateDirectory(workspace);
        Directory.CreateDirectory(external);
        Directory.CreateDirectory(protectedRoot);
        string protectedFile = Path.Combine(protectedRoot, "blocked.txt");
        await File.WriteAllTextAsync(protectedFile, "blocked");
        var attachments = new List<AttachmentInfo>();
        for (int index = 0; index < 11; index++)
        {
            string path = Path.Combine(external, $"file-{index}.txt");
            await File.WriteAllTextAsync(path, index.ToString(System.Globalization.CultureInfo.InvariantCulture));
            attachments.Add(new AttachmentInfo(path, "mention"));
        }

        var protectedPolicy = new ProtectedDirectoryPolicy([protectedRoot]);
        var pathPolicy = new PathAccessPolicy();
        await using var service = new CodexSessionService(
            new ApprovalPolicyEngine(pathPolicy, protectedPolicy),
            new SecretRedactor(),
            pathPolicy,
            protectedPolicy);
        var connection = new RecordingConnection
        {
            Handler = (method, _) => method == "turn/start"
                ? JsonSerializer.SerializeToElement(new { turn = new { id = "turn-1" } })
                : JsonSerializer.SerializeToElement(new { }),
        };
        await service.InitializeAsync(connection, Options(workspace), CancellationToken.None);

        await service.StartTurnAsync(
            new StartTurnRequest { ThreadId = "thread-1", Text = "inspect", Attachments = attachments.Take(10).ToList() },
            CancellationToken.None);
        await Assert.ThrowsExactlyAsync<AttachmentRejectedException>(() => service.StartTurnAsync(
            new StartTurnRequest { ThreadId = "thread-1", Text = "inspect", Attachments = attachments },
            CancellationToken.None));

        JsonElement[] input = ParametersFor(connection, "turn/start").GetProperty("input").EnumerateArray().ToArray();
        Assert.AreEqual(10, input.Count(item => item.GetProperty("type").GetString() == "mention"));

        Directory.Delete(root, recursive: true);
    }

    [TestMethod]
    public async Task ProtectedOrMissingAttachmentRejectsTheWholeTurn()
    {
        string root = Path.Combine(Path.GetTempPath(), $"codex-attachment-reject-{Guid.NewGuid():N}");
        string workspace = Path.Combine(root, "workspace");
        string protectedRoot = Path.Combine(root, "protected");
        Directory.CreateDirectory(workspace);
        Directory.CreateDirectory(protectedRoot);
        string readable = Path.Combine(workspace, "readable.txt");
        string protectedFile = Path.Combine(protectedRoot, "blocked.txt");
        string missing = Path.Combine(workspace, "missing.txt");
        await File.WriteAllTextAsync(readable, "ok");
        await File.WriteAllTextAsync(protectedFile, "blocked");
        var protectedPolicy = new ProtectedDirectoryPolicy([protectedRoot]);
        var pathPolicy = new PathAccessPolicy();
        await using var service = new CodexSessionService(
            new ApprovalPolicyEngine(pathPolicy, protectedPolicy),
            new SecretRedactor(),
            pathPolicy,
            protectedPolicy);
        var connection = new RecordingConnection
        {
            Handler = (method, _) => method == "turn/start"
                ? JsonSerializer.SerializeToElement(new { turn = new { id = "turn-1" } })
                : JsonSerializer.SerializeToElement(new { }),
        };
        await service.InitializeAsync(connection, Options(workspace), CancellationToken.None);

        try
        {
            foreach ((string path, string name) in new[] { (protectedFile, "blocked.txt"), (missing, "missing.txt") })
            {
                AttachmentRejectedException rejected = await Assert.ThrowsExactlyAsync<AttachmentRejectedException>(() =>
                    service.StartTurnAsync(
                        new StartTurnRequest
                        {
                            ThreadId = "thread-1",
                            Text = "inspect",
                            Attachments = [new AttachmentInfo(readable, "mention"), new AttachmentInfo(path, "mention")],
                        },
                        CancellationToken.None));
                StringAssert.Contains(rejected.Message, name);
                Assert.IsFalse(rejected.Message.Contains(root, StringComparison.OrdinalIgnoreCase));
            }

            Assert.IsFalse(connection.Requests.Any(item => item.Method == "turn/start"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task StartTurnOmitsModelAndModeOverridesWhenUnset()
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
        Assert.IsFalse(parameters.TryGetProperty("approvalPolicy", out _));
        Assert.IsFalse(parameters.TryGetProperty("approvalsReviewer", out _));
        Assert.IsFalse(parameters.TryGetProperty("sandboxPolicy", out _));
        Assert.IsFalse(parameters.TryGetProperty("permissions", out _));
        Assert.IsFalse(parameters.TryGetProperty("effort", out _));
        Assert.IsFalse(parameters.TryGetProperty("personality", out _));
        Assert.IsFalse(parameters.TryGetProperty("serviceTier", out _));
        Assert.IsFalse(parameters.TryGetProperty("collaborationMode", out _));
    }

    [TestMethod]
    public async Task StartTurnForwardsPermissionProfileWithoutLowLevelOverrides()
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
            new StartTurnRequest { ThreadId = "thread-1", Text = "hello", Permissions = "team-review" },
            CancellationToken.None);

        JsonElement parameters = ParametersFor(connection, "turn/start");
        Assert.AreEqual("team-review", parameters.GetProperty("permissions").GetString());
        Assert.IsFalse(parameters.TryGetProperty("approvalPolicy", out _));
        Assert.IsFalse(parameters.TryGetProperty("approvalsReviewer", out _));
        Assert.IsFalse(parameters.TryGetProperty("sandboxPolicy", out _));
    }

    [TestMethod]
    [DataRow("on-request", "user", "workspaceWrite")]
    [DataRow("on-request", "auto_review", "workspaceWrite")]
    [DataRow("never", "user", "dangerFullAccess")]
    public async Task StartTurnForwardsExactBuiltInApprovalTuple(
        string approvalPolicy,
        string approvalsReviewer,
        string sandboxMode)
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
            new StartTurnRequest
            {
                ThreadId = "thread-1",
                Text = "hello",
                ApprovalPolicy = approvalPolicy,
                ApprovalsReviewer = approvalsReviewer,
                SandboxMode = sandboxMode,
            },
            CancellationToken.None);

        JsonElement parameters = ParametersFor(connection, "turn/start");
        Assert.AreEqual(approvalPolicy, parameters.GetProperty("approvalPolicy").GetString());
        Assert.AreEqual(approvalsReviewer, parameters.GetProperty("approvalsReviewer").GetString());
        Assert.AreEqual(sandboxMode, parameters.GetProperty("sandboxPolicy").GetProperty("type").GetString());
        Assert.IsFalse(parameters.TryGetProperty("permissions", out _));
    }

    [TestMethod]
    public async Task StartTurnRejectsPermissionProfileCombinedWithLowLevelOverrides()
    {
        var connection = new RecordingConnection();
        await using var service = CreateService();
        await service.InitializeAsync(connection, Options(), CancellationToken.None);
        var request = new StartTurnRequest
        {
            ThreadId = "thread-1",
            Text = "hello",
            Permissions = "team-review",
            ApprovalsReviewer = "user",
        };

        await Assert.ThrowsExactlyAsync<ArgumentException>(
            () => service.StartTurnAsync(request, CancellationToken.None));

        Assert.IsFalse(connection.Requests.Any(item => item.Method == "turn/start"));
    }

    [TestMethod]
    public async Task ListPermissionProfilesUsesCwdAndPaginationAndBoundsUntrustedFields()
    {
        const string longDescription = "This description is intentionally longer than the display boundary. ";
        var connection = new RecordingConnection
        {
            Handler = (method, parameters) => method == "permissionProfile/list"
                ? PermissionProfilePage(parameters, longDescription)
                : JsonSerializer.SerializeToElement(new { }),
        };
        await using var service = CreateService();
        string cwd = Path.GetTempPath();
        await service.InitializeAsync(connection, Options(cwd, experimentalApi: true), CancellationToken.None);

        ListPermissionProfilesResult result = await service.ListPermissionProfilesAsync(CancellationToken.None);

        Assert.IsTrue(result.IsSupported);
        Assert.IsFalse(result.IsTruncated);
        Assert.AreEqual(3, result.Profiles.Count);
        CollectionAssert.AreEqual(
            new[] { "review", "custom", ":workspace" },
            result.Profiles.Select(profile => profile.Id).ToArray());
        Assert.IsTrue(result.Profiles[0].Allowed);
        Assert.IsFalse(result.Profiles[1].Allowed);
        Assert.IsTrue(result.Profiles[0].Description!.Length <= 512);
        Assert.IsFalse(result.Profiles[0].Description!.Any(char.IsControl));

        RecordedRequest[] requests = connection.Requests
            .Where(item => item.Method == "permissionProfile/list")
            .ToArray();
        Assert.AreEqual(2, requests.Length);
        JsonElement first = JsonSerializer.SerializeToElement(requests[0].Parameters, WireJsonOptions);
        JsonElement second = JsonSerializer.SerializeToElement(requests[1].Parameters, WireJsonOptions);
        Assert.AreEqual(cwd, first.GetProperty("cwd").GetString());
        Assert.AreEqual(100, first.GetProperty("limit").GetInt32());
        // permissionProfile/list is an allowlisted read: its 15-second budget is one deadline shared
        // by every overload retry, so each send receives only the remaining time.
        Assert.IsTrue(requests[0].Timeout <= TimeSpan.FromSeconds(15));
        Assert.IsTrue(requests[0].Timeout > TimeSpan.FromSeconds(14));
        Assert.IsFalse(first.TryGetProperty("cursor", out _));
        Assert.AreEqual("page-2", second.GetProperty("cursor").GetString());
    }

    [TestMethod]
    public async Task ListPermissionProfilesStopsAtPageLimitAndHonorsCancellation()
    {
        int page = 0;
        var connection = new RecordingConnection
        {
            Handler = (method, _) => method == "permissionProfile/list"
                ? JsonSerializer.SerializeToElement(new
                {
                    data = new[] { new { id = $"profile-{page}", allowed = true } },
                    nextCursor = $"page-{++page}",
                })
                : JsonSerializer.SerializeToElement(new { }),
        };
        await using var service = CreateService();
        await service.InitializeAsync(connection, Options(experimentalApi: true), CancellationToken.None);

        ListPermissionProfilesResult result = await service.ListPermissionProfilesAsync(CancellationToken.None);

        Assert.IsTrue(result.IsTruncated);
        Assert.AreEqual(10, connection.Requests.Count(item => item.Method == "permissionProfile/list"));
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        await Assert.ThrowsExactlyAsync<OperationCanceledException>(
            () => service.ListPermissionProfilesAsync(canceled.Token));
    }

    [TestMethod]
    public async Task ListPermissionProfilesDegradesAndCachesMethodNotFound()
    {
        var connection = new RecordingConnection
        {
            Handler = (method, _) => method == "permissionProfile/list"
                ? throw new JsonRpcRemoteException(-32601, "Method not found")
                : JsonSerializer.SerializeToElement(new { }),
        };
        await using var service = CreateService();
        await service.InitializeAsync(connection, Options(experimentalApi: true), CancellationToken.None);

        ListPermissionProfilesResult first = await service.ListPermissionProfilesAsync(CancellationToken.None);
        ListPermissionProfilesResult second = await service.ListPermissionProfilesAsync(CancellationToken.None);

        Assert.IsFalse(first.IsSupported);
        Assert.IsFalse(second.IsSupported);
        Assert.AreEqual(1, connection.Requests.Count(item => item.Method == "permissionProfile/list"));
    }

    [TestMethod]
    public async Task ListPermissionProfilesDoesNotProbeWhenExperimentalApiIsDisabled()
    {
        var connection = new RecordingConnection();
        await using var service = CreateService();
        await service.InitializeAsync(connection, Options(experimentalApi: false), CancellationToken.None);

        ListPermissionProfilesResult result = await service.ListPermissionProfilesAsync(CancellationToken.None);

        Assert.IsFalse(result.IsSupported);
        Assert.IsFalse(connection.Requests.Any(item => item.Method == "permissionProfile/list"));
    }

    [TestMethod]
    public async Task ListSkills_ReturnsSkillsAndErrorsFromEveryEntry()
    {
        var connection = new RecordingConnection
        {
            Handler = (method, _) => method == "skills/list"
                ? JsonSerializer.SerializeToElement(new
                {
                    data = new object[]
                    {
                        new
                        {
                            cwd = "/repo",
                            errors = new object[]
                            {
                                new { message = "SKILL.md front matter is not valid YAML.", path = "/repo/.codex/skills/broken/SKILL.md" },
                            },
                            skills = new object[]
                            {
                                new
                                {
                                    name = "review-diff",
                                    description = "Review the current diff.",
                                    enabled = true,
                                    path = "/repo/.codex/skills/review-diff/SKILL.md",
                                    scope = "repo",
                                },
                            },
                        },
                    },
                })
                : JsonSerializer.SerializeToElement(new { }),
        };
        await using var service = CreateService();
        await service.InitializeAsync(connection, Options(), CancellationToken.None);

        ListSkillsResult result = await service.ListSkillsAsync(forceReload: false, CancellationToken.None);

        Assert.IsTrue(result.IsSupported);
        Assert.IsFalse(result.IsTruncated);
        Assert.AreEqual(1, result.Skills.Count);
        Assert.AreEqual("review-diff", result.Skills[0].Name);
        Assert.AreEqual("repo", result.Skills[0].Scope);
        Assert.AreEqual("/repo/.codex/skills/review-diff/SKILL.md", result.Skills[0].Path);
        Assert.IsTrue(result.Skills[0].Enabled);
        Assert.AreEqual("/repo", result.Skills[0].Cwd);
        Assert.AreEqual(1, result.Errors.Count);
        Assert.AreEqual("SKILL.md front matter is not valid YAML.", result.Errors[0].Message);
        Assert.AreEqual("/repo/.codex/skills/broken/SKILL.md", result.Errors[0].Path);
        Assert.AreEqual("/repo", result.Errors[0].Cwd);

        JsonElement parameters = ParametersFor(connection, "skills/list");
        Assert.AreEqual(0, parameters.GetProperty("cwds").GetArrayLength());
        Assert.IsFalse(parameters.GetProperty("forceReload").GetBoolean());
    }

    [TestMethod]
    public async Task ListSkills_ReturnsEmptyWhenDataPropertyMissing()
    {
        // Matches the Fake app-server's catch-all response (`_ => new { }`) for an unhandled
        // method: no "data" property at all. A naive result.GetProperty("data") would throw
        // KeyNotFoundException here instead of degrading gracefully.
        var connection = new RecordingConnection
        {
            Handler = (method, _) => JsonSerializer.SerializeToElement(new { }),
        };
        await using var service = CreateService();
        await service.InitializeAsync(connection, Options(), CancellationToken.None);

        ListSkillsResult result = await service.ListSkillsAsync(forceReload: false, CancellationToken.None);

        Assert.IsTrue(result.IsSupported);
        Assert.AreEqual(0, result.Skills.Count);
        Assert.AreEqual(0, result.Errors.Count);
    }

    [TestMethod]
    public async Task ListSkills_DropsSkillsMissingRequiredFields()
    {
        var connection = new RecordingConnection
        {
            Handler = (method, _) => method == "skills/list"
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
                                // Missing "path" — required by SkillMetadata, must be dropped.
                                new { name = "no-path", description = "d", enabled = true, scope = "repo" },
                                // Missing "scope" — required by SkillMetadata, must be dropped.
                                new { name = "no-scope", description = "d", enabled = true, path = "/repo/.codex/skills/no-scope/SKILL.md" },
                                // Complete entry — must survive.
                                new { name = "complete", description = "d", enabled = true, path = "/repo/.codex/skills/complete/SKILL.md", scope = "repo" },
                            },
                        },
                    },
                })
                : JsonSerializer.SerializeToElement(new { }),
        };
        await using var service = CreateService();
        await service.InitializeAsync(connection, Options(), CancellationToken.None);

        ListSkillsResult result = await service.ListSkillsAsync(forceReload: false, CancellationToken.None);

        Assert.AreEqual(1, result.Skills.Count);
        Assert.AreEqual("complete", result.Skills[0].Name);
    }

    [TestMethod]
    public async Task ListSkills_DropsSkillsWithNonRootedPath()
    {
        var connection = new RecordingConnection
        {
            Handler = (method, _) => method == "skills/list"
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
                                new { name = "relative", description = "d", enabled = true, path = "relative/path", scope = "repo" },
                                new { name = "rooted", description = "d", enabled = true, path = "/repo/.codex/skills/rooted/SKILL.md", scope = "repo" },
                            },
                        },
                    },
                })
                : JsonSerializer.SerializeToElement(new { }),
        };
        await using var service = CreateService();
        await service.InitializeAsync(connection, Options(), CancellationToken.None);

        ListSkillsResult result = await service.ListSkillsAsync(forceReload: false, CancellationToken.None);

        Assert.AreEqual(1, result.Skills.Count);
        Assert.AreEqual("rooted", result.Skills[0].Name);
    }

    [TestMethod]
    public async Task ListSkills_NormalizesErrorPathToNullWhenNotRooted()
    {
        // SkillLoadError.Path must go through the same rooted-path validation as SkillInfo.Path
        // (NormalizeSkillPath), not just length/control-character sanitization -- a relative or
        // malformed path must not be forwarded over the contract as if it were absolute.
        var connection = new RecordingConnection
        {
            Handler = (method, _) => method == "skills/list"
                ? JsonSerializer.SerializeToElement(new
                {
                    data = new object[]
                    {
                        new
                        {
                            cwd = "/repo",
                            errors = new object[]
                            {
                                new { message = "relative path error", path = "relative/SKILL.md" },
                                new { message = "rooted path error", path = "/repo/.codex/skills/broken/SKILL.md" },
                            },
                            skills = Array.Empty<object>(),
                        },
                    },
                })
                : JsonSerializer.SerializeToElement(new { }),
        };
        await using var service = CreateService();
        await service.InitializeAsync(connection, Options(), CancellationToken.None);

        ListSkillsResult result = await service.ListSkillsAsync(forceReload: false, CancellationToken.None);

        Assert.HasCount(2, result.Errors);
        Assert.IsNull(result.Errors[0].Path);
        Assert.AreEqual("/repo/.codex/skills/broken/SKILL.md", result.Errors[1].Path);
    }

    [TestMethod]
    public async Task ListSkills_PreservesLongCwdInsteadOfTruncatingToNull()
    {
        // cwd is a filesystem path, so it must be capped at MaxSkillPathLength (1024) like other
        // path fields, not the shorter MaxSkillTextLength (512) used for prose descriptions --
        // otherwise a long-but-valid working directory would be silently dropped to null.
        string longCwd = "/" + string.Join('/', Enumerable.Repeat("segment", 80));
        Assert.IsTrue(longCwd.Length > 512 && longCwd.Length <= 1024);
        var connection = new RecordingConnection
        {
            Handler = (method, _) => method == "skills/list"
                ? JsonSerializer.SerializeToElement(new
                {
                    data = new object[]
                    {
                        new
                        {
                            cwd = longCwd,
                            errors = Array.Empty<object>(),
                            skills = new object[]
                            {
                                new { name = "review-diff", description = "d", enabled = true, path = "/repo/.codex/skills/review-diff", scope = "repo" },
                            },
                        },
                    },
                })
                : JsonSerializer.SerializeToElement(new { }),
        };
        await using var service = CreateService();
        await service.InitializeAsync(connection, Options(), CancellationToken.None);

        ListSkillsResult result = await service.ListSkillsAsync(forceReload: false, CancellationToken.None);

        Assert.AreEqual(longCwd, result.Skills[0].Cwd);
    }

    [TestMethod]
    public async Task ListSkills_RedactsSecretsInDescriptionAndErrorMessage()
    {
        var connection = new RecordingConnection
        {
            Handler = (method, _) => method == "skills/list"
                ? JsonSerializer.SerializeToElement(new
                {
                    data = new object[]
                    {
                        new
                        {
                            cwd = "/repo",
                            errors = new object[]
                            {
                                new { message = "Failed with token=secret-value", path = "/repo/.codex/skills/broken/SKILL.md" },
                            },
                            skills = new object[]
                            {
                                new
                                {
                                    name = "leaky",
                                    description = "Uses api_key=secret-value internally.",
                                    enabled = true,
                                    path = "/repo/.codex/skills/leaky/SKILL.md",
                                    scope = "repo",
                                },
                            },
                        },
                    },
                })
                : JsonSerializer.SerializeToElement(new { }),
        };
        await using var service = CreateService();
        await service.InitializeAsync(connection, Options(), CancellationToken.None);

        ListSkillsResult result = await service.ListSkillsAsync(forceReload: false, CancellationToken.None);

        StringAssert.Contains(result.Skills[0].Description, "[REDACTED]");
        StringAssert.DoesNotMatch(result.Skills[0].Description, new System.Text.RegularExpressions.Regex("secret-value"));
        StringAssert.Contains(result.Errors[0].Message, "[REDACTED]");
    }

    [TestMethod]
    public async Task ListSkills_TruncatesBeyondMaximumSkillCount()
    {
        object[] skills = Enumerable.Range(0, 205)
            .Select(index => (object)new
            {
                name = $"skill-{index}",
                description = "d",
                enabled = true,
                path = $"/repo/.codex/skills/skill-{index}",
                scope = "repo",
            })
            .ToArray();
        var connection = new RecordingConnection
        {
            Handler = (method, _) => method == "skills/list"
                ? JsonSerializer.SerializeToElement(new
                {
                    data = new object[] { new { cwd = "/repo", errors = Array.Empty<object>(), skills } },
                })
                : JsonSerializer.SerializeToElement(new { }),
        };
        await using var service = CreateService();
        await service.InitializeAsync(connection, Options(), CancellationToken.None);

        ListSkillsResult result = await service.ListSkillsAsync(forceReload: false, CancellationToken.None);

        Assert.AreEqual(200, result.Skills.Count);
        Assert.IsTrue(result.IsTruncated);
    }

    [TestMethod]
    public async Task ListSkills_StopsScanningEntriesOnceBothCapsAreReached()
    {
        // skills/list is untrusted and unbounded: once one entry alone fills both the skill and
        // error caps, a second entry must not be scanned at all (not just capped) so a
        // pathological response with a huge "data" array cannot force unbounded per-entry work.
        object[] fillerSkills = Enumerable.Range(0, 200)
            .Select(index => (object)new
            {
                name = $"skill-{index}",
                description = "d",
                enabled = true,
                path = $"/repo/.codex/skills/skill-{index}",
                scope = "repo",
            })
            .ToArray();
        object[] fillerErrors = Enumerable.Range(0, 50)
            .Select(index => (object)new { message = $"error-{index}", path = "/repo/.codex/skills/broken/SKILL.md" })
            .ToArray();
        var connection = new RecordingConnection
        {
            Handler = (method, _) => method == "skills/list"
                ? JsonSerializer.SerializeToElement(new
                {
                    data = new object[]
                    {
                        new { cwd = "/repo-a", errors = fillerErrors, skills = fillerSkills },
                        new
                        {
                            cwd = "/repo-b",
                            errors = Array.Empty<object>(),
                            skills = new object[]
                            {
                                new { name = "should-not-appear", description = "d", enabled = true, path = "/repo-b/.codex/skills/should-not-appear/SKILL.md", scope = "repo" },
                            },
                        },
                    },
                })
                : JsonSerializer.SerializeToElement(new { }),
        };
        await using var service = CreateService();
        await service.InitializeAsync(connection, Options(), CancellationToken.None);

        ListSkillsResult result = await service.ListSkillsAsync(forceReload: false, CancellationToken.None);

        Assert.AreEqual(200, result.Skills.Count);
        Assert.AreEqual(50, result.Errors.Count);
        Assert.IsTrue(result.IsTruncated);
        Assert.IsFalse(result.Skills.Any(skill => skill.Name == "should-not-appear"));
    }

    [TestMethod]
    public async Task ListSkills_ReturnsUnsupportedWhenMethodUnknown()
    {
        var connection = new RecordingConnection
        {
            Handler = (method, _) => method == "skills/list"
                ? throw new JsonRpcRemoteException(-32601, "Method not found")
                : JsonSerializer.SerializeToElement(new { }),
        };
        await using var service = CreateService();
        await service.InitializeAsync(connection, Options(), CancellationToken.None);

        ListSkillsResult first = await service.ListSkillsAsync(forceReload: false, CancellationToken.None);
        ListSkillsResult second = await service.ListSkillsAsync(forceReload: false, CancellationToken.None);

        Assert.IsFalse(first.IsSupported);
        Assert.IsFalse(second.IsSupported);
        Assert.AreEqual(1, connection.Requests.Count(item => item.Method == "skills/list"));
    }

    [TestMethod]
    public async Task ThreadResponsesAndSettingsNotificationTrackEffectiveApprovalState()
    {
        var connection = new RecordingConnection
        {
            Handler = (method, _) => method == "thread/start"
                ? JsonSerializer.SerializeToElement(new
                {
                    thread = new { id = "thread-1" },
                    activePermissionProfile = new { id = "review" },
                    approvalPolicy = "on-request",
                    approvalsReviewer = "auto_review",
                    sandbox = new { type = "workspaceWrite" },
                    effort = "medium",
                    serviceTier = "standard",
                })
                : JsonSerializer.SerializeToElement(new { }),
        };
        await using var service = CreateService();
        await service.InitializeAsync(connection, Options(), CancellationToken.None);

        ThreadSummary thread = await service.StartThreadAsync(CancellationToken.None);

        Assert.AreEqual("review", thread.EffectiveApprovalState!.ActivePermissionProfile);
        Assert.AreEqual("auto_review", service.EffectiveApprovalState!.ApprovalsReviewer);
        Assert.AreEqual("medium", thread.EffectiveReasoningEffort);
        Assert.AreEqual("standard", thread.EffectiveServiceTier);
        var changed = new TaskCompletionSource<EffectiveApprovalState>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.EffectiveApprovalStateChanged += (value, _) =>
        {
            changed.TrySetResult(value);
            return Task.CompletedTask;
        };
        await connection.EmitNotificationAsync(
            "thread/settings/updated",
            new
            {
                threadId = "thread-1",
                threadSettings = new
                {
                    activePermissionProfile = new { id = ":workspace" },
                    approvalPolicy = "never",
                    approvalsReviewer = "user",
                    sandboxPolicy = new { type = "dangerFullAccess" },
                    effort = "high",
                    serviceTier = "fast",
                },
            });

        EffectiveApprovalState updated = await changed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.AreEqual(":workspace", updated.ActivePermissionProfile);
        Assert.AreEqual("never", updated.ApprovalPolicy);
        Assert.AreEqual("user", updated.ApprovalsReviewer);
        Assert.AreEqual("dangerFullAccess", updated.SandboxMode);
        Assert.AreEqual("high", service.EffectiveReasoningEffort);
        Assert.AreEqual("fast", service.EffectiveServiceTier);

        await connection.EmitNotificationAsync(
            "thread/settings/updated",
            new
            {
                threadId = "stale-thread",
                threadSettings = new
                {
                    activePermissionProfile = new { id = "stale" },
                    approvalPolicy = "never",
                    approvalsReviewer = "user",
                    sandboxPolicy = new { type = "readOnly" },
                },
            });
        Assert.AreSame(updated, service.EffectiveApprovalState);
    }

    [TestMethod]
    public async Task ResumeAndForkResponsesReplaceEffectiveApprovalState()
    {
        var connection = new RecordingConnection
        {
            Handler = (method, _) => method switch
            {
                "thread/resume" => EffectiveThreadResponse("thread-resumed", "resume-profile", "auto_review"),
                "thread/fork" => EffectiveThreadResponse("thread-forked", "fork-profile", "user"),
                _ => JsonSerializer.SerializeToElement(new { }),
            },
        };
        await using var service = CreateService();
        await service.InitializeAsync(connection, Options(), CancellationToken.None);

        ThreadSummary resumed = await service.ResumeThreadAsync("thread-resumed", CancellationToken.None);
        ForkThreadResult forked = await service.ForkThreadAsync(
            new ForkThreadRequest { ThreadId = "thread-resumed" },
            CancellationToken.None);

        Assert.AreEqual("resume-profile", resumed.EffectiveApprovalState!.ActivePermissionProfile);
        Assert.AreEqual("auto_review", resumed.EffectiveApprovalState.ApprovalsReviewer);
        Assert.AreEqual("high", resumed.EffectiveReasoningEffort);
        Assert.AreEqual("fast", resumed.EffectiveServiceTier);
        Assert.AreEqual("fork-profile", forked.Thread!.EffectiveApprovalState!.ActivePermissionProfile);
        Assert.AreEqual("user", service.EffectiveApprovalState!.ApprovalsReviewer);
    }

    private static JsonElement EffectiveThreadResponse(string threadId, string profileId, string reviewer)
        => JsonSerializer.SerializeToElement(new
        {
            thread = new
            {
                id = threadId,
                settings = new { reasoningEffort = "high", serviceTier = "fast" },
            },
            activePermissionProfile = new { id = profileId },
            approvalPolicy = "on-request",
            approvalsReviewer = reviewer,
            sandbox = new { type = "workspaceWrite" },
        });

    private static JsonElement PermissionProfilePage(object? parameters, string longDescription)
    {
        JsonElement value = JsonSerializer.SerializeToElement(parameters, WireJsonOptions);
        string? cursor = value.TryGetProperty("cursor", out JsonElement cursorValue)
            ? cursorValue.GetString()
            : null;
        return cursor is null
            ? JsonSerializer.SerializeToElement(new
            {
                data = new object[]
                {
                    new { id = "review", description = string.Concat(Enumerable.Repeat(longDescription, 12)) + "\u001b", allowed = true },
                    new { id = "custom", description = "A legal raw profile id that is namespaced by the UI.", allowed = false },
                    new { id = new string('x', 257), description = "too long", allowed = true },
                },
                nextCursor = "page-2",
            })
            : JsonSerializer.SerializeToElement(new
            {
                data = new[]
                {
                    new { id = "review", description = "duplicate", allowed = false },
                    new { id = ":workspace", description = "built-in profile", allowed = true },
                },
                nextCursor = (string?)null,
            });
    }

    [TestMethod]
    public async Task SlashOperationsUseTypedAppServerMethodsAndParameters()
    {
        var connection = new RecordingConnection
        {
            Handler = (method, _) => method switch
            {
                "review/start" => JsonSerializer.SerializeToElement(new
                {
                    reviewThreadId = "thread-review",
                    turn = new { id = "turn-review" },
                }),
                "thread/fork" => JsonSerializer.SerializeToElement(new
                {
                    thread = new { id = "thread-fork", preview = "forked" },
                }),
                "thread/goal/get" or "thread/goal/set" => JsonSerializer.SerializeToElement(new
                {
                    goal = new
                    {
                        threadId = "thread-1",
                        objective = "Ship typed slash commands",
                        status = "active",
                        tokenBudget = 1000L,
                        tokensUsed = 10L,
                        timeUsedSeconds = 5L,
                        createdAt = 1L,
                        updatedAt = 2L,
                    },
                }),
                "thread/goal/clear" => JsonSerializer.SerializeToElement(new { cleared = true }),
                "mcpServerStatus/list" => JsonSerializer.SerializeToElement(new
                {
                    data = new[]
                    {
                        new
                        {
                            name = "docs",
                            authStatus = "oAuth",
                            tools = new Dictionary<string, object>
                            {
                                ["search"] = new { description = "Search" },
                            },
                            resources = Array.Empty<object>(),
                            resourceTemplates = Array.Empty<object>(),
                            serverInfo = new { title = "Documentation" },
                        },
                    },
                    nextCursor = (string?)null,
                }),
                "feedback/upload" => JsonSerializer.SerializeToElement(new { threadId = "thread-1" }),
                "account/rateLimits/read" => JsonSerializer.SerializeToElement(new
                {
                    rateLimits = new
                    {
                        limitId = "codex",
                        limitName = "Codex",
                        planType = "plus",
                        primary = new { usedPercent = 20, resetsAt = 100L, windowDurationMins = 300L },
                        secondary = (object?)null,
                        credits = new { hasCredits = true, unlimited = false, balance = "10" },
                    },
                    rateLimitsByLimitId = (object?)null,
                }),
                _ => JsonSerializer.SerializeToElement(new { }),
            },
        };
        await using var service = CreateService();
        await service.InitializeAsync(connection, Options(), CancellationToken.None);

        CompactThreadResult compact = await service.CompactThreadAsync(
            new CompactThreadRequest { ThreadId = "thread-1" },
            CancellationToken.None);
        StartReviewResult review = await service.StartReviewAsync(
            new StartReviewRequest
            {
                ThreadId = "thread-1",
                Delivery = ReviewDelivery.Detached,
                Target = new ReviewTarget { Kind = ReviewTargetKind.BaseBranch, Value = "main" },
            },
            CancellationToken.None);
        ForkThreadResult fork = await service.ForkThreadAsync(
            new ForkThreadRequest { ThreadId = "thread-1" },
            CancellationToken.None);
        ThreadGoalResult goal = await service.GetThreadGoalAsync("thread-1", CancellationToken.None);
        await service.SetThreadGoalAsync(
            new SetThreadGoalRequest
            {
                ThreadId = "thread-1",
                Objective = "Ship typed slash commands",
                Status = ThreadGoalStatus.Active,
                TokenBudget = 1000,
            },
            CancellationToken.None);
        ThreadGoalResult cleared = await service.ClearThreadGoalAsync("thread-1", CancellationToken.None);
        McpServerListResult mcp = await service.ListMcpServersAsync("thread-1", CancellationToken.None);
        UploadFeedbackResult feedback = await service.UploadFeedbackAsync(
            new UploadFeedbackRequest
            {
                Classification = "bug",
                Reason = "Something failed.",
                IncludeLogs = false,
                ThreadId = "thread-1",
            },
            CancellationToken.None);
        RateLimitsResult rateLimits = await service.GetRateLimitsAsync(CancellationToken.None);

        Assert.IsTrue(compact.IsSupported);
        Assert.AreEqual("thread-review", review.ReviewThreadId);
        Assert.AreEqual("turn-review", review.TurnId);
        Assert.AreEqual("thread-fork", fork.Thread?.Id);
        Assert.AreEqual("Ship typed slash commands", goal.Goal?.Objective);
        Assert.IsTrue(cleared.Cleared);
        Assert.AreEqual("docs", mcp.Servers[0].Name);
        Assert.AreEqual("search", mcp.Servers[0].ToolNames[0]);
        Assert.AreEqual("thread-1", feedback.ThreadId);
        Assert.AreEqual(20, rateLimits.RateLimits?.Primary?.UsedPercent);

        CollectionAssert.AreEqual(
            new[]
            {
                "initialize",
                "account/gatewayOAuth/read",
                "thread/compact/start",
                "review/start",
                "thread/fork",
                "thread/goal/get",
                "thread/goal/set",
                "thread/goal/clear",
                "mcpServerStatus/list",
                "feedback/upload",
                "account/rateLimits/read",
            },
            connection.Requests.Select(item => item.Method).ToArray());
        JsonElement reviewParameters = ParametersFor(connection, "review/start");
        Assert.AreEqual("detached", reviewParameters.GetProperty("delivery").GetString());
        Assert.AreEqual("baseBranch", reviewParameters.GetProperty("target").GetProperty("type").GetString());
        Assert.AreEqual("main", reviewParameters.GetProperty("target").GetProperty("branch").GetString());
        JsonElement goalParameters = ParametersFor(connection, "thread/goal/set");
        Assert.AreEqual("active", goalParameters.GetProperty("status").GetString());
        Assert.AreEqual(1000L, goalParameters.GetProperty("tokenBudget").GetInt64());
        JsonElement mcpParameters = ParametersFor(connection, "mcpServerStatus/list");
        Assert.AreEqual("toolsAndAuthOnly", mcpParameters.GetProperty("detail").GetString());
        JsonElement feedbackParameters = ParametersFor(connection, "feedback/upload");
        Assert.IsFalse(feedbackParameters.GetProperty("includeLogs").GetBoolean());
    }

    [TestMethod]
    public async Task GetRateLimitsAsync_MissingUsedPercent_RemainsUnknown()
    {
        var connection = new RecordingConnection
        {
            Handler = (method, _) => method == "account/rateLimits/read"
                ? JsonSerializer.SerializeToElement(new
                {
                    rateLimits = new
                    {
                        limitId = "codex",
                        primary = new { resetsAt = 1_800_000_000L, windowDurationMins = 300L },
                    },
                })
                : JsonSerializer.SerializeToElement(new { }),
        };
        await using var service = CreateService();
        await service.InitializeAsync(connection, Options(), CancellationToken.None);

        RateLimitsResult result = await service.GetRateLimitsAsync(CancellationToken.None);

        Assert.IsNull(result.RateLimits?.Primary?.UsedPercent);
        Assert.AreEqual(1_800_000_000L, result.RateLimits?.Primary?.ResetsAt);
        Assert.AreEqual(300L, result.RateLimits?.Primary?.WindowDurationMinutes);
    }

    [TestMethod]
    public async Task MethodNotFoundDisablesOnlyThatOperationForTheSession()
    {
        var connection = new RecordingConnection
        {
            Handler = (method, _) => method == "thread/compact/start"
                ? throw new JsonRpcRemoteException(-32601, "Method not found")
                : method == "account/rateLimits/read"
                    ? JsonSerializer.SerializeToElement(new { rateLimits = new { } })
                    : JsonSerializer.SerializeToElement(new { }),
        };
        await using var service = CreateService();
        await service.InitializeAsync(connection, Options(), CancellationToken.None);

        CompactThreadResult first = await service.CompactThreadAsync(
            new CompactThreadRequest { ThreadId = "thread-1" },
            CancellationToken.None);
        CompactThreadResult second = await service.CompactThreadAsync(
            new CompactThreadRequest { ThreadId = "thread-1" },
            CancellationToken.None);
        RateLimitsResult otherOperation = await service.GetRateLimitsAsync(CancellationToken.None);

        Assert.IsFalse(first.IsSupported);
        Assert.IsFalse(second.IsSupported);
        Assert.IsTrue(otherOperation.IsSupported);
        Assert.AreEqual(1, connection.Requests.Count(item => item.Method == "thread/compact/start"));
        Assert.AreEqual(1, connection.Requests.Count(item => item.Method == "account/rateLimits/read"));
    }

    [TestMethod]
    public async Task NonIdempotentSlashOperationIsNotRetried()
    {
        var connection = new RecordingConnection
        {
            Handler = (method, _) => method == "feedback/upload"
                ? throw new JsonRpcRemoteException(-32001, "overloaded")
                : JsonSerializer.SerializeToElement(new { }),
        };
        await using var service = CreateService();
        await service.InitializeAsync(connection, Options(), CancellationToken.None);

        await Assert.ThrowsExactlyAsync<JsonRpcRemoteException>(() => service.UploadFeedbackAsync(
            new UploadFeedbackRequest { Classification = "bug" },
            CancellationToken.None));

        Assert.AreEqual(1, connection.Requests.Count(item => item.Method == "feedback/upload"));
    }

    [TestMethod]
    public async Task CompactionReviewAndGoalNotificationsUseDedicatedEvents()
    {
        var connection = new RecordingConnection();
        await using var service = CreateService();
        await service.InitializeAsync(connection, Options(), CancellationToken.None);
        var conversationEvents = new List<ConversationEvent>();
        var compactionEvents = new List<ContextCompactionEvent>();
        var reviewEvents = new List<ReviewModeEvent>();
        var goalEvents = new List<ThreadGoalEvent>();
        service.ConversationEventReceived += (value, _) =>
        {
            conversationEvents.Add(value);
            return Task.CompletedTask;
        };
        service.ContextCompacted += (value, _) =>
        {
            compactionEvents.Add(value);
            return Task.CompletedTask;
        };
        service.ReviewModeChanged += (value, _) =>
        {
            reviewEvents.Add(value);
            return Task.CompletedTask;
        };
        service.ThreadGoalChanged += (value, _) =>
        {
            goalEvents.Add(value);
            return Task.CompletedTask;
        };

        await connection.EmitNotificationAsync(
            "item/completed",
            new
            {
                threadId = "thread-1",
                turnId = "turn-1",
                item = new { id = "compact-1", type = "contextCompaction" },
            });
        await connection.EmitNotificationAsync(
            "item/completed",
            new
            {
                threadId = "thread-1",
                turnId = "turn-1",
                item = new
                {
                    id = "review-1",
                    type = "enteredReviewMode",
                    review = "Review token=secret-value",
                },
            });
        await connection.EmitNotificationAsync(
            "thread/goal/updated",
            new
            {
                threadId = "thread-1",
                turnId = "turn-1",
                goal = new
                {
                    threadId = "thread-1",
                    objective = "Do not expose password=secret-value",
                    status = "active",
                    tokenBudget = (long?)null,
                    tokensUsed = 0L,
                    timeUsedSeconds = 0L,
                    createdAt = 1L,
                    updatedAt = 2L,
                },
            });

        Assert.AreEqual(
            0,
            conversationEvents.Count,
            string.Join(" | ", conversationEvents.Select(item => $"{item.Kind}:{item.Text}")));
        Assert.AreEqual(1, compactionEvents.Count);
        Assert.IsTrue(compactionEvents[0].IsCompleted);
        Assert.AreEqual(1, reviewEvents.Count);
        Assert.AreEqual(ReviewModeChangeKind.Entered, reviewEvents[0].ChangeKind);
        StringAssert.Contains(reviewEvents[0].Review, "[REDACTED]");
        Assert.AreEqual(1, goalEvents.Count);
        StringAssert.Contains(goalEvents[0].Goal?.Objective, "[REDACTED]");
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
        // A different account on the same owner is an owner boundary, so each state uses its own owner.
        async Task<AccountStatus> ReadAsync(bool signedIn)
        {
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
            return await service.GetAccountStatusAsync(CancellationToken.None);
        }

        AccountStatus signedOut = await ReadAsync(signedIn: false);
        AccountStatus signedInStatus = await ReadAsync(signedIn: true);

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
    public async Task InterruptLogsRequestAcknowledgementAndTimeUntilTheTurnEnds()
    {
        var connection = new RecordingConnection();
        await using var service = CreateService();
        await service.InitializeAsync(connection, Options(), CancellationToken.None);
        TextWriter originalError = Console.Error;
        using var log = new StringWriter();
        Console.SetError(log);
        try
        {
            await service.InterruptTurnAsync(
                new InterruptTurnRequest { ThreadId = "thread-1", TurnId = "turn-1" },
                CancellationToken.None);
            await connection.EmitNotificationAsync(
                "turn/completed",
                new { threadId = "thread-1", turn = new { id = "turn-1", status = "interrupted" } });

            // A later completion of a turn nobody asked to stop is not reported as interrupted.
            await connection.EmitNotificationAsync(
                "turn/completed",
                new { threadId = "thread-1", turn = new { id = "turn-2", status = "completed" } });
        }
        finally
        {
            Console.SetError(originalError);
        }

        RecordedRequest interrupt = connection.Requests.Single(request => request.Method == "turn/interrupt");
        Assert.AreEqual("turn-1", JsonSerializer.SerializeToElement(interrupt.Parameters).GetProperty("turnId").GetString());
        string text = log.ToString();
        StringAssert.Contains(text, "turn/interrupt requested thread=thread-1 turn=turn-1");
        StringAssert.Contains(text, "turn/interrupt acknowledged turn=turn-1 elapsedMs=");
        StringAssert.Contains(text, "turn completed after interrupt request turn=turn-1 status=interrupted elapsedMs=");
        Assert.IsFalse(text.Contains("turn=turn-2", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task AccountReadAcceptsPlanTypeAddedInContract0159()
    {
        var connection = new RecordingConnection
        {
            Handler = (method, _) => method == "account/read"
                ? JsonSerializer.SerializeToElement(new
                {
                    account = new { type = "chatgpt", planType = "promax" },
                    workspaceRouting = new { accountRoutingOverride = "NO_CONSTRAINT" },
                })
                : JsonSerializer.SerializeToElement(new { }),
        };
        await using var service = CreateService();
        await service.InitializeAsync(connection, Options(), CancellationToken.None);

        AccountStatus status = await service.GetAccountStatusAsync(CancellationToken.None);

        Assert.AreEqual(AccountState.SignedIn, status.State);
        Assert.AreEqual("promax", status.PlanType);
    }

    [TestMethod]
    public async Task ListModelsToleratesAccessProgramsAddedInContract0159()
    {
        // 0.159.1 adds availableAccessPrograms to Model and promotes a new default model. The
        // catalog must still parse, and the new default must be offered in the picker.
        var connection = new RecordingConnection
        {
            Handler = (method, _) => method == "model/list"
                ? JsonSerializer.SerializeToElement(new
                {
                    data = new object[]
                    {
                        new
                        {
                            model = "gpt-6.1-sol",
                            displayName = "GPT-6.1 Sol",
                            isDefault = true,
                            hidden = false,
                            defaultReasoningEffort = "medium",
                            supportedReasoningEfforts = new[] { new { reasoningEffort = "medium", description = "Balanced" } },
                            availableAccessPrograms = new { cyber = new[] { "daybreak" } },
                        },
                        new
                        {
                            model = "gpt-6-astra",
                            displayName = "GPT-6 Astra",
                            isDefault = false,
                            hidden = false,
                            availableAccessPrograms = (object?)null,
                        },
                    },
                    nextCursor = (string?)null,
                })
                : JsonSerializer.SerializeToElement(new { }),
        };
        await using var service = CreateService();
        await service.InitializeAsync(connection, Options(), CancellationToken.None);

        ListModelsResult result = await service.ListModelsAsync(CancellationToken.None);

        CollectionAssert.AreEqual(new[] { "gpt-6.1-sol", "gpt-6-astra" }, result.Models.Select(model => model.Id).ToArray());
        Assert.AreEqual("gpt-6.1-sol", result.DefaultModel);
        Assert.AreEqual("medium", result.Models[0].DefaultReasoningEffort);
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
    public async Task LogoutRequestsAccountLogoutAndRetiresTheOwnerConnection()
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
            new[] { "initialize", "account/gatewayOAuth/read", "account/logout" },
            connection.Requests.Select(item => item.Method).ToArray());
        Assert.IsFalse(service.IsConnectionActive);
        Assert.IsTrue(service.InvalidatedByOwnerAction);
        Assert.AreEqual(AccountState.SignedOut, service.InvalidatedAccountState);
    }

    [TestMethod]
    public async Task AccountNotificationBeforeTheLogoutResponseIsStillTheOwnersLogout()
    {
        RecordingConnection? connection = null;
        connection = new RecordingConnection
        {
            AsyncHandler = async (method, _, _) =>
            {
                if (method == "account/logout")
                {
                    // The notification for this logout is processed before its response.
                    await connection!.EmitNotificationAsync("account/updated", new { authMode = (string?)null, planType = (string?)null });
                }

                return method == "account/read"
                    ? JsonSerializer.SerializeToElement(new { account = new { type = "chatgpt", email = "a@example.test", planType = "plus" } })
                    : JsonSerializer.SerializeToElement(new { });
            },
        };
        await using var service = CreateService();
        await service.InitializeAsync(connection, Options(), CancellationToken.None);
        await service.GetAccountStatusAsync(CancellationToken.None);

        AccountStatus result = await service.LogoutAccountAsync(CancellationToken.None);

        Assert.AreEqual(AccountState.SignedOut, result.State);
        Assert.IsFalse(service.IsConnectionActive);
        Assert.IsTrue(service.InvalidatedByOwnerAction);
        Assert.AreEqual(AccountState.SignedOut, service.InvalidatedAccountState);
    }

    [TestMethod]
    public async Task LoginCompletionForTheOwnersSignInIsAnOwnerAction()
    {
        var connection = new RecordingConnection
        {
            Handler = (method, _) => method == "account/login/start"
                ? JsonSerializer.SerializeToElement(new { type = "chatgpt", loginId = "login-1", authUrl = "https://auth.example.test/start" })
                : JsonSerializer.SerializeToElement(new { }),
        };
        await using var service = CreateService();
        await service.InitializeAsync(connection, Options(), CancellationToken.None);
        await service.StartAccountLoginAsync(CancellationToken.None);

        await connection.EmitNotificationAsync("account/login/completed", new { loginId = "login-1", success = true });

        Assert.IsFalse(service.IsConnectionActive);
        Assert.IsTrue(service.InvalidatedByOwnerAction);
    }

    [TestMethod]
    [DataRow("account/updated", null)]
    [DataRow("account/login/completed", "someone-else")]
    [DataRow("account/login/completed", "")]
    public async Task UnsolicitedNotificationForTheSameAccountKeepsTheOwner(string method, string? loginId)
    {
        int reads = 0;
        var connection = new RecordingConnection
        {
            Handler = (requestMethod, _) =>
            {
                if (requestMethod == "account/read")
                {
                    reads++;
                    return JsonSerializer.SerializeToElement(new { account = new { type = "chatgpt", email = "a@example.test", planType = "plus" } });
                }

                return requestMethod == "account/login/start"
                    ? JsonSerializer.SerializeToElement(new { type = "chatgpt", loginId = "login-1", authUrl = "https://auth.example.test/start" })
                    : JsonSerializer.SerializeToElement(new { });
            },
        };
        await using var service = CreateService();
        await service.InitializeAsync(connection, Options(), CancellationToken.None);
        await service.GetAccountStatusAsync(CancellationToken.None);
        await service.StartAccountLoginAsync(CancellationToken.None);

        object parameters = method == "account/updated"
            ? new { authMode = "chatgpt", planType = "plus" }
            : string.IsNullOrEmpty(loginId)
                ? new { success = true }
                : new { loginId, success = true };
        await connection.EmitNotificationAsync(method, parameters);

        Assert.AreEqual(2, reads);
        Assert.IsTrue(service.IsConnectionActive);
        Assert.IsFalse(service.InvalidatedByOwnerAction);
    }

    [TestMethod]
    public async Task UnsolicitedNotificationForAChangedAccountRetiresTheOwner()
    {
        string email = "a@example.test";
        var connection = new RecordingConnection
        {
            Handler = (method, _) => method == "account/read"
                ? JsonSerializer.SerializeToElement(new { account = new { type = "chatgpt", email, planType = "plus" } })
                : JsonSerializer.SerializeToElement(new { }),
        };
        await using var service = CreateService();
        await service.InitializeAsync(connection, Options(), CancellationToken.None);
        var statuses = new List<AccountStatus>();
        service.AccountStatusChanged += (value, _) =>
        {
            statuses.Add(value);
            return Task.CompletedTask;
        };
        await service.GetAccountStatusAsync(CancellationToken.None);

        email = "b@example.test";
        await connection.EmitNotificationAsync("account/updated", new { authMode = "chatgpt", planType = "plus" });

        Assert.IsFalse(service.IsConnectionActive);
        Assert.IsFalse(service.InvalidatedByOwnerAction);
        Assert.AreEqual(AccountState.Unavailable, statuses[^1].State);
        Assert.AreEqual(1, statuses.Count(status => status.State == AccountState.SignedIn));
    }

    [TestMethod]
    public async Task NotificationBeforeTheFirstAccountReadIsCoveredByThatRead()
    {
        int reads = 0;
        var connection = new RecordingConnection
        {
            Handler = (method, _) =>
            {
                if (method == "account/read")
                {
                    reads++;
                }

                return method == "account/read"
                    ? JsonSerializer.SerializeToElement(new { account = new { type = "chatgpt", planType = "plus" } })
                    : JsonSerializer.SerializeToElement(new { });
            },
        };
        await using var service = CreateService();
        await service.InitializeAsync(connection, Options(), CancellationToken.None);

        await connection.EmitNotificationAsync("account/updated", new { authMode = "chatgpt", planType = "plus" });
        AccountStatus status = await service.GetAccountStatusAsync(CancellationToken.None);

        Assert.AreEqual(1, reads);
        Assert.AreEqual(AccountState.SignedIn, status.State);
        Assert.IsTrue(service.IsConnectionActive);
    }

    [TestMethod]
    public async Task AccountNotificationReadTimeoutReportsUnavailable()
    {
        bool timeOut = false;
        var connection = new RecordingConnection
        {
            AsyncHandler = (method, _, _) => method == "account/read" && timeOut
                ? Task.FromCanceled<JsonElement>(new CancellationToken(canceled: true))
                : Task.FromResult(JsonSerializer.SerializeToElement(new { })),
        };
        await using var service = CreateService();
        await service.InitializeAsync(connection, Options(), CancellationToken.None);
        await service.GetAccountStatusAsync(CancellationToken.None);
        timeOut = true;
        var statuses = new List<AccountStatus>();
        service.AccountStatusChanged += (value, _) =>
        {
            statuses.Add(value);
            return Task.CompletedTask;
        };

        await connection.EmitNotificationAsync("account/updated", new { authMode = "chatgpt" });

        Assert.AreEqual(AccountState.Unavailable, statuses[^1].State);
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
                ChoiceId = request.Choices.Single(choice => choice.Label == "Approve for this session").ChoiceId,
            },
            cancellationToken);
        await service.InitializeAsync(connection, Options(), CancellationToken.None);
        string cwd = Options().WorkingDirectory;

        await connection.EmitRequestAsync(
            "approval-1",
            "item/commandExecution/requestApproval",
            new { command = "dotnet build", cwd, itemId = "item-1", threadId = "thread-1", turnId = "turn-1", startedAtMs = 1L,
                availableDecisions = new[] { "accept", "acceptForSession", "decline", "cancel" } });
        await connection.EmitRequestAsync(
            "approval-2",
            "item/commandExecution/requestApproval",
            new { command = "dotnet build", cwd, itemId = "item-2", threadId = "thread-1", turnId = "turn-2", startedAtMs = 2L,
                availableDecisions = new[] { "accept", "acceptForSession", "decline", "cancel" } });

        Assert.AreEqual(2, audit.Count);
        Assert.AreEqual(ApprovalAuditAction.GrantCreated, audit[0].Action);
        Assert.AreEqual(ApprovalScope.Session, audit[0].Scope);
        Assert.AreEqual(ApprovalAuditAction.AutoApproved, audit[1].Action);
        Assert.AreEqual(ApprovalScope.Session, audit[1].Scope);
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

            // Simulate the user picking the second option using its opaque Worker-issued id.
            return service.ResolveUserInputAsync(
                new ResolveUserInputRequest
                {
                    RequestId = request.RequestId,
                    Answers = new Dictionary<string, UserInputAnswer>
                    {
                        [request.Questions[0].Id] = new UserInputAnswer
                        {
                            Kind = UserInputAnswerKind.SelectedOptions,
                            OptionIds = [request.Questions[0].Options[1].OptionId],
                        },
                    },
                },
                cancellationToken);
        };
        await service.InitializeAsync(connection, Options(), CancellationToken.None);

        JsonElement result = await connection.EmitRequestAsync(
            "ui-1",
            "item/tool/requestUserInput",
            new
            {
                itemId = "item-1",
                threadId = "thread-1",
                turnId = "turn-1",
                isBlocking = true,
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

    private static CodexSessionService CreateService(ISkillCatalogStore? skillCatalogStore = null)
        => new(
            new ApprovalPolicyEngine(new PathAccessPolicy()),
            new SecretRedactor(),
            null,
            null,
            null,
            skillCatalogStore ?? new NullSkillCatalogStore());

    private static WorkerOptions Options(string? workingDirectory = null, bool experimentalApi = false) => new()
    {
        WorkingDirectory = workingDirectory ?? Path.GetTempPath(),
        ExtensionVersion = "test",
        ExperimentalApi = experimentalApi,
    };

    private static JsonElement ParametersFor(RecordingConnection connection, string method)
        => JsonSerializer.SerializeToElement(
            connection.Requests.Single(item => item.Method == method).Parameters,
            WireJsonOptions);

    [TestMethod]
    public async Task UnknownServerRequestWithQuestionsIsRejectedWithoutUserInputRouting()
    {
        var connection = new RecordingConnection();
        await using var service = CreateService();
        int requests = 0;
        service.UserInputRequested += (_, _) =>
        {
            requests++;
            return Task.CompletedTask;
        };
        await service.InitializeAsync(connection, Options(), CancellationToken.None);

        JsonRpcRemoteException exception = await Assert.ThrowsExactlyAsync<JsonRpcRemoteException>(() =>
            connection.EmitRequestAsync("unknown-1", "future/requestUserInput", new { questions = Array.Empty<object>() }));

        Assert.AreEqual(-32601, exception.Code);
        Assert.AreEqual(0, requests);
    }

    [TestMethod]
    public async Task InitializePreservesMetadataAndClearsItBeforeReinitialize()
    {
        var connection = new RecordingConnection
        {
            Handler = (method, _) => method == "initialize"
                ? JsonSerializer.SerializeToElement(new
                {
                    codexHome = "C:/private/codex",
                    platformFamily = "windows",
                    platformOs = "windows-11",
                    userAgent = "codex-cli/0.159.1",
                })
                : JsonSerializer.SerializeToElement(new { }),
        };
        await using var service = CreateService();
        await service.InitializeAsync(connection, Options(), CancellationToken.None);
        Assert.AreEqual("C:/private/codex", service.InitializationMetadata?.CodexHome);
        Assert.AreEqual("windows", service.InitializationMetadata?.PlatformFamily);

        connection.Handler = (method, _) => method == "initialize"
            ? JsonSerializer.SerializeToElement(new { })
            : JsonSerializer.SerializeToElement(new { });
        await service.InitializeAsync(connection, Options(), CancellationToken.None);
        Assert.IsNotNull(service.InitializationMetadata);
        Assert.IsNull(service.InitializationMetadata?.CodexHome);
    }

    [TestMethod]
    public async Task KnownServerRequestsWithInvalidRequiredFieldsReturnInvalidParams()
    {
        var connection = new RecordingConnection();
        await using var service = CreateService();
        int approvalRequests = 0;
        int inputRequests = 0;
        service.ApprovalRequested += (_, _) =>
        {
            approvalRequests++;
            return Task.CompletedTask;
        };
        service.UserInputRequested += (_, _) =>
        {
            inputRequests++;
            return Task.CompletedTask;
        };
        await service.InitializeAsync(connection, Options(), CancellationToken.None);

        (string Method, object Parameters)[] invalidRequests =
        [
            ("item/commandExecution/requestApproval", new { threadId = "thread-1", turnId = "turn-1", itemId = "item-1" }),
            ("item/fileChange/requestApproval", new { threadId = "thread-1", turnId = "turn-1", itemId = "item-1", startedAtMs = "now" }),
            ("item/permissions/requestApproval", new { threadId = "thread-1", turnId = "turn-1", itemId = "item-1", cwd = "C:/work", permissions = new { } }),
            ("item/tool/requestUserInput", new { threadId = "thread-1", turnId = "turn-1", itemId = "item-1", questions = Array.Empty<object>() }),
        ];

        foreach ((string method, object parameters) in invalidRequests)
        {
            JsonRpcRemoteException exception = await Assert.ThrowsExactlyAsync<JsonRpcRemoteException>(() =>
                connection.EmitRequestAsync("invalid-1", method, parameters));
            Assert.AreEqual(-32602, exception.Code, method);
        }

        Assert.AreEqual(0, approvalRequests);
        Assert.AreEqual(0, inputRequests);
    }

    [TestMethod]
    public async Task CompletedBeforeTurnStartResponseIsNotRevivedByLateStartedNotification()
    {
        var requestStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var response = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        var connection = new RecordingConnection
        {
            AsyncHandler = (method, _, _) =>
            {
                if (method == "turn/start")
                {
                    requestStarted.TrySetResult();
                    return response.Task;
                }

                return Task.FromResult(JsonSerializer.SerializeToElement(new { }));
            },
        };
        await using var service = CreateService();
        var events = new List<ConversationEvent>();
        service.ConversationEventReceived += (value, _) =>
        {
            events.Add(value);
            return Task.CompletedTask;
        };
        await service.InitializeAsync(connection, Options(), CancellationToken.None);

        Task<string> start = service.StartTurnAsync(
            new StartTurnRequest { ThreadId = "thread-1", Text = "hello" },
            CancellationToken.None);
        await requestStarted.Task;
        await connection.EmitNotificationAsync(
            "turn/completed",
            new { threadId = "thread-1", turn = new { id = "turn-1", status = "completed" } });
        response.TrySetResult(JsonSerializer.SerializeToElement(new { turn = new { id = "turn-1" } }));

        Assert.AreEqual("turn-1", await start);
        Assert.IsNull(service.ActiveTurnId);
        int startedEvents = events.Count(item => item.Kind == ConversationEventKind.TurnStarted);
        await connection.EmitNotificationAsync(
            "turn/started",
            new { threadId = "thread-1", turn = new { id = "turn-1" } });
        Assert.IsNull(service.ActiveTurnId);
        Assert.AreEqual(startedEvents, events.Count(item => item.Kind == ConversationEventKind.TurnStarted));
    }

    [TestMethod]
    public async Task OtherThreadOrTurnCompletionDoesNotChangeCurrentTurn()
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
            new StartTurnRequest { ThreadId = "thread-1", Text = "hello" },
            CancellationToken.None);

        await connection.EmitNotificationAsync(
            "turn/completed",
            new { threadId = "thread-2", turn = new { id = "turn-1", status = "completed" } });
        Assert.AreEqual("turn-1", service.ActiveTurnId);
        await connection.EmitNotificationAsync(
            "turn/completed",
            new { threadId = "thread-1", turn = new { id = "turn-2", status = "completed" } });
        Assert.AreEqual("turn-1", service.ActiveTurnId);
        await connection.EmitNotificationAsync(
            "turn/completed",
            new { threadId = "thread-1", turn = new { id = "turn-1", status = "completed" } });
        await connection.EmitNotificationAsync(
            "turn/completed",
            new { threadId = "thread-1", turn = new { id = "turn-1", status = "completed" } });
        Assert.IsNull(service.ActiveTurnId);
    }

    [TestMethod]
    public async Task OldGenerationResponseAndNotificationCannotChangeCurrentState()
    {
        var requestStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var response = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        var oldConnection = new RecordingConnection
        {
            AsyncHandler = (method, _, _) =>
            {
                if (method == "turn/start")
                {
                    requestStarted.TrySetResult();
                    return response.Task;
                }

                return Task.FromResult(JsonSerializer.SerializeToElement(new { }));
            },
        };
        var currentConnection = new RecordingConnection();
        await using var service = CreateService();
        await service.InitializeAsync(oldConnection, Options(), CancellationToken.None);
        Task<string> oldStart = service.StartTurnAsync(
            new StartTurnRequest { ThreadId = "old-thread", Text = "hello" },
            CancellationToken.None);
        await requestStarted.Task;

        await service.InitializeAsync(currentConnection, Options(), CancellationToken.None);
        await oldConnection.EmitNotificationAsync(
            "turn/started",
            new { threadId = "old-thread", turn = new { id = "old-turn" } });
        response.TrySetResult(JsonSerializer.SerializeToElement(new { turn = new { id = "old-turn" } }));

        TurnStartOutcomeUnknownException failure = await Assert.ThrowsExactlyAsync<TurnStartOutcomeUnknownException>(() => oldStart);
        Assert.IsTrue(failure.InnerException is JsonRpcConnectionClosedException);
        Assert.IsNull(service.ActiveThreadId);
        Assert.IsNull(service.ActiveTurnId);
    }

    [TestMethod]
    public async Task ConnectionCloseReleasesApprovalWithoutStaleResolvedNotification()
    {
        var connection = new RecordingConnection();
        var requested = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var service = CreateService();
        int resolved = 0;
        service.ApprovalRequested += (_, _) =>
        {
            requested.TrySetResult();
            return Task.CompletedTask;
        };
        service.ApprovalResolved += (_, _) =>
        {
            resolved++;
            return Task.CompletedTask;
        };
        await service.InitializeAsync(connection, Options(), CancellationToken.None);

        Task<JsonElement> pending = connection.EmitRequestAsync(
            "approval-1",
            "item/commandExecution/requestApproval",
            new { command = "dotnet build", threadId = "thread-1", turnId = "turn-1", itemId = "item-1", startedAtMs = 1L });
        await requested.Task;
        connection.EmitClosed();

        await Assert.ThrowsExactlyAsync<JsonRpcRequestResolvedException>(() => pending);
        Assert.AreEqual(0, resolved);
    }

    [TestMethod]
    public async Task ConnectionCloseReleasesUserInputAndEmitsResolvedOnce()
    {
        var connection = new RecordingConnection();
        var requested = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var service = CreateService();
        int resolved = 0;
        service.UserInputRequested += (_, _) =>
        {
            requested.TrySetResult();
            return Task.CompletedTask;
        };
        service.UserInputResolved += (_, _) =>
        {
            resolved++;
            return Task.CompletedTask;
        };
        await service.InitializeAsync(connection, Options(), CancellationToken.None);

        Task<JsonElement> pending = connection.EmitRequestAsync(
            "input-1",
            "item/tool/requestUserInput",
            new
            {
                threadId = "thread-1",
                turnId = "turn-1",
                itemId = "item-1",
                isBlocking = true,
                questions = new[]
                {
                    new { id = "choice", header = "Choice", question = "Pick one", options = (object?)null },
                },
            });
        await requested.Task;
        connection.EmitClosed();

        await Assert.ThrowsExactlyAsync<JsonRpcRequestResolvedException>(() => pending);
        Assert.AreEqual(1, resolved);
    }

    [TestMethod]
    public async Task OldResolvedEventCannotResolveSameRequestIdInNewGeneration()
    {
        var oldConnection = new RecordingConnection();
        var currentConnection = new RecordingConnection();
        var requestSignals = new Queue<TaskCompletionSource>();
        var firstRequested = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondRequested = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        requestSignals.Enqueue(firstRequested);
        requestSignals.Enqueue(secondRequested);
        await using var service = CreateService();
        int resolved = 0;
        string? oldClientRequestId = null;
        string? currentClientRequestId = null;
        service.ApprovalResolved += (_, _) =>
        {
            resolved++;
            return Task.CompletedTask;
        };
        int requestCount = 0;
        service.ApprovalRequested += (request, _) =>
        {
            if (requestCount++ == 0)
            {
                oldClientRequestId = request.RequestId;
            }
            else if (requestCount == 2)
            {
                currentClientRequestId = request.RequestId;
            }
            requestSignals.Dequeue().TrySetResult();
            return Task.CompletedTask;
        };
        await service.InitializeAsync(oldConnection, Options(), CancellationToken.None);

        object parameters = new { command = "dotnet build", threadId = "thread-1", turnId = "turn-1", itemId = "item-1", startedAtMs = 1L };
        Task<JsonElement> oldRequest = oldConnection.EmitRequestAsync("same-id", "item/commandExecution/requestApproval", parameters);
        await firstRequested.Task;
        await service.InitializeAsync(currentConnection, Options(), CancellationToken.None);
        await Assert.ThrowsExactlyAsync<JsonRpcRequestResolvedException>(() => oldRequest);
        Assert.AreEqual(0, resolved);

        Task<JsonElement> currentRequest = currentConnection.EmitRequestAsync("same-id", "item/commandExecution/requestApproval", parameters);
        await secondRequested.Task;
        await oldConnection.EmitNotificationAsync("serverRequest/resolved", new { requestId = "same-id" });
        Assert.IsFalse(currentRequest.IsCompleted);
        Assert.IsNotNull(oldClientRequestId);
        await service.ResolveApprovalAsync(
            new ResolveApprovalRequest { RequestId = oldClientRequestId!, Decision = ApprovalDecision.Accept },
            CancellationToken.None);
        Assert.IsFalse(currentRequest.IsCompleted);
        Assert.IsNotNull(currentClientRequestId);
        await service.ResolveApprovalAsync(
            new ResolveApprovalRequest { RequestId = currentClientRequestId!, Decision = ApprovalDecision.Accept },
            CancellationToken.None);
        Assert.AreEqual("accept", (await currentRequest).GetProperty("decision").GetString());
        Assert.AreEqual(1, resolved);
    }

    [TestMethod]
    public async Task UnknownNotificationIsNotProjectedToConversationObservers()
    {
        var connection = new RecordingConnection();
        await using var service = CreateService();
        var events = new List<ConversationEvent>();
        service.ConversationEventReceived += (value, _) =>
        {
            events.Add(value);
            return Task.CompletedTask;
        };
        await service.InitializeAsync(connection, Options(), CancellationToken.None);
        events.Clear();

        await connection.EmitNotificationAsync("future/notification", new { secret = "do-not-project" });

        Assert.AreEqual(0, events.Count);
    }

    [TestMethod]
    public async Task TurnCompletedCarriesTurnIdFromWireShape()
    {
        var connection = new RecordingConnection
        {
            Handler = (method, _) => method == "turn/start"
                ? JsonSerializer.SerializeToElement(new { turn = new { id = "turn-1" } })
                : JsonSerializer.SerializeToElement(new { }),
        };
        await using var service = CreateService();
        var events = new List<ConversationEvent>();
        service.ConversationEventReceived += (value, _) =>
        {
            events.Add(value);
            return Task.CompletedTask;
        };
        await service.InitializeAsync(connection, Options(), CancellationToken.None);
        await service.StartTurnAsync(
            new StartTurnRequest { ThreadId = "thread-1", Text = "hello" },
            CancellationToken.None);

        await connection.EmitNotificationAsync(
            "turn/completed",
            new { threadId = "thread-1", turn = new { id = "turn-1", status = "completed" } });

        Assert.IsNull(service.ActiveTurnId);
        ConversationEvent completed = events.Single(item => item.Kind == ConversationEventKind.TurnCompleted);
        Assert.AreEqual("turn-1", completed.TurnId);
    }

    [TestMethod]
    public async Task TurnStartedForNewThreadBeforeStartResponseIsNotSuppressed()
    {
        var secondRequestStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondResponse = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        int starts = 0;
        var connection = new RecordingConnection
        {
            AsyncHandler = (method, _, _) =>
            {
                if (method != "turn/start")
                {
                    return Task.FromResult(JsonSerializer.SerializeToElement(new { }));
                }

                if (Interlocked.Increment(ref starts) == 1)
                {
                    return Task.FromResult(JsonSerializer.SerializeToElement(new { turn = new { id = "turn-a" } }));
                }

                secondRequestStarted.TrySetResult();
                return secondResponse.Task;
            },
        };
        await using var service = CreateService();
        var events = new List<ConversationEvent>();
        service.ConversationEventReceived += (value, _) =>
        {
            events.Add(value);
            return Task.CompletedTask;
        };
        await service.InitializeAsync(connection, Options(), CancellationToken.None);
        await service.StartTurnAsync(new StartTurnRequest { ThreadId = "thread-a", Text = "first" }, CancellationToken.None);
        await connection.EmitNotificationAsync(
            "turn/completed",
            new { threadId = "thread-a", turn = new { id = "turn-a", status = "completed" } });

        Task<string> second = service.StartTurnAsync(
            new StartTurnRequest { ThreadId = "thread-b", Text = "second" },
            CancellationToken.None);
        await secondRequestStarted.Task;
        await connection.EmitNotificationAsync(
            "turn/started",
            new { threadId = "thread-b", turn = new { id = "turn-b" } });

        Assert.AreEqual("turn-b", service.ActiveTurnId);
        Assert.IsTrue(events.Any(item => item.Kind == ConversationEventKind.TurnStarted && item.TurnId == "turn-b"));

        secondResponse.TrySetResult(JsonSerializer.SerializeToElement(new { turn = new { id = "turn-b" } }));
        Assert.AreEqual("turn-b", await second);
        Assert.AreEqual("thread-b", service.ActiveThreadId);
        Assert.AreEqual("turn-b", service.ActiveTurnId);
    }

    [TestMethod]
    public async Task NewThreadCompletionBeforeStartResponseIsNotRevived()
    {
        var secondRequestStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondResponse = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        int starts = 0;
        var connection = new RecordingConnection
        {
            AsyncHandler = (method, _, _) =>
            {
                if (method != "turn/start")
                {
                    return Task.FromResult(JsonSerializer.SerializeToElement(new { }));
                }

                if (Interlocked.Increment(ref starts) == 1)
                {
                    return Task.FromResult(JsonSerializer.SerializeToElement(new { turn = new { id = "turn-a" } }));
                }

                secondRequestStarted.TrySetResult();
                return secondResponse.Task;
            },
        };
        await using var service = CreateService();
        var events = new List<ConversationEvent>();
        service.ConversationEventReceived += (value, _) =>
        {
            events.Add(value);
            return Task.CompletedTask;
        };
        await service.InitializeAsync(connection, Options(), CancellationToken.None);
        await service.StartTurnAsync(new StartTurnRequest { ThreadId = "thread-a", Text = "first" }, CancellationToken.None);
        await connection.EmitNotificationAsync(
            "turn/completed",
            new { threadId = "thread-a", turn = new { id = "turn-a", status = "completed" } });

        Task<string> second = service.StartTurnAsync(
            new StartTurnRequest { ThreadId = "thread-b", Text = "second" },
            CancellationToken.None);
        await secondRequestStarted.Task;
        await connection.EmitNotificationAsync(
            "turn/completed",
            new { threadId = "thread-b", turn = new { id = "turn-b", status = "completed" } });
        secondResponse.TrySetResult(JsonSerializer.SerializeToElement(new { turn = new { id = "turn-b" } }));

        Assert.AreEqual("turn-b", await second);
        Assert.IsNull(service.ActiveTurnId);
        Assert.IsTrue(events.Any(item => item.Kind == ConversationEventKind.TurnCompleted && item.TurnId == "turn-b"));
    }

    [TestMethod]
    public async Task NumericServerRequestResolvedReleasesPendingApproval()
    {
        var connection = new RecordingConnection();
        var requested = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var service = CreateService();
        int resolved = 0;
        service.ApprovalRequested += (_, _) =>
        {
            requested.TrySetResult();
            return Task.CompletedTask;
        };
        service.ApprovalResolved += (_, _) =>
        {
            resolved++;
            return Task.CompletedTask;
        };
        await service.InitializeAsync(connection, Options(), CancellationToken.None);

        Task<JsonElement> pending = connection.EmitRequestAsync(
            7L,
            "item/commandExecution/requestApproval",
            new { command = "dotnet build", threadId = "thread-1", turnId = "turn-1", itemId = "item-1", startedAtMs = 1L });
        await requested.Task;
        await connection.EmitNotificationAsync("serverRequest/resolved", new { threadId = "thread-1", requestId = 7L });

        await Assert.ThrowsExactlyAsync<JsonRpcRequestResolvedException>(() => pending.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.AreEqual(1, resolved);
    }

    [TestMethod]
    public async Task UnmappableAttachmentIsRejectedBeforeTurnStart()
    {
        string root = Path.Combine(Path.GetTempPath(), "codex-map-" + Guid.NewGuid().ToString("N"));
        string workspace = Path.Combine(root, "workspace");
        Directory.CreateDirectory(workspace);
        string inside = Path.Combine(workspace, "inside.txt");
        string outside = Path.Combine(root, "outside.txt");
        File.WriteAllText(inside, "inside");
        File.WriteAllText(outside, "outside");
        try
        {
            var connection = new RecordingConnection
            {
                Handler = (method, _) => method == "turn/start"
                    ? JsonSerializer.SerializeToElement(new { turn = new { id = "turn-1" } })
                    : JsonSerializer.SerializeToElement(new { }),
            };
            await using var service = CreateService();
            WorkerOptions options = Options(workspace);
            options.LocalRoot = workspace;
            options.ServerRoot = "/srv/workspace";
            await service.InitializeAsync(connection, options, CancellationToken.None);

            AttachmentRejectedException rejected = await Assert.ThrowsExactlyAsync<AttachmentRejectedException>(() =>
                service.StartTurnAsync(
                    new StartTurnRequest
                    {
                        ThreadId = "thread-1",
                        Text = "hello",
                        Attachments = [new AttachmentInfo(outside, "mention")],
                    },
                    CancellationToken.None));
            StringAssert.Contains(rejected.Message, "outside.txt");
            Assert.IsFalse(connection.Requests.Any(item => item.Method == "turn/start"));

            await service.StartTurnAsync(
                new StartTurnRequest
                {
                    ThreadId = "thread-1",
                    Text = "hello",
                    Attachments = [new AttachmentInfo(inside, "mention")],
                },
                CancellationToken.None);
            string json = ParametersFor(connection, "turn/start").GetRawText();
            StringAssert.Contains(json, "/srv/workspace/inside.txt");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task RemoteModeMapsWorkingDirectoryImagesAndIdeContextThroughTheSharedMapper()
    {
        string root = Path.Combine(Path.GetTempPath(), "codex-map-" + Guid.NewGuid().ToString("N"));
        string workspace = Path.Combine(root, "workspace");
        string source = Path.Combine(workspace, "src");
        Directory.CreateDirectory(source);
        string image = Path.Combine(workspace, "shot.png");
        string document = Path.Combine(source, "Program.cs");
        string referenced = Path.Combine(source, "Helper.cs");
        string outside = Path.Combine(root, "outside.cs");
        File.WriteAllText(image, "png");
        File.WriteAllText(document, "class Program {}");
        File.WriteAllText(referenced, "class Helper {}");
        File.WriteAllText(outside, "class Outside {}");
        try
        {
            var connection = new RecordingConnection
            {
                Handler = (method, _) => method switch
                {
                    "thread/start" => JsonSerializer.SerializeToElement(new { thread = new { id = "thread-1", cwd = "/srv/workspace" } }),
                    "turn/start" => JsonSerializer.SerializeToElement(new { turn = new { id = "turn-1" } }),
                    _ => JsonSerializer.SerializeToElement(new { }),
                },
            };
            await using var service = CreateService();
            WorkerOptions options = Options(workspace);
            options.LocalRoot = workspace;
            options.ServerRoot = "/srv/workspace";
            await service.InitializeAsync(connection, options, CancellationToken.None);

            ThreadSummary thread = await service.StartThreadAsync(CancellationToken.None);
            await service.StartTurnAsync(
                new StartTurnRequest
                {
                    ThreadId = "thread-1",
                    Text = "inspect",
                    Attachments = [new AttachmentInfo(image, "image")],
                    IdeContext = new IdeContextInfo
                    {
                        ActiveDocumentPath = document,
                        ReferencedFilePaths = [referenced, outside],
                    },
                },
                CancellationToken.None);

            Assert.AreEqual("/srv/workspace", ParametersFor(connection, "thread/start").GetProperty("cwd").GetString());
            Assert.AreEqual(
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(workspace)),
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(thread.Cwd!)));
            JsonElement[] input = ParametersFor(connection, "turn/start").GetProperty("input").EnumerateArray().ToArray();
            JsonElement localImage = input.Single(item => item.GetProperty("type").GetString() == "localImage");
            Assert.AreEqual("/srv/workspace/shot.png", localImage.GetProperty("path").GetString());
            string json = ParametersFor(connection, "turn/start").GetRawText();
            StringAssert.Contains(json, "/srv/workspace/src/Program.cs");
            StringAssert.Contains(json, "/srv/workspace/src/Helper.cs");
            Assert.IsFalse(json.Contains("outside.cs", StringComparison.Ordinal));
            Assert.IsFalse(json.Contains(Path.GetFileName(root), StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task RemoteModeThreadListShowsMappedWorkingDirectoryOrAFixedLabel()
    {
        string workspace = Path.Combine(Path.GetTempPath(), "codex-map-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(workspace, "sub"));
        try
        {
            var connection = new RecordingConnection
            {
                Handler = (method, _) => method == "thread/list"
                    ? JsonSerializer.SerializeToElement(new
                    {
                        data = new[]
                        {
                            new { id = "thread-1", cwd = "/srv/workspace/sub" },
                            new { id = "thread-2", cwd = "/srv/other/secret" },
                        },
                    })
                    : JsonSerializer.SerializeToElement(new { }),
            };
            await using var service = CreateService();
            WorkerOptions options = Options(workspace);
            options.LocalRoot = workspace;
            options.ServerRoot = "/srv/workspace";
            await service.InitializeAsync(connection, options, CancellationToken.None);

            ThreadPage page = await service.ListThreadsAsync(null, CancellationToken.None);

            Assert.AreEqual(
                Path.Combine(Path.GetFullPath(workspace), "sub"),
                Path.GetFullPath(page.Threads[0].Cwd!));
            Assert.AreEqual(CodexSessionService.RemoteWorkingDirectoryLabel, page.Threads[1].Cwd);
        }
        finally
        {
            Directory.Delete(workspace, recursive: true);
        }
    }

    [TestMethod]
    public async Task RemoteModeMapsPermissionProfileWorkingDirectory()
    {
        string workspace = Path.Combine(Path.GetTempPath(), "codex-map-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workspace);
        try
        {
            var connection = new RecordingConnection();
            await using var service = CreateService();
            WorkerOptions options = Options(workspace, experimentalApi: true);
            options.LocalRoot = workspace;
            options.ServerRoot = "/srv/workspace";
            await service.InitializeAsync(connection, options, CancellationToken.None);

            await service.ListPermissionProfilesAsync(CancellationToken.None);

            Assert.AreEqual(
                "/srv/workspace",
                ParametersFor(connection, "permissionProfile/list").GetProperty("cwd").GetString());
        }
        finally
        {
            Directory.Delete(workspace, recursive: true);
        }
    }

    [TestMethod]
    public async Task RemoteModeBlocksApprovalForAnUnmappableServerPath()
    {
        string workspace = Path.Combine(Path.GetTempPath(), "codex-map-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workspace);
        try
        {
            var connection = new RecordingConnection();
            await using var service = CreateService();
            var requests = new List<ApprovalRequest>();
            service.ApprovalRequested += (request, cancellationToken) =>
            {
                requests.Add(request);
                return service.ResolveApprovalAsync(
                    new ResolveApprovalRequest { RequestId = request.RequestId, Decision = ApprovalDecision.Cancel },
                    cancellationToken);
            };
            WorkerOptions options = Options(workspace);
            options.LocalRoot = workspace;
            options.ServerRoot = "/srv/workspace";
            await service.InitializeAsync(connection, options, CancellationToken.None);

            JsonElement blocked = await connection.EmitRequestAsync(
                "approval-1",
                "item/commandExecution/requestApproval",
                new { command = "ls", cwd = "/srv/other", itemId = "item-1", threadId = "thread-1", turnId = "turn-1", startedAtMs = 1L });
            await connection.EmitRequestAsync(
                "approval-2",
                "item/commandExecution/requestApproval",
                new { command = "ls", cwd = "/srv/workspace", itemId = "item-2", threadId = "thread-1", turnId = "turn-1", startedAtMs = 2L });

            // The unmappable request is declined without reaching the UI; the mapped one is shown
            // with its local working directory.
            Assert.AreEqual("decline", blocked.GetProperty("decision").GetString());
            ApprovalRequest shown = requests.Single();
            Assert.AreNotEqual("remote-path-unmappable", shown.RiskKey);
            Assert.IsFalse(shown.IsPolicyBlocked);
        }
        finally
        {
            Directory.Delete(workspace, recursive: true);
        }
    }

    private sealed class RecordingConnection : IJsonRpcConnection
    {
        public event Func<JsonRpcMessage, CancellationToken, Task>? NotificationReceived;

        public event Func<JsonRpcMessage, CancellationToken, Task<JsonElement>>? RequestReceived;

        public event EventHandler<Exception?>? Closed;

        public Func<string, object?, JsonElement> Handler { get; set; } = (_, _) => JsonSerializer.SerializeToElement(new { });

        public JsonElement GatewayOAuthReadResponse { get; set; } = JsonSerializer.SerializeToElement(new
        {
            providerId = "test-provider",
            providerName = "Test provider",
            required = false,
            status = (string?)null,
        });

        public Func<string, object?, CancellationToken, Task<JsonElement>>? AsyncHandler { get; set; }

        public List<RecordedRequest> Requests { get; } = new();

        public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<JsonElement> SendRequestAsync(string method, object? parameters, TimeSpan timeout, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Add(new RecordedRequest(method, parameters, timeout));
            if (method == "account/gatewayOAuth/read")
            {
                return Task.FromResult(GatewayOAuthReadResponse.Clone());
            }

            return AsyncHandler?.Invoke(method, parameters, cancellationToken)
                ?? Task.FromResult(Handler(method, parameters));
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

        public Task<JsonElement> EmitRequestAsync(long id, string method, object parameters)
            => RequestReceived?.Invoke(
                new JsonRpcMessage
                {
                    Id = JsonSerializer.SerializeToElement(id),
                    Method = method,
                    Params = JsonSerializer.SerializeToElement(parameters),
                },
                CancellationToken.None)
                ?? Task.FromResult(JsonSerializer.SerializeToElement(new { }));

        public void EmitClosed(Exception? exception = null) => Closed?.Invoke(this, exception);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed record RecordedRequest(string Method, object? Parameters, TimeSpan Timeout);

    private sealed class NullSkillCatalogStore : ISkillCatalogStore
    {
        public int ReadCount { get; private set; }
        public int WriteCount { get; private set; }
        public int DeleteCount { get; private set; }

        public ValueTask<ListSkillsResult?> TryReadAsync(
            string workspace,
            string statePartitionKey,
            string? codexVersion,
            DateTimeOffset now,
            CancellationToken cancellationToken)
        {
            ReadCount++;
            return ValueTask.FromResult<ListSkillsResult?>(null);
        }

        public ValueTask WriteAsync(
            string workspace,
            string statePartitionKey,
            string? codexVersion,
            ListSkillsResult result,
            DateTimeOffset now,
            CancellationToken cancellationToken)
        {
            WriteCount++;
            return ValueTask.CompletedTask;
        }

        public ValueTask DeleteAsync(string workspace, string statePartitionKey, CancellationToken cancellationToken)
        {
            DeleteCount++;
            return ValueTask.CompletedTask;
        }
    }
}
