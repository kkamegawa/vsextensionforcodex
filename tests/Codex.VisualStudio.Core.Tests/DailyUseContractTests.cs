using Codex.VisualStudio.Contracts;

using Codex.VisualStudio.Worker;
using System.Text.Json;

namespace Codex.VisualStudio.Core.Tests;

[TestClass]
public sealed class DailyUseContractTests
{
    [TestMethod]
    public void WorkerOptionsSerializesCurrentContractVersion()
    {
        var options = new WorkerOptions();
        using JsonDocument payload = JsonDocument.Parse(JsonSerializer.Serialize(options));

        Assert.AreEqual(ContractVersions.Current, payload.RootElement.GetProperty(nameof(WorkerOptions.ContractVersion)).GetInt32());
    }

    [TestMethod]
    public void SavedAttachmentIdentityUsesServerPathCaseRules()
    {
        string windowsPath = SavedAttachmentPayloadRegistry.CreateIdentityKey(ServerPath.Create(@"C:\Workspace\File.txt"), serverIsWindows: true);
        string windowsPathWithDifferentCasingAndSeparators = SavedAttachmentPayloadRegistry.CreateIdentityKey(ServerPath.Create("c:/workspace/file.txt"), serverIsWindows: true);
        string posixPath = SavedAttachmentPayloadRegistry.CreateIdentityKey(ServerPath.Create("/Workspace/File.txt"), serverIsWindows: false);
        string posixPathWithDifferentCasing = SavedAttachmentPayloadRegistry.CreateIdentityKey(ServerPath.Create("/workspace/file.txt"), serverIsWindows: false);

        Assert.AreEqual(windowsPath, windowsPathWithDifferentCasingAndSeparators);
        Assert.AreNotEqual(posixPath, posixPathWithDifferentCasing);
        string posixBackslash = SavedAttachmentPayloadRegistry.CreateIdentityKey(ServerPath.Create("/workspace/a\\b"), serverIsWindows: false);
        string posixSlash = SavedAttachmentPayloadRegistry.CreateIdentityKey(ServerPath.Create("/workspace/a/b"), serverIsWindows: false);
        Assert.AreNotEqual(posixBackslash, posixSlash);
        Assert.IsTrue(windowsPath.StartsWith(SavedAttachmentPayloadRegistry.IdentityKeyPrefix, StringComparison.Ordinal));
    }

    [TestMethod]
    public void SavedAttachmentRegistryRejectsWrongServerFamilyAndUnknownPayloads()
    {
        Assert.Throws<ArgumentException>(() =>
            SavedAttachmentPayloadRegistry.CreateIdentityKey(ServerPath.Create(@"C:\Workspace\File.txt"), serverIsWindows: false));
        Assert.IsTrue(SavedAttachmentPayloadRegistry.IsKnownMimeType("image/png"));
        Assert.IsFalse(SavedAttachmentPayloadRegistry.IsKnownMimeType("image/svg+xml"));
        Assert.IsTrue(SavedAttachmentPayloadRegistry.IsSupportedFilePayloadVersion(1));
        Assert.IsFalse(SavedAttachmentPayloadRegistry.IsSupportedFilePayloadVersion(2));
    }
    [TestMethod]
    public void ShellContractKeepsExecutionAndRpcTimeoutsIndependent()
    {
        var request = new ShellCommandPrepareRequest
        {
            Command = "printf 'a | b' > result.txt",
            TimeoutMs = long.MaxValue,
            RpcTimeoutMs = 15000,
        };

        Assert.AreEqual("printf 'a | b' > result.txt", request.Command);
        Assert.AreEqual(long.MaxValue, request.TimeoutMs);
        Assert.AreEqual(15000, request.RpcTimeoutMs);

        request.TimeoutMs = null;
        Assert.IsNull(request.TimeoutMs);
        Assert.AreEqual(15000, request.RpcTimeoutMs);
    }

