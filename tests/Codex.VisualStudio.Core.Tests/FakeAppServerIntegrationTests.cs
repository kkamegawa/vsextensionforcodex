using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using Codex.VisualStudio.Contracts;

namespace Codex.VisualStudio.Core.Tests;

[TestClass]
public sealed class FakeAppServerIntegrationTests
{
    private static readonly JsonSerializerOptions WireJsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

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
            string fakeOutput = Path.Combine(root, "src", "Codex.AppServer.Fake", "bin", configuration, "net8.0", "Codex.AppServer.Fake.dll");
            string runtimeConfig = Path.Combine(root, "src", "Codex.AppServer.Fake", "bin", configuration, "net8.0", "Codex.AppServer.Fake.runtimeconfig.json");
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
}
