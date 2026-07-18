using System.Text;
using System.IO;
using Codex.VisualStudio.Contracts;
using Codex.VisualStudio.Extension;

namespace Codex.VisualStudio.Ui.Tests;

[TestClass]
public sealed class IdeContextCaptureServiceTests
{
    [TestMethod]
    public void Build_IncludesOnlyWorkspacePaths()
    {
        string root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "workspace"));
        string active = Path.Combine(root, "src", "Program.cs");
        string outside = Path.GetFullPath(Path.Combine(root, "..", "outside.cs"));

        IdeContextInfo? context = IdeContextCaptureService.Build(root, active, outside, "selected");

        Assert.IsNotNull(context);
        Assert.AreEqual(active, context!.ActiveDocumentPath);
        Assert.AreEqual(0, context.ReferencedFilePaths.Count);
        Assert.AreEqual(active, context.SelectionFilePath);
        Assert.AreEqual("selected", context.SelectionText);
    }

    [TestMethod]
    public void Build_AddsSelectedWorkspaceFileAsReference()
    {
        string root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "workspace"));
        string active = Path.Combine(root, "src", "Program.cs");
        string selected = Path.Combine(root, "README.md");

        IdeContextInfo? context = IdeContextCaptureService.Build(root, active, selected, null);

        Assert.IsNotNull(context);
        CollectionAssert.AreEqual(new[] { selected }, context!.ReferencedFilePaths.ToArray());
    }

    [TestMethod]
    public void TruncateUtf8_DoesNotSplitMultibyteCharacters()
    {
        string input = string.Concat(Enumerable.Repeat("あ", IdeContextCaptureService.MaximumSelectionBytes));

        string result = IdeContextCaptureService.TruncateUtf8(input, IdeContextCaptureService.MaximumSelectionBytes);

        Assert.IsTrue(Encoding.UTF8.GetByteCount(result) <= IdeContextCaptureService.MaximumSelectionBytes);
        Assert.IsTrue(result.All(static character => character == 'あ'));
    }
}
