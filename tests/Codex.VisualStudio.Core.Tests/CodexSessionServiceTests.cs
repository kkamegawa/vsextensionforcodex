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
                    new AttachmentInfo(Path.Combine(workspace, "missing.txt"), "mention"),
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
    public async Task StartTurnAllowsOutsideWorkspaceAttachmentsButRejectsProtectedFilesAndCapsAtTen()
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

        attachments.Insert(0, new AttachmentInfo(protectedFile, "mention"));
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
            new StartTurnRequest { ThreadId = "thread-1", Text = "inspect", Attachments = attachments },
            CancellationToken.None);

        JsonElement[] input = ParametersFor(connection, "turn/start").GetProperty("input").EnumerateArray().ToArray();
        Assert.AreEqual(10, input.Count(item => item.GetProperty("type").GetString() == "mention"));
        Assert.IsFalse(input.Any(item => item.TryGetProperty("path", out JsonElement path)
            && string.Equals(path.GetString(), protectedFile, StringComparison.OrdinalIgnoreCase)));

        Directory.Delete(root, recursive: true);
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
        Assert.AreEqual(TimeSpan.FromSeconds(15), requests[0].Timeout);
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
                                    path = "/repo/.codex/skills/review-diff",
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
        Assert.AreEqual("/repo/.codex/skills/review-diff", result.Skills[0].Path);
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
                                new { name = "no-scope", description = "d", enabled = true, path = "/repo/.codex/skills/no-scope" },
                                // Complete entry — must survive.
                                new { name = "complete", description = "d", enabled = true, path = "/repo/.codex/skills/complete", scope = "repo" },
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
                                new { name = "rooted", description = "d", enabled = true, path = "/repo/.codex/skills/rooted", scope = "repo" },
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
                                    path = "/repo/.codex/skills/leaky",
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
        StringAssert.DoesNotMatch(result.Skills[0].Description!, new System.Text.RegularExpressions.Regex("secret-value"));
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
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Add(new RecordedRequest(method, parameters, timeout));
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

    private sealed record RecordedRequest(string Method, object? Parameters, TimeSpan Timeout);
}
