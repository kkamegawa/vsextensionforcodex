using System.Text.Json;
using Codex.AppServer.Protocol;
using Codex.VisualStudio.Contracts;
using Codex.VisualStudio.Worker;

namespace Codex.VisualStudio.Core.Tests;

[TestClass]
public sealed class SkillCatalogStoreTests
{
    private string temporaryRoot = null!;

    [TestInitialize]
    public void Initialize()
    {
        temporaryRoot = Path.Combine(Path.GetTempPath(), $"skill-catalog-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(temporaryRoot);
    }

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(temporaryRoot))
        {
            Directory.Delete(temporaryRoot, recursive: true);
        }
    }

    [TestMethod]
    public async Task WriteReadPersistsOnlyDisplayCatalogFields()
    {
        string workspace = CreateWorkspace("repo-a");
        var store = CreateStore();
        DateTimeOffset now = new(2026, 8, 11, 0, 0, 0, TimeSpan.Zero);
        ListSkillsResult live = CreateResult("review-diff", workspace);
        live.Errors = [new SkillLoadError { Message = "not persisted", Path = Path.Combine(workspace, "broken") }];
        live.Skills[0].Cwd = workspace;
        live.Skills[0].DefaultPrompt = "secret prompt";
        live.Skills[0].HasIconSmall = true;
        live.Skills[0].ToolDependencies =
        [
            new SkillToolDependencyInfo { Type = "mcp", Value = "private-tool", Description = "not persisted" },
        ];

        await store.WriteAsync(workspace, "1.2.3", live, now, CancellationToken.None);
        ListSkillsResult? cached = await store.TryReadAsync(
            workspace,
            "1.2.3",
            now.AddMinutes(1),
            CancellationToken.None);

        Assert.IsNotNull(cached);
        Assert.IsTrue(cached.IsStale);
        Assert.AreEqual(1, cached.Skills.Count);
        Assert.AreEqual("review-diff", cached.Skills[0].Name);
        Assert.IsNull(cached.Skills[0].Cwd);
        Assert.IsNull(cached.Skills[0].DefaultPrompt);
        Assert.IsFalse(cached.Skills[0].HasIconSmall);
        Assert.AreEqual(0, cached.Skills[0].ToolDependencies.Count);
        Assert.AreEqual(0, cached.Errors.Count);

        string json = await File.ReadAllTextAsync(Directory.GetFiles(store.RootDirectory, "*.json").Single());
        Assert.IsFalse(json.Contains("secret prompt", StringComparison.Ordinal));
        Assert.IsFalse(json.Contains("private-tool", StringComparison.Ordinal));
        Assert.IsFalse(json.Contains("not persisted", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task ReadRejectsExpiredAndCorruptCatalogs()
    {
        string workspace = CreateWorkspace("repo-expired");
        var store = CreateStore();
        DateTimeOffset now = new(2026, 8, 11, 0, 0, 0, TimeSpan.Zero);
        await store.WriteAsync(workspace, "1.2.3", CreateResult("old", workspace), now, CancellationToken.None);

        ListSkillsResult? expired = await store.TryReadAsync(
            workspace,
            "1.2.3",
            now.Add(FileSkillCatalogStore.HardExpiry),
            CancellationToken.None);

        Assert.IsNull(expired);
        await store.WriteAsync(workspace, "1.2.3", CreateResult("corrupt", workspace), now, CancellationToken.None);
        string cachePath = Directory.GetFiles(store.RootDirectory, "*.json").Single();
        await File.WriteAllTextAsync(cachePath, "{not-json");

        ListSkillsResult? corrupt = await store.TryReadAsync(
            workspace,
            "1.2.3",
            now.AddMinutes(1),
            CancellationToken.None);

        Assert.IsNull(corrupt);
    }

    [TestMethod]
    public async Task WorkspaceHashKeepsCatalogsIsolated()
    {
        string workspaceA = CreateWorkspace("repo-a");
        string workspaceB = CreateWorkspace("repo-b");
        var store = CreateStore();
        DateTimeOffset now = new(2026, 8, 11, 0, 0, 0, TimeSpan.Zero);
        await store.WriteAsync(workspaceA, "1.2.3", CreateResult("only-a", workspaceA), now, CancellationToken.None);

        ListSkillsResult? wrongWorkspace = await store.TryReadAsync(
            workspaceB,
            "1.2.3",
            now.AddMinutes(1),
            CancellationToken.None);
        ListSkillsResult? rightWorkspace = await store.TryReadAsync(
            workspaceA,
            "1.2.3",
            now.AddMinutes(1),
            CancellationToken.None);

        Assert.IsNull(wrongWorkspace);
        Assert.AreEqual("only-a", rightWorkspace?.Skills.Single().Name);
        string fileName = Path.GetFileNameWithoutExtension(Directory.GetFiles(store.RootDirectory, "*.json").Single());
        Assert.AreEqual(64, fileName.Length);
        Assert.IsTrue(fileName.All(Uri.IsHexDigit));
        Assert.IsFalse(fileName.Contains("repo-a", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task OversizedWriteIsRejectedAndAtomicWriteLeavesNoTemporaryFile()
    {
        string workspace = CreateWorkspace("repo-size");
        var store = CreateStore();
        DateTimeOffset now = new(2026, 8, 11, 0, 0, 0, TimeSpan.Zero);
        ListSkillsResult oversized = CreateResult("oversized", workspace);
        oversized.Skills[0].Description = new string('x', checked((int)FileSkillCatalogStore.MaximumWorkspaceBytes));

        await store.WriteAsync(workspace, "1.2.3", oversized, now, CancellationToken.None);

        Assert.IsFalse(Directory.Exists(store.RootDirectory));
        await store.WriteAsync(workspace, "1.2.3", CreateResult("normal", workspace), now, CancellationToken.None);
        Assert.AreEqual(1, Directory.GetFiles(store.RootDirectory, "*.json").Length);
        Assert.AreEqual(0, Directory.GetFiles(store.RootDirectory, "*.tmp").Length);
        Assert.AreEqual(TimeSpan.FromHours(24), FileSkillCatalogStore.HardExpiry);
    }

    [TestMethod]
    public async Task ServiceReturnsStaleCatalogThenPublishesLiveGeneration()
    {
        string workspace = CreateWorkspace("repo-service");
        var stale = CreateResult("cached", workspace);
        var store = new StubSkillCatalogStore(stale);
        var connection = new RecordingSkillConnection(workspace);
        await using var service = new CodexSessionService(
            new ApprovalPolicyEngine(new PathAccessPolicy()),
            new SecretRedactor(),
            pathAccessPolicy: null,
            protectedDirectoryPolicy: null,
            timeProvider: null,
            skillCatalogStore: store);
        var refreshed = new TaskCompletionSource<SkillsChangedEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.SkillsChanged += (value, _) =>
        {
            refreshed.TrySetResult(value);
            return Task.CompletedTask;
        };
        await service.InitializeAsync(connection, Options(workspace), CancellationToken.None);

        ListSkillsResult first = await service.ListSkillsAsync(forceReload: false, CancellationToken.None);
        SkillsChangedEvent changed = await refreshed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        ListSkillsResult live = await service.ListSkillsAsync(forceReload: false, CancellationToken.None);

        Assert.IsTrue(first.IsStale);
        Assert.AreEqual("cached", first.Skills.Single().Name);
        Assert.IsFalse(live.IsStale);
        Assert.AreEqual("live", live.Skills.Single().Name);
        Assert.AreEqual(live.Generation, changed.Generation);
        Assert.AreEqual(first.Generation, live.Generation);
        Assert.AreEqual(1, connection.SkillRequests);
        Assert.IsNotNull(store.Written);
    }

    private FileSkillCatalogStore CreateStore()
        => new(new SecretRedactor(), Path.Combine(temporaryRoot, "cache"));

    private string CreateWorkspace(string name)
    {
        string workspace = Path.Combine(temporaryRoot, name);
        Directory.CreateDirectory(workspace);
        return workspace;
    }

    private static ListSkillsResult CreateResult(string name, string workspace) => new()
    {
        Skills =
        [
            new SkillInfo
            {
                Name = name,
                Description = $"Description for {name}",
                ShortDescription = "Short",
                DisplayName = "Display",
                Scope = "repo",
                Path = Path.Combine(workspace, name, "SKILL.md"),
                Enabled = true,
                BrandColor = "#123ABC",
            },
        ],
    };

    private static WorkerOptions Options(string workspace) => new()
    {
        WorkingDirectory = workspace,
        ExtensionVersion = "test",
    };

    private sealed class StubSkillCatalogStore(ListSkillsResult cached) : ISkillCatalogStore
    {
        public ListSkillsResult? Written { get; private set; }

        public ValueTask<ListSkillsResult?> TryReadAsync(
            string workspace,
            string? codexVersion,
            DateTimeOffset now,
            CancellationToken cancellationToken)
            => ValueTask.FromResult<ListSkillsResult?>(cached);

        public ValueTask WriteAsync(
            string workspace,
            string? codexVersion,
            ListSkillsResult result,
            DateTimeOffset now,
            CancellationToken cancellationToken)
        {
            Written = result;
            return ValueTask.CompletedTask;
        }

        public ValueTask DeleteAsync(string workspace, CancellationToken cancellationToken)
            => ValueTask.CompletedTask;
    }

    private sealed class RecordingSkillConnection(string workspace) : IJsonRpcConnection
    {
        public event Func<JsonRpcMessage, CancellationToken, Task>? NotificationReceived
        {
            add { }
            remove { }
        }

        public event Func<JsonRpcMessage, CancellationToken, Task<JsonElement>>? RequestReceived
        {
            add { }
            remove { }
        }

        public event EventHandler<Exception?>? Closed
        {
            add { }
            remove { }
        }

        public int SkillRequests { get; private set; }

        public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<JsonElement> SendRequestAsync(
            string method,
            object? parameters,
            TimeSpan timeout,
            CancellationToken cancellationToken)
        {
            if (method == "skills/list")
            {
                SkillRequests++;
                return Task.FromResult(JsonSerializer.SerializeToElement(new
                {
                    data = new[]
                    {
                        new
                        {
                            cwd = workspace,
                            errors = Array.Empty<object>(),
                            skills = new[]
                            {
                                new
                                {
                                    name = "live",
                                    description = "Live description",
                                    enabled = true,
                                    path = Path.Combine(workspace, "live", "SKILL.md"),
                                    scope = "repo",
                                },
                            },
                        },
                    },
                }));
            }

            return Task.FromResult(JsonSerializer.SerializeToElement(new { }));
        }

        public Task SendNotificationAsync(string method, object? parameters, CancellationToken cancellationToken)
            => Task.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
