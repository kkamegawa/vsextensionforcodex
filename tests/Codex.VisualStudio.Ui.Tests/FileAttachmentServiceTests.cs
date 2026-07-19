using System.IO;
using Codex.VisualStudio.Extension;

namespace Codex.VisualStudio.Ui.Tests;

[TestClass]
public sealed class FileAttachmentServiceTests
{
    [TestMethod]
    public async Task FilePickerReturnsEmptyWhenVisualStudioIsUnavailable()
    {
        var service = new FilePickerService(null);

        IReadOnlyList<string> result = await service.PickFilesAsync(null, CancellationToken.None);

        Assert.AreEqual(0, result.Count);
    }

    [TestMethod]
    public async Task WorkspaceSearchUsesDiskFallbackExcludesGeneratedDirectoriesAndRanksMatches()
    {
        string workspace = CreateTemporaryDirectory();
        try
        {
            string sourceDirectory = Directory.CreateDirectory(Path.Combine(workspace, "src")).FullName;
            string nestedDirectory = Directory.CreateDirectory(Path.Combine(sourceDirectory, "Feature")).FullName;
            string excludedDirectory = Directory.CreateDirectory(Path.Combine(workspace, "obj")).FullName;
            string exact = Path.Combine(sourceDirectory, "ChatViewModel.cs");
            string prefix = Path.Combine(nestedDirectory, "ChatViewModelTests.cs");
            string contains = Path.Combine(sourceDirectory, "MyChatViewModelHelper.cs");
            await File.WriteAllTextAsync(exact, "exact");
            await File.WriteAllTextAsync(prefix, "prefix");
            await File.WriteAllTextAsync(contains, "contains");
            await File.WriteAllTextAsync(Path.Combine(excludedDirectory, "ChatViewModel.cs"), "excluded");
            var service = new WorkspaceFileSearchService(null);

            IReadOnlyList<WorkspaceFileSearchResult> result = await service.SearchAsync(
                workspace,
                "ChatViewModel.cs",
                CancellationToken.None);

            Assert.AreEqual(1, result.Count);
            Assert.AreEqual(exact, result[0].Path);
            Assert.IsFalse(result.Any(item => item.Path.StartsWith(excludedDirectory, StringComparison.OrdinalIgnoreCase)));
        }
        finally
        {
            Directory.Delete(workspace, recursive: true);
        }
    }

    [TestMethod]
    public async Task WorkspaceSearchReturnsAtMostTwentyResultsAndCachesTheIndexForThirtySeconds()
    {
        string workspace = CreateTemporaryDirectory();
        try
        {
            for (int index = 0; index < 25; index++)
            {
                await File.WriteAllTextAsync(Path.Combine(workspace, $"match-{index:D2}.txt"), "content");
            }

            var service = new WorkspaceFileSearchService(null);
            IReadOnlyList<WorkspaceFileSearchResult> first = await service.SearchAsync(
                workspace,
                "match",
                CancellationToken.None);
            string addedAfterIndex = Path.Combine(workspace, "match-new.txt");
            await File.WriteAllTextAsync(addedAfterIndex, "new");
            IReadOnlyList<WorkspaceFileSearchResult> cached = await service.SearchAsync(
                workspace,
                "match-new",
                CancellationToken.None);

            Assert.AreEqual(20, first.Count);
            Assert.AreEqual(0, cached.Count);
        }
        finally
        {
            Directory.Delete(workspace, recursive: true);
        }
    }

    [TestMethod]
    public void FileSuggestionRefreshCleanupClearsAndDisposesCurrentSource()
    {
        var completed = new CancellationTokenSource();
        CancellationTokenSource? current = completed;

        ChatViewModel.CompleteFileSuggestionRefresh(ref current, completed);

        Assert.IsNull(current);
        Assert.ThrowsExactly<ObjectDisposedException>(() => _ = completed.Token);
    }

    [TestMethod]
    public void FileSuggestionRefreshCleanupPreservesNewerSource()
    {
        using var completed = new CancellationTokenSource();
        using var newer = new CancellationTokenSource();
        CancellationTokenSource? current = newer;

        ChatViewModel.CompleteFileSuggestionRefresh(ref current, completed);

        Assert.AreSame(newer, current);
        _ = completed.Token;
    }

    private static string CreateTemporaryDirectory()
    {
        string path = Path.Combine(Path.GetTempPath(), $"codex-file-search-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }
}
