using System.Text.Json;
using Codex.AppServer.Protocol;
using Codex.VisualStudio.Contracts;
using Codex.VisualStudio.Worker;

namespace Codex.VisualStudio.Core.Tests;

[TestClass]
public sealed class InteractionValidationTests
{
    [TestMethod]
    public void JsonRpcRequestIds_PreserveJsonKindAndRejectUnsupportedIds()
    {
        using JsonDocument numeric = JsonDocument.Parse("1");
        using JsonDocument text = JsonDocument.Parse("\"1\"");
        using JsonDocument fraction = JsonDocument.Parse("1.0");

        Assert.AreEqual("n:1", JsonRpcRequestId.GetKey(numeric.RootElement));
        Assert.AreEqual("s:1", JsonRpcRequestId.GetKey(text.RootElement));
        Assert.AreNotEqual(JsonRpcRequestId.GetKey(numeric.RootElement), JsonRpcRequestId.GetKey(text.RootElement));
        Assert.IsFalse(JsonRpcRequestId.TryGetKey(fraction.RootElement, out _));
    }

    [TestMethod]
    public async Task Dispatcher_UncertainSendFailureDoesNotSendSecondResponse()
    {
        int sendCount = 0;
        var dispatcher = new JsonRpcServerRequestDispatcher((_, _) =>
        {
            Interlocked.Increment(ref sendCount);
            throw new IOException("send failed after write");
        });
        using JsonDocument request = JsonDocument.Parse("{\"id\":1,\"method\":\"test/request\",\"params\":{}}");
        dispatcher.Start(JsonSerializer.Deserialize<JsonRpcMessage>(request.RootElement)!,
            (_, _) => Task.FromResult(JsonSerializer.SerializeToElement(new { ok = true })), CancellationToken.None);

        await dispatcher.WhenOutstandingCompletedAsync().WaitAsync(TimeSpan.FromSeconds(2));
        Assert.AreEqual(1, Volatile.Read(ref sendCount));
    }