    [TestMethod]
    public void HistoricalThreadItemsSerializeTypedPartsAndPlan()
    {
        var item = new ThreadHistoryItem
        {
            Id = "item-1",
            TurnId = "turn-1",
            Type = "fileChange",
            Parts =
            [
                new ArtifactPart
                {
                    Kind = ArtifactPartKind.File,
                    DisplayName = "result.txt",
                    ActionId = "opaque-action",
                    AllowedActions = [ArtifactActionKind.Open, ArtifactActionKind.Reveal],
                },
            ],
            Plan = new TurnPlanSnapshot
            {
                ThreadId = "thread-1",
                TurnId = "turn-1",
                Steps = [new PlanStepInfo { StepId = "1", Text = "Review changes", Status = "completed" }],
                IsComplete = true,
            },
        };

        using JsonDocument payload = JsonDocument.Parse(JsonSerializer.Serialize(item));
        JsonElement serializedItem = payload.RootElement;
        Assert.AreEqual("opaque-action", serializedItem.GetProperty(nameof(ThreadHistoryItem.Parts))[0]
            .GetProperty(nameof(ArtifactPart.ActionId)).GetString());
        Assert.AreEqual("thread-1", serializedItem.GetProperty(nameof(ThreadHistoryItem.Plan))
            .GetProperty(nameof(TurnPlanSnapshot.ThreadId)).GetString());
        Assert.IsFalse(serializedItem.GetProperty(nameof(ThreadHistoryItem.Parts))[0]
            .TryGetProperty("ServerPath", out _));
    }

    [TestMethod]
    public void ShellConfirmationRequiresOpaquePreparedToken()
    {
        var confirmation = new ShellCommandConfirmationSnapshot
        {
            ConfirmationId = "opaque-token",
            ThreadId = "thread-1",
            Command = "echo safe",
            RunsUnsandboxedWithFullAccess = true,
        };

        Assert.AreEqual("opaque-token", confirmation.ConfirmationId);
        Assert.IsTrue(confirmation.RunsUnsandboxedWithFullAccess);
    }

    [TestMethod]
    public void ShellAcknowledgementMustBeAnEmptyObject()
    {
        using JsonDocument valid = JsonDocument.Parse("{}");
        using JsonDocument malformedObject = JsonDocument.Parse("{\"executionId\":\"unexpected\"}");
        using JsonDocument malformedArray = JsonDocument.Parse("[]");

        Assert.IsTrue(ShellCommandAdmission.IsAcknowledgement(valid.RootElement));
        Assert.IsFalse(ShellCommandAdmission.IsAcknowledgement(malformedObject.RootElement));
        Assert.IsFalse(ShellCommandAdmission.IsAcknowledgement(malformedArray.RootElement));
    }

    [TestMethod]
    public void WindowsSandboxStartAcknowledgementRequiresBooleanStarted()
    {
        using JsonDocument missing = JsonDocument.Parse("{}");
        using JsonDocument malformed = JsonDocument.Parse("{\"started\":\"yes\"}");
        using JsonDocument startedFalse = JsonDocument.Parse("{\"started\":false}");

        Assert.IsFalse(ShellCommandAdmission.TryReadStarted(missing.RootElement, out _));
        Assert.IsFalse(ShellCommandAdmission.TryReadStarted(malformed.RootElement, out _));
        Assert.IsTrue(ShellCommandAdmission.TryReadStarted(startedFalse.RootElement, out bool started));
        Assert.IsFalse(started);
    }

    [TestMethod]
    public void SavedAttachmentAddAcknowledgementRequiresMatchingPayloadProvenance()
    {
        const string path = "C:\\workspace\\notes.txt";
        string identityKey = SavedAttachmentPayloadRegistry.CreateIdentityKey(ServerPath.Create(path), serverIsWindows: true);
        using JsonDocument malformed = JsonDocument.Parse($"{{\"outcome\":\"created\",\"attachment\":{{\"attachmentType\":\"relaycodex.file.v1\",\"identityKey\":\"{identityKey}\",\"payload\":{{\"version\":1,\"serverPath\":\"C:\\\\workspace\\\\other.txt\",\"mimeType\":\"text/plain\",\"displayName\":\"notes.txt\"}}}}}}");

        Assert.IsFalse(SavedAttachmentPayloadCodec.TryReadAddResponse(
            malformed.RootElement, identityKey, path, serverIsWindows: true, out _, out _));
    }
}
