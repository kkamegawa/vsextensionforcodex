using System.IO;
using System.Text;
using Codex.VisualStudio.Extension;

namespace Codex.VisualStudio.Ui.Tests;

[TestClass]
public sealed class AgentsFileInitializerTests
{
    [TestMethod]
    public async Task InitializeAsync_CreatesBomCrLfFileAfterPreviewConfirmation()
    {
        string directory = CreateTemporaryDirectory();
        string? preview = null;
        try
        {
            var initializer = new AgentsFileInitializer((value, _) =>
            {
                preview = value;
                return Task.FromResult(true);
            });

            AgentsFileInitializationResult result = await initializer.InitializeAsync(directory, CancellationToken.None);

            Assert.AreEqual(AgentsFileInitializationStatus.Created, result.Status);
            Assert.IsNotNull(preview);
            StringAssert.Contains(preview, AgentsFileInitializer.Template);
            byte[] bytes = await File.ReadAllBytesAsync(Path.Combine(directory, "AGENTS.md"));
            byte[] preamble = new UTF8Encoding(encoderShouldEmitUTF8Identifier: true).GetPreamble();
            CollectionAssert.AreEqual(preamble, bytes.Take(preamble.Length).ToArray());
            string text = Encoding.UTF8.GetString(bytes[preamble.Length..]);
            Assert.AreEqual(AgentsFileInitializer.Template, text);
            Assert.IsFalse(text.Replace("\r\n", string.Empty, StringComparison.Ordinal).Contains('\n'));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [TestMethod]
    public async Task InitializeAsync_DoesNotOverwriteExistingFile()
    {
        string directory = CreateTemporaryDirectory();
        string path = Path.Combine(directory, "AGENTS.md");
        await File.WriteAllTextAsync(path, "keep");
        try
        {
            var initializer = new AgentsFileInitializer((_, _) => Task.FromResult(true));

            AgentsFileInitializationResult result = await initializer.InitializeAsync(directory, CancellationToken.None);

            Assert.AreEqual(AgentsFileInitializationStatus.AlreadyExists, result.Status);
            Assert.AreEqual("keep", await File.ReadAllTextAsync(path));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [TestMethod]
    public async Task InitializeAsync_CancelLeavesWorkspaceUnchanged()
    {
        string directory = CreateTemporaryDirectory();
        try
        {
            var initializer = new AgentsFileInitializer((_, _) => Task.FromResult(false));

            AgentsFileInitializationResult result = await initializer.InitializeAsync(directory, CancellationToken.None);

            Assert.AreEqual(AgentsFileInitializationStatus.Canceled, result.Status);
            Assert.IsFalse(File.Exists(Path.Combine(directory, "AGENTS.md")));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static string CreateTemporaryDirectory()
    {
        string path = Path.Combine(Path.GetTempPath(), "CodexVsTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
