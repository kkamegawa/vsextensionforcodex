using System.Text.Json;
using Codex.AppServer.Protocol;
using Codex.VisualStudio.Contracts;
using Codex.VisualStudio.Worker;

namespace Codex.VisualStudio.Core.Tests;

// Codex CLI 0.164.0 tier behavior: presets, diagnostic ZIP saving, MCP runtime details,
// generated-image references, and authoritative resume settings.
public sealed partial class CodexSessionServiceTests
{
    private static readonly string[] AllPresetSlots = ["1", "2", "3", "4", "5", "6", "7", "8", "9", "0"];

    [TestMethod]
    [DataRow("0.164.0-alpha.3", "0.164.0", -1)]
    [DataRow("0.164.0-alpha.3", "0.164.0-alpha.10", -1)]
    [DataRow("0.164.0-alpha", "0.164.0-alpha.1", -1)]
    [DataRow("0.164.0-1", "0.164.0-alpha", -1)]
    [DataRow("0.164.0+build.1", "0.164.0+build.2", 0)]
    [DataRow("0.165.0", "0.164.9", 1)]
    public void CodexCliVersionUsesSemanticVersioningPrecedence(string left, string right, int expected)
    {
        int actual = CodexCliVersion.Parse(left).CompareTo(CodexCliVersion.Parse(right));

        Assert.AreEqual(expected, Math.Sign(actual));
    }

    [TestMethod]
    public async Task ListPresetsReadsLayersAndAppliesEditAndDeleteRules()
    {
        object? readParameters = null;
        var connection = Tier164Connection((method, parameters) =>
        {
            if (method != "config/read")
            {
                return null;
            }

            readParameters = parameters;
            return JsonSerializer.SerializeToElement(new
            {
                config = new { },
                origins = new { },
                layers = new object[]
                {
                    Layer("sessionFlags", null, new { tui = new { presets = new Dictionary<string, object> { ["3"] = new { input = "/review", label = "" } } } }),
                    Layer("project", null, new { tui = new { presets = new Dictionary<string, object> { ["2"] = new { input = "project prompt", label = "Project" } } } }, disabledReason: "untrusted"),
                    Layer("user", "user-v1", new
                    {
                        tui = new
                        {
                            presets = new Dictionary<string, object>
                            {
                                ["1"] = new { input = "line one\n  line two ", label = "  Mine " },
                                ["3"] = new { input = "/status", label = "Shadowed" },
                                ["4"] = new { input = "!rm -rf", label = "Invalid" },
                            },
                        },
                    }),
                    Layer("system", null, new { tui = new { presets = new Dictionary<string, object> { ["5"] = new { input = "system prompt" } } } }),
                },
            });
        });
        await using var service = CreateService();
        await service.InitializeAsync(connection, Options(), CancellationToken.None);

        ListPresetsResult result = await service.ListPresetsAsync(CancellationToken.None);

        Assert.IsTrue(result.IsSupported);
        Assert.AreEqual("user-v1", result.UserLayerVersion);
        CollectionAssert.AreEqual(AllPresetSlots, result.Slots.Select(slot => slot.Slot).ToArray());
        using JsonDocument parameters = JsonDocument.Parse(JsonSerializer.Serialize(readParameters));
        Assert.IsTrue(parameters.RootElement.GetProperty("includeLayers").GetBoolean());

        PresetSlotInfo mine = result.Slots.Single(slot => slot.Slot == "1");
        Assert.AreEqual("line one\n  line two ", mine.Input, "Preset input keeps newlines and spaces.");
        Assert.AreEqual("Mine", mine.Label);
        Assert.AreEqual(PresetLayerKind.User, mine.SourceLayer);
        Assert.IsTrue(mine.CanEdit);
        Assert.IsTrue(mine.CanDelete);

        PresetSlotInfo disabledProject = result.Slots.Single(slot => slot.Slot == "2");
        Assert.AreEqual(PresetLayerKind.None, disabledProject.SourceLayer, "A disabled layer does not define slots.");
        Assert.IsTrue(disabledProject.CanEdit);
        Assert.IsFalse(disabledProject.CanDelete);

        PresetSlotInfo shadowed = result.Slots.Single(slot => slot.Slot == "3");
        Assert.AreEqual(PresetLayerKind.SessionFlags, shadowed.SourceLayer);
        Assert.AreEqual("/review", shadowed.Input);
        Assert.IsFalse(shadowed.CanEdit);
        Assert.IsFalse(shadowed.CanDelete);

        Assert.IsNull(result.Slots.Single(slot => slot.Slot == "4").Input, "An input the CLI would reject is not offered.");

        PresetSlotInfo system = result.Slots.Single(slot => slot.Slot == "5");
        Assert.AreEqual(PresetLayerKind.System, system.SourceLayer);
        Assert.IsFalse(system.CanEdit);
        Assert.IsFalse(system.CanDelete);
    }