    [TestMethod]
    public async Task Dispatcher_UnexpectedExceptionIsShapedWithoutSensitiveText()
    {
        object? sent = null;
        var dispatcher = new JsonRpcServerRequestDispatcher((message, _) =>
        {
            sent = message;
            return Task.CompletedTask;
        });
        using JsonDocument request = JsonDocument.Parse("{\"id\":\"secret-id\",\"method\":\"test/request\",\"params\":{}}");
        dispatcher.Start(JsonSerializer.Deserialize<JsonRpcMessage>(request.RootElement)!,
            (_, _) => throw new InvalidOperationException("challenge-secret-sentinel"), CancellationToken.None);

        await dispatcher.WhenOutstandingCompletedAsync().WaitAsync(TimeSpan.FromSeconds(2));
        string wire = JsonSerializer.Serialize(sent);
        StringAssert.Contains(wire, "The client could not process the server request.");
        Assert.IsFalse(wire.Contains("challenge-secret-sentinel", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task Dispatcher_ExternallyResolvedRequestSuppressesResponse()
    {
        int sendCount = 0;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        var dispatcher = new JsonRpcServerRequestDispatcher((_, _) =>
        {
            Interlocked.Increment(ref sendCount);
            return Task.CompletedTask;
        });
        using JsonDocument request = JsonDocument.Parse("{\"id\":1,\"method\":\"test/request\",\"params\":{}}");
        dispatcher.Start(JsonSerializer.Deserialize<JsonRpcMessage>(request.RootElement)!, async (_, _) =>
        {
            entered.TrySetResult();
            return await finish.Task;
        }, CancellationToken.None);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        using JsonDocument resolution = JsonDocument.Parse("{\"requestId\":1}");
        dispatcher.ResolveServerRequest(resolution.RootElement);
        finish.TrySetResult(JsonSerializer.SerializeToElement(new { ok = true }));

        await dispatcher.WhenOutstandingCompletedAsync().WaitAsync(TimeSpan.FromSeconds(2));
        Assert.AreEqual(0, Volatile.Read(ref sendCount));
    }

    [TestMethod]
    public void McpFormParser_ValidatesTypesAndMapsOpaqueChoicesToExactValues()
    {
        using JsonDocument request = JsonDocument.Parse("""
        {"mode":"form","serverName":"example","requestedSchema":{"type":"object","properties":{
          "text":{"type":"string","minLength":1},
          "integer":{"type":"integer","minimum":2.5},
          "enabled":{"type":"boolean","default":false},
          "choice":{"type":"string","enum":["wire-secret-a","wire-secret-b"]},
          "many":{"type":"array","items":{"type":"string","enum":["wire-secret-x","wire-secret-y"]},"minItems":1,"maxItems":18446744073709551615}
        },"required":["text","integer","enabled","choice","many"]}}
        """);
        int id = 0;
        Assert.IsTrue(McpElicitationFormParser.TryParse(request.RootElement, "s:1", () => $"opaque-{++id}", out McpElicitationForm? form, out _));
        Assert.IsNotNull(form);
        Assert.AreEqual(false, form.Request.Fields.Single(field => field.Name == "enabled").DefaultValue?.BooleanValue);
        Assert.AreEqual(ulong.MaxValue, form.Request.Fields.Single(field => field.Name == "many").MaximumSelections);

        string choiceId = form.Request.Fields.Single(field => field.Name == "choice").Choices[1].ChoiceId;
        string multiId = form.Request.Fields.Single(field => field.Name == "many").Choices[0].ChoiceId;
        var values = new McpElicitationValue[]
        {
            new() { FieldName = "text", StringValue = "😀" },
            new() { FieldName = "integer", IntegerValue = 3 },
            new() { FieldName = "enabled", BooleanValue = false },
            new() { FieldName = "choice", ChoiceIds = [choiceId] },
            new() { FieldName = "many", ChoiceIds = [multiId] },
        };
        Assert.IsTrue(form.TryValidateValues(values, out JsonElement content, out _));
        Assert.AreEqual("wire-secret-b", content.GetProperty("choice").GetString());
        Assert.AreEqual("wire-secret-x", content.GetProperty("many")[0].GetString());
        Assert.IsFalse(form.TryValidateValues([new() { FieldName = "text", StringValue = "" }], out _, out _));
    }

    [TestMethod]
    public void McpFormParser_RefusesVerificationAndExtensionSchemasWithoutProjectingPayload()
    {
        using JsonDocument verification = JsonDocument.Parse("{\"mode\":\"openai/userVerification\",\"challenge\":\"secret-challenge-marker\",\"proof\":\"secret-proof-marker\"}");
        Assert.IsFalse(McpElicitationFormParser.TryParse(verification.RootElement, "s:x", () => "id", out McpElicitationForm? form, out McpElicitationParseRefusal refusal));
        Assert.IsNull(form);
        Assert.AreEqual(UnsupportedInteractionKind.UserVerification, refusal.UnsupportedKind);
        string refusalOutput = JsonSerializer.Serialize(new { form, refusal });
        Assert.IsFalse(refusalOutput.Contains("secret-challenge-marker", StringComparison.Ordinal));
        Assert.IsFalse(refusalOutput.Contains("secret-proof-marker", StringComparison.Ordinal));

        using JsonDocument extension = JsonDocument.Parse("{\"mode\":\"openai/form\",\"secret\":\"secret-marker\"}");
        Assert.IsFalse(McpElicitationFormParser.TryParse(extension.RootElement, "s:x", () => "id", out _, out McpElicitationParseRefusal extensionRefusal));
        Assert.AreEqual(McpElicitationParseStatus.Unsupported, extensionRefusal.Status);
        Assert.IsFalse(extensionRefusal.SafeReason.Contains("secret-marker", StringComparison.Ordinal));
    }

    [TestMethod]
    public void McpUrlElicitation_UsesProtectedActionAndNeverProjectsRawUrl()
    {
        using JsonDocument request = JsonDocument.Parse("""
        {"mode":"url","serverName":"auth","elicitationId":"elicitation-1","message":"Sign in","url":"https://auth.example.test/path?secret=private","threadId":"thread"}
        """);
        var store = new ProtectedAuthorizationUrlStore();
        Assert.IsTrue(McpElicitationFormParser.TryParse(
            request.RootElement,
            "s:request",
            () => "unused",
            out McpElicitationForm? form,
            out _,
            url => store.TryStore(1, url, out ProtectedAuthorizationUrlInfo info) ? info : null));
        Assert.IsNotNull(form);
        Assert.AreEqual(McpElicitationKind.Url, form.Request.Kind);
        Assert.AreEqual("https://auth.example.test", form.Request.OriginDisplay);
        Assert.IsFalse(JsonSerializer.Serialize(form.Request).Contains("secret=private", StringComparison.Ordinal));
        Assert.IsNotNull(form.Request.OpenAuthorizationActionId);
        Assert.IsTrue(store.TryTake(1, form.Request.OpenAuthorizationActionId!, out Uri? protectedUri));
        Assert.AreEqual("https://auth.example.test/path?secret=private", protectedUri?.ToString());
        store.Dispose();
    }

    [TestMethod]
    public void McpStringFormats_RequireValidCalendarDatesZonesAndBareEmailsButAllowGenericUris()
    {
        AssertFormatted("date", "2024-02-29", true);
        AssertFormatted("date", "2025-02-29", false);
        AssertFormatted("date-time", "2026-10-05T12:30:00Z", true);
        AssertFormatted("date-time", "2026-10-05T12:30:00", false);
        AssertFormatted("email", "person@example.test", true);
        AssertFormatted("email", "Person <person@example.test>", false);
        AssertFormatted("uri", "urn:example:resource", true);

        static void AssertFormatted(string format, string value, bool expected)
        {
            string schema = JsonSerializer.Serialize(new
            {
                mode = "form",
                serverName = "format-test",
                requestedSchema = new
                {
                    type = "object",
                    properties = new Dictionary<string, object>
                    {
                        ["value"] = new { type = "string", format },
                    },
                    required = new[] { "value" },
                },
            });
            using JsonDocument request = JsonDocument.Parse(schema);
            Assert.IsTrue(McpElicitationFormParser.TryParse(request.RootElement, "s:format", () => Guid.NewGuid().ToString("N"), out McpElicitationForm? form, out _));
            Assert.IsNotNull(form);
            bool valid = form.TryValidateValues([new McpElicitationValue { FieldName = "value", StringValue = value }], out _, out _);
            Assert.AreEqual(expected, valid, $"Format '{format}' value '{value}' validation differed.");
        }
    }

    [TestMethod]
    public void OfferedCommandChoices_UseOnlyExactOfferedDecisionsAndPreserveOrder()
    {
        using JsonDocument request = JsonDocument.Parse("""
        {"availableDecisions":["decline",{"applyNetworkPolicyAmendment":{"network_policy_amendment":{"action":"deny","host":"example.test"}}}],
         "proposedExecpolicyAmendment":["must-not-be-added"],
         "additionalPermissions":{"network":{"enabled":true},"fileSystem":{"entries":[{"access":"read","path":{"type":"path","path":"/safe/path"}}]}}}
        """);
        int id = 0;
        Assert.IsTrue(OfferedCommandChoiceSet.TryCreate(request.RootElement, value => value, () => $"id-{++id}", out OfferedCommandChoiceSet? choices));
        Assert.IsNotNull(choices);
        Assert.AreEqual(2, choices.Choices.Count);
        Assert.AreEqual("Decline", choices.Choices[0].Label);
        StringAssert.Contains(choices.Choices[1].Description, "persistent deny rule for network host example.test");
        StringAssert.Contains(choices.Choices[0].Description, "network access");
        StringAssert.Contains(choices.Choices[0].Description, "/safe/path");
        Assert.IsFalse(choices.TryResolve("invented", out _));
        Assert.IsTrue(choices.TryResolve(choices.Choices[1].ChoiceId, out JsonElement response));
        Assert.AreEqual("deny", response.GetProperty("decision").GetProperty("applyNetworkPolicyAmendment")
            .GetProperty("network_policy_amendment").GetProperty("action").GetString());
        Assert.AreEqual("example.test", response.GetProperty("decision").GetProperty("applyNetworkPolicyAmendment")
            .GetProperty("network_policy_amendment").GetProperty("host").GetString());
    }

    [TestMethod]
    public void PermissionSelection_RejectsForgedIdsAndPreservesDenyConstraints()
    {
        using JsonDocument requested = JsonDocument.Parse("""
        {"fileSystem":{"entries":[
          {"access":"read","path":{"type":"glob_pattern","pattern":"src/**"}},
          {"access":"deny","path":{"type":"path","path":"src/secrets"}}],"globScanMaxDepth":12},
         "network":{"enabled":true}}
        """);
        int id = 0;
        Assert.IsTrue(PermissionSelectionBuilder.TryCreate(requested.RootElement, value => value, () => $"perm-{++id}", out PermissionSelectionSet? set));
        Assert.IsNotNull(set);
        Assert.IsFalse(set.TryBuild(["unknown"], PermissionScope.Turn, out _, out _));
        PermissionGrantOption grant = set.RequestedPermissions.Single(option => option.Label.Contains("src/**", StringComparison.Ordinal));
        Assert.IsTrue(set.TryBuild([grant.PermissionId], PermissionScope.Session, out JsonElement response, out _));
        Assert.AreEqual("session", response.GetProperty("scope").GetString());
        JsonElement fileSystem = response.GetProperty("permissions").GetProperty("fileSystem");
        Assert.AreEqual(12, fileSystem.GetProperty("globScanMaxDepth").GetInt32());
        Assert.AreEqual(2, fileSystem.GetProperty("entries").GetArrayLength());
        Assert.AreEqual("deny", fileSystem.GetProperty("entries")[1].GetProperty("access").GetString());
    }

    [TestMethod]
    public void AuthorizationUrls_AreProtectedRevocableAndOneShot()
    {
        var store = new ProtectedAuthorizationUrlStore();
        Assert.IsFalse(store.TryStore(1, "javascript:alert(1)", out _));
        Assert.IsFalse(store.TryStore(1, "https://user:pass@example.test/auth", out _));
        Assert.IsTrue(store.TryStore(1, "https://example.test/path?secret=hidden", out ProtectedAuthorizationUrlInfo info));
        Assert.AreEqual("https://example.test", info.OriginDisplay);
        Assert.IsTrue(store.Remove(1, info.ActionId));
        Assert.IsFalse(store.TryTake(1, info.ActionId, out _));
        Assert.IsTrue(store.TryStore(1, "http://127.0.0.1:4312/callback", out info));
        Assert.IsTrue(store.TryTake(1, info.ActionId, out Uri? uri));
        Assert.AreEqual("http://127.0.0.1:4312/callback", uri?.ToString());
        Assert.IsFalse(store.TryTake(1, info.ActionId, out _));
        store.RetireGeneration(2);
        Assert.IsFalse(store.TryStore(2, "https://example.test/auth", out _));
        store.Dispose();
    }

    [TestMethod]
    public void PendingRegistry_ValidatesBeforeAtomicCompletionAndRejectsStaleEntryReuse()
    {
        using var registry = new PendingInteractionRegistry();
        using JsonDocument id = JsonDocument.Parse("7");
        using JsonDocument parameters = JsonDocument.Parse("{\"safe\":true}");
        using JsonDocument timeout = JsonDocument.Parse("{\"decision\":\"decline\"}");
        using JsonDocument response = JsonDocument.Parse("{\"decision\":\"accept\"}");
        var key = new PendingInteractionKey(3, JsonRpcRequestId.GetKey(id.RootElement));
        Assert.IsTrue(registry.TryAdd(key, "method/a", id.RootElement, parameters.RootElement, TimeSpan.FromMinutes(1), timeout.RootElement, out PendingInteractionEntry first));
        Assert.IsTrue(registry.TryComplete(key, first, response.RootElement));
        Assert.AreEqual(PendingInteractionCompletionKind.Respond, first.Completion.Result.Kind);
        Assert.IsFalse(registry.TryComplete(key, first, response.RootElement));

        Assert.IsTrue(registry.TryAdd(key, "method/a", id.RootElement, parameters.RootElement, TimeSpan.FromMinutes(1), timeout.RootElement, out PendingInteractionEntry second));
        Assert.AreNotEqual(first.InteractionId, second.InteractionId);
        Assert.IsFalse(registry.TryComplete(key, first, response.RootElement));
        Assert.IsTrue(registry.TryComplete(key, second, response.RootElement));
    }

    [TestMethod]
    public void PendingRegistry_ExternalResolutionBeforeRegistrationSuppressesEntry()
    {
        using var registry = new PendingInteractionRegistry();
        using JsonDocument id = JsonDocument.Parse("\"7\"");
        using JsonDocument parameters = JsonDocument.Parse("{}");
        using JsonDocument timeout = JsonDocument.Parse("{}");
        var key = new PendingInteractionKey(5, JsonRpcRequestId.GetKey(id.RootElement));
        Assert.IsFalse(registry.TryResolveExternally(key));
        Assert.IsTrue(registry.TryAdd(key, "mcpServer/elicitation/request", id.RootElement, parameters.RootElement, TimeSpan.FromMinutes(1), timeout.RootElement, out PendingInteractionEntry entry));
        Assert.AreEqual(PendingInteractionCompletionKind.ExternallyResolved, entry.Completion.Result.Kind);
        Assert.IsFalse(entry.Completion.Result.ShouldSendResponse);
        Assert.IsFalse(registry.TryGetByInteractionId(5, entry.InteractionId, out _));
    }

    [TestMethod]
    public void PendingRegistry_DelayedResolutionForCompletedIdDoesNotSuppressSameGenerationReuse()
    {
        using var registry = new PendingInteractionRegistry();
        using JsonDocument id = JsonDocument.Parse("\"reused-id\"");
        using JsonDocument parameters = JsonDocument.Parse("{}");
        using JsonDocument timeout = JsonDocument.Parse("{}");
        using JsonDocument response = JsonDocument.Parse("{\"answers\":{}}");
        var key = new PendingInteractionKey(4, JsonRpcRequestId.GetKey(id.RootElement));

        Assert.IsTrue(registry.TryAdd(key, "method/a", id.RootElement, parameters.RootElement, TimeSpan.FromMinutes(1), timeout.RootElement, out PendingInteractionEntry first));
        Assert.IsTrue(registry.TryComplete(key, first, response.RootElement));
        Assert.IsFalse(registry.TryResolveExternally(key));

        Assert.IsTrue(registry.TryAdd(key, "method/a", id.RootElement, parameters.RootElement, TimeSpan.FromMinutes(1), timeout.RootElement, out PendingInteractionEntry reused));
        Assert.AreNotEqual(first.InteractionId, reused.InteractionId);
        Assert.IsFalse(reused.Completion.IsCompleted);
        Assert.IsTrue(registry.TryComplete(key, reused, response.RootElement));
    }

    [TestMethod]
    public void PendingRegistry_RetirementAndDisposalRejectLateRegistration()
    {
        using JsonDocument id = JsonDocument.Parse("1");
        using JsonDocument parameters = JsonDocument.Parse("{}");
        using JsonDocument timeout = JsonDocument.Parse("{}");
        var retired = new PendingInteractionRegistry();
        retired.RetireGeneration(9);
        Assert.IsFalse(retired.TryAdd(new PendingInteractionKey(9, "n:1"), "m", id.RootElement, parameters.RootElement,
            TimeSpan.FromMinutes(1), timeout.RootElement, out _));
        retired.Dispose();

        var disposed = new PendingInteractionRegistry();
        disposed.Dispose();
        Assert.IsFalse(disposed.TryAdd(new PendingInteractionKey(9, "n:1"), "m", id.RootElement, parameters.RootElement,
            TimeSpan.FromMinutes(1), timeout.RootElement, out _));
    }
}