    [TestMethod]
    public async Task WritePresetSendsOneReplaceEditWithTheObservedVersion()
    {
        var writes = new List<object?>();
        var connection = Tier164Connection((method, parameters) =>
        {
            if (method == "config/batchWrite")
            {
                writes.Add(parameters);
                return JsonSerializer.SerializeToElement(new { status = "ok", version = "user-v2", filePath = "C:/codex/config.toml" });
            }

            return method == "config/read" ? UserOnlyPresetLayers("user-v1") : null;
        });
        await using var service = CreateService();
        await service.InitializeAsync(connection, Options(), CancellationToken.None);

        WritePresetResult saved = await service.WritePresetAsync(
            new WritePresetRequest { Slot = "7", Input = "Explain this file", Label = null, ExpectedVersion = "user-v1" },
            CancellationToken.None);
        WritePresetResult deleted = await service.WritePresetAsync(
            new WritePresetRequest { Slot = "1", Input = null, ExpectedVersion = "user-v1" },
            CancellationToken.None);

        Assert.AreEqual(WritePresetOutcome.Written, saved.Outcome);
        Assert.AreEqual(WritePresetOutcome.Written, deleted.Outcome);
        Assert.AreEqual(2, writes.Count);
        // Serialize as the JSON-RPC connections do, which drop null properties of ordinary objects.
        var wireOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        };
        using JsonDocument save = JsonDocument.Parse(JsonSerializer.Serialize(writes[0], wireOptions));
        JsonElement edit = save.RootElement.GetProperty("edits").EnumerateArray().Single();
        Assert.AreEqual("tui.presets.7", edit.GetProperty("keyPath").GetString());
        Assert.AreEqual("replace", edit.GetProperty("mergeStrategy").GetString());
        Assert.AreEqual("Explain this file", edit.GetProperty("value").GetProperty("input").GetString());
        Assert.AreEqual(string.Empty, edit.GetProperty("value").GetProperty("label").GetString(), "A missing label is written as an explicit empty string.");
        Assert.AreEqual("user-v1", save.RootElement.GetProperty("expectedVersion").GetString());
        using JsonDocument delete = JsonDocument.Parse(JsonSerializer.Serialize(writes[1], wireOptions));
        JsonElement deleteEdit = delete.RootElement.GetProperty("edits").EnumerateArray().Single();
        Assert.AreEqual("tui.presets.1", deleteEdit.GetProperty("keyPath").GetString());
        Assert.IsTrue(deleteEdit.TryGetProperty("value", out JsonElement deleteValue), "A delete must send an explicit null value.");
        Assert.AreEqual(JsonValueKind.Null, deleteValue.ValueKind);
    }

    [TestMethod]
    [DataRow("   ", "cannot be empty")]
    [DataRow("  !dir", "not shell commands")]
    public async Task WritePresetRejectsInputTheCliRejectsWithoutWriting(string input, string message)
    {
        int writes = 0;
        var connection = Tier164Connection((method, _) =>
        {
            writes += method == "config/batchWrite" ? 1 : 0;
            return method == "config/read" ? UserOnlyPresetLayers("user-v1") : null;
        });
        await using var service = CreateService();
        await service.InitializeAsync(connection, Options(), CancellationToken.None);

        WritePresetResult result = await service.WritePresetAsync(
            new WritePresetRequest { Slot = "2", Input = input, ExpectedVersion = "user-v1" },
            CancellationToken.None);

        Assert.AreEqual(WritePresetOutcome.Rejected, result.Outcome);
        StringAssert.Contains(result.Message, message);
        Assert.AreEqual(0, writes);
    }

    [TestMethod]
    public void PresetRulesBoundInputAtEightThousandUtf8Bytes()
    {
        Assert.IsNull(PresetRules.ValidateInput(new string('a', PresetRules.MaximumInputBytes)));
        StringAssert.Contains(PresetRules.ValidateInput(new string('a', PresetRules.MaximumInputBytes + 1)), "8,000");
        StringAssert.Contains(PresetRules.ValidateInput(new string('あ', 2667)), "8,000", "Multi-byte text is measured in UTF-8 bytes.");
    }

    [TestMethod]
    public async Task WritePresetReportsConflictsWithoutRetrying()
    {
        int writes = 0;
        var connection = Tier164Connection((method, _) =>
        {
            if (method == "config/batchWrite")
            {
                writes++;
                throw new JsonRpcRemoteException(
                    -32600,
                    "configuration version conflict",
                    JsonSerializer.SerializeToElement(new { config_write_error_code = "configVersionConflict" }));
            }

            return method == "config/read" ? UserOnlyPresetLayers("user-v1") : null;
        });
        await using var service = CreateService();
        await service.InitializeAsync(connection, Options(), CancellationToken.None);

        WritePresetResult stale = await service.WritePresetAsync(
            new WritePresetRequest { Slot = "2", Input = "hello", ExpectedVersion = "user-v0" },
            CancellationToken.None);
        WritePresetResult raced = await service.WritePresetAsync(
            new WritePresetRequest { Slot = "2", Input = "hello", ExpectedVersion = "user-v1" },
            CancellationToken.None);

        Assert.AreEqual(WritePresetOutcome.Conflict, stale.Outcome);
        Assert.AreEqual(WritePresetOutcome.Conflict, raced.Outcome);
        Assert.AreEqual(1, writes, "A stale version is rejected before writing, and a conflict is never retried.");
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task WritePresetRefusesAUserLayerWithoutAVersion(bool blankVersion)
    {
        int writes = 0;
        object userLayer = blankVersion
            ? new { name = new { type = "user" }, version = "  ", config = new { } }
            : new { name = new { type = "user" }, config = new { } };
        var connection = Tier164Connection((method, _) =>
        {
            writes += method == "config/batchWrite" ? 1 : 0;
            return method == "config/read"
                ? JsonSerializer.SerializeToElement(new { config = new { }, origins = new { }, layers = new[] { userLayer } })
                : null;
        });
        await using var service = CreateService();
        await service.InitializeAsync(connection, Options(), CancellationToken.None);

        WritePresetResult result = await service.WritePresetAsync(
            new WritePresetRequest { Slot = "2", Input = "hello", ExpectedVersion = null },
            CancellationToken.None);

        Assert.AreEqual(WritePresetOutcome.Failed, result.Outcome);
        StringAssert.Contains(result.Message, "version");
        Assert.AreEqual(0, writes, "A write without an optimistic-concurrency guard must never be sent.");
    }

    [TestMethod]
    public async Task PresetsAndDiagnosticSaveRequireThe164Tier()
    {
        var connection = new RecordingConnection
        {
            Handler = (method, _) => method == "initialize"
                ? JsonSerializer.SerializeToElement(new { userAgent = "codex-cli/0.163.0" })
                : JsonSerializer.SerializeToElement(new { }),
        };
        await using var service = CreateService();
        await service.InitializeAsync(connection, Options(), CancellationToken.None);

        ListPresetsResult presets = await service.ListPresetsAsync(CancellationToken.None);
        SaveFeedbackResult save = await service.SaveFeedbackAsync(
            new SaveFeedbackRequest { Classification = "visual-studio", SavePath = ServerZipPath() },
            CancellationToken.None);

        Assert.IsFalse(presets.IsSupported);
        Assert.IsFalse(save.IsSupported);
        StringAssert.Contains(presets.UnavailableReason, "0.164.0");
        Assert.IsFalse(connection.Requests.Any(request => request.Method is "config/read" or "feedback/save"));
    }

    [TestMethod]
    public async Task SaveFeedbackSendsTheServerPathWithoutExtraLogs()
    {
        object? saveParameters = null;
        string zip = ServerZipPath();
        var connection = Tier164Connection((method, parameters) =>
        {
            if (method != "feedback/save")
            {
                return null;
            }

            saveParameters = parameters;
            return JsonSerializer.SerializeToElement(new { savedPath = zip, threadId = "thread-1" });
        });
        await using var service = CreateService();
        await service.InitializeAsync(connection, Options(), CancellationToken.None);

        SaveFeedbackResult result = await service.SaveFeedbackAsync(
            new SaveFeedbackRequest { Classification = "visual-studio", ThreadId = "thread-1", SavePath = zip },
            CancellationToken.None);

        Assert.AreEqual(SaveFeedbackOutcome.Saved, result.Outcome);
        Assert.AreEqual(zip, result.SavedPath);
        using JsonDocument parameters = JsonDocument.Parse(JsonSerializer.Serialize(saveParameters));
        Assert.AreEqual(zip, parameters.RootElement.GetProperty("savePath").GetString());
        Assert.IsFalse(parameters.RootElement.GetProperty("includeLogs").GetBoolean());
        Assert.AreEqual(JsonValueKind.Null, parameters.RootElement.GetProperty("extraLogFiles").ValueKind);
        Assert.AreEqual("visual-studio", parameters.RootElement.GetProperty("classification").GetString());
    }

    [TestMethod]
    public async Task SaveFeedbackReportsServerErrorsAndUnconfirmedOutcomes()
    {
        bool close = false;
        var connection = Tier164Connection((method, _) =>
        {
            if (method != "feedback/save")
            {
                return null;
            }

            if (close)
            {
                throw new JsonRpcConnectionClosedException("closed");
            }

            throw new JsonRpcRemoteException(-32600, "path already exists");
        });
        await using var service = CreateService();
        await service.InitializeAsync(connection, Options(), CancellationToken.None);
        var request = new SaveFeedbackRequest { Classification = "visual-studio", SavePath = ServerZipPath() };

        SaveFeedbackResult failed = await service.SaveFeedbackAsync(request, CancellationToken.None);
        close = true;
        SaveFeedbackResult unconfirmed = await service.SaveFeedbackAsync(request, CancellationToken.None);

        Assert.AreEqual(SaveFeedbackOutcome.Failed, failed.Outcome);
        StringAssert.Contains(failed.Message, "path already exists");
        Assert.AreEqual(SaveFeedbackOutcome.Unconfirmed, unconfirmed.Outcome);
        Assert.AreEqual(2, connection.Requests.Count(item => item.Method == "feedback/save"), "Each save is sent once.");
        await Assert.ThrowsExactlyAsync<ArgumentException>(() => service.SaveFeedbackAsync(
            new SaveFeedbackRequest { Classification = "visual-studio", SavePath = "relative.zip" },
            CancellationToken.None));
    }

    [TestMethod]
    [DataRow("0.164.0", true)]
    [DataRow("0.163.0", false)]
    public async Task McpRuntimeDetailsAreReadOnlyInThe164Tier(string version, bool expectDetails)
    {
        var connection = new RecordingConnection
        {
            Handler = (method, _) => method switch
            {
                "initialize" => JsonSerializer.SerializeToElement(new { userAgent = $"codex-cli/{version}" }),
                "mcpServerStatus/list" => JsonSerializer.SerializeToElement(new
                {
                    data = new object[]
                    {
                        new
                        {
                            name = "docs",
                            authStatus = "oAuth",
                            runtimeStatus = "authenticationRequired",
                            tools = new { },
                            resources = Array.Empty<object>(),
                            resourceTemplates = Array.Empty<object>(),
                            toolsError = "discovery failed\u001b[31m token=secret-value",
                            toolListMeta = new { opaque = "do-not-forward" },
                        },
                        new
                        {
                            name = "future",
                            authStatus = "unsupported",
                            runtimeStatus = "somethingNew",
                            tools = new { search = new { name = "search", inputSchema = new { } } },
                            resources = Array.Empty<object>(),
                            resourceTemplates = Array.Empty<object>(),
                        },
                    },
                }),
                _ => JsonSerializer.SerializeToElement(new { }),
            },
        };
        await using var service = CreateService();
        await service.InitializeAsync(connection, Options(), CancellationToken.None);

        McpServerListResult result = await service.ListMcpServersAsync(null, CancellationToken.None);

        McpServerStatusInfo docs = result.Servers.Single(server => server.Name == "docs");
        McpServerStatusInfo future = result.Servers.Single(server => server.Name == "future");
        Assert.AreEqual(expectDetails, docs.HasRuntimeDetails);
        Assert.AreEqual(1, future.ToolCount);
        Assert.IsFalse(JsonSerializer.Serialize(result).Contains("do-not-forward", StringComparison.Ordinal));
        if (expectDetails)
        {
            Assert.AreEqual(McpRuntimeStatus.AuthenticationRequired, docs.RuntimeStatus);
            Assert.AreEqual(McpRuntimeStatus.Unknown, future.RuntimeStatus);
            Assert.IsNotNull(docs.ToolsError);
            Assert.IsFalse(docs.ToolsError.Any(char.IsControl));
        }
        else
        {
            Assert.AreEqual(McpRuntimeStatus.Unknown, docs.RuntimeStatus);
            Assert.IsNull(docs.ToolsError);
        }
    }

    [TestMethod]
    public async Task GeneratedImageItemsProjectReferencesInlineDataAndFailures()
    {
        // A 1x1 PNG.
        const string rawPng = "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNkYAAAAAYAAjCB0C8AAAAASUVORK5CYII=";
        var connection = Tier164Connection((_, _) => null);
        await using var service = CreateService();
        var completed = new List<ConversationEvent>();
        service.ConversationEventReceived += (value, _) =>
        {
            if (value.Kind == ConversationEventKind.ItemCompleted)
            {
                completed.Add(value);
            }

            return Task.CompletedTask;
        };
        await service.InitializeAsync(connection, Options(), CancellationToken.None);

        object[] items =
        [
            new { id = "image-file", type = "imageGeneration", status = "completed", revisedPrompt = (string?)null, fileId = "file-abc", inputImages = new[] { new { path = "C:/secret/input.png", sha256 = new string('0', 64) } } },
            new { id = "image-inline", type = "imageGeneration", status = "completed", revisedPrompt = (string?)null, result = rawPng },
            new { id = "image-both", type = "imageGeneration", status = "completed", revisedPrompt = (string?)null, fileId = "file-def", result = string.Empty },
            new { id = "image-failed", type = "imageGeneration", status = "failed", revisedPrompt = (string?)null, result = string.Empty, failure = new { message = "blocked" } },
        ];
        foreach (object item in items)
        {
            await connection.EmitNotificationAsync("item/completed", new { threadId = "thread-1", turnId = "turn-1", item });
        }

        Assert.AreEqual(4, completed.Count);
        StringAssert.Contains(completed[0].Parts[0].Text, "reference that this connection cannot preview");
        StringAssert.Contains(completed[0].Parts[1].Text, "1 local input image");
        Assert.IsFalse(JsonSerializer.Serialize(completed[0]).Contains("secret", StringComparison.Ordinal), "Input image paths are not exposed.");
        Assert.AreEqual(ArtifactPartKind.Image, completed[1].Parts.Single().Kind);
        Assert.AreEqual("image/png", completed[1].Parts.Single().MimeType);
        StringAssert.Contains(completed[2].Parts.Single().Text, "reference", "fileId wins over an empty migration result.");
        Assert.AreEqual("Image generation failed.", completed[3].Parts.Single().Text);
    }

    [TestMethod]
    [DataRow("0.164.0", true)]
    [DataRow("0.163.0", false)]
    public async Task ResumeMarksSettingsAuthoritativeOnlyInThe164Tier(string version, bool expected)
    {
        var connection = new RecordingConnection
        {
            Handler = (method, _) => method switch
            {
                "initialize" => JsonSerializer.SerializeToElement(new { userAgent = $"codex-cli/{version}" }),
                "thread/resume" => JsonSerializer.SerializeToElement(new
                {
                    thread = new { id = "thread-1", cwd = "C:/work" },
                    model = "gpt-5-codex",
                    reasoningEffort = "high",
                    serviceTier = (string?)null,
                }),
                _ => JsonSerializer.SerializeToElement(new { }),
            },
        };
        await using var service = CreateService();
        await service.InitializeAsync(connection, Options(), CancellationToken.None);

        ThreadSummary resumed = await service.ResumeThreadAsync("thread-1", CancellationToken.None);

        Assert.AreEqual(expected, resumed.HasAuthoritativeTurnSettings);
        Assert.IsNull(resumed.EffectiveServiceTier);
    }

    private static RecordingConnection Tier164Connection(Func<string, object?, JsonElement?> handler)
        => new()
        {
            Handler = (method, parameters) => method == "initialize"
                ? JsonSerializer.SerializeToElement(new { userAgent = "codex-cli/0.164.0" })
                : handler(method, parameters) ?? JsonSerializer.SerializeToElement(new { }),
        };

    private static object Layer(string type, string? version, object config, string? disabledReason = null)
        => new { name = new { type }, version = version ?? $"{type}-version", config, disabledReason };

    private static JsonElement UserOnlyPresetLayers(string version)
        => JsonSerializer.SerializeToElement(new
        {
            config = new { },
            origins = new { },
            layers = new object[]
            {
                Layer("user", version, new { tui = new { presets = new Dictionary<string, object> { ["1"] = new { input = "old", label = "Old" } } } }),
            },
        });

    private static string ServerZipPath()
        => OperatingSystem.IsWindows() ? @"C:\diagnostics\codex-diagnostics.zip" : "/tmp/codex-diagnostics.zip";
}
