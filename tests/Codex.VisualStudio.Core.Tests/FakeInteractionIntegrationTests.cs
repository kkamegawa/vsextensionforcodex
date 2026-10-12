using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using Codex.AppServer.Protocol;
using Codex.VisualStudio.Contracts;
using Codex.VisualStudio.Worker;

namespace Codex.VisualStudio.Core.Tests;

[TestClass]
public sealed class FakeInteractionIntegrationTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(12);

    [TestMethod]
    public async Task FakeAppServerExercisesConcurrentInteractionsAndAuthenticationRecoveryOverStdio()
    {
        string repositoryRoot = FindRepositoryRoot();
#if DEBUG
        const string configuration = "Debug";
#else
        const string configuration = "Release";
#endif
        string fakeAssembly = Path.Combine(repositoryRoot, "src", "Codex.AppServer.Fake", "bin", configuration, "net8.0", "Codex.AppServer.Fake.dll");
        Assert.IsTrue(File.Exists(fakeAssembly), $"Build the solution before running this process integration test: {fakeAssembly}");

        using var process = new Process
        {
            StartInfo = new ProcessStartInfo("dotnet")
            {
                WorkingDirectory = repositoryRoot,
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            },
        };
        process.StartInfo.ArgumentList.Add(fakeAssembly);
        process.StartInfo.Environment["CODEX_FAKE_SCENARIO"] = "interaction";
        Assert.IsTrue(process.Start(), "The fake app-server process did not start.");
        _ = process.StandardError.ReadToEndAsync();

        await using var connection = new JsonLineRpcConnection(process.StandardOutput.BaseStream, process.StandardInput.BaseStream);
        await connection.StartAsync(CancellationToken.None);
        await using var service = new CodexSessionService(
            new ApprovalPolicyEngine(new PathAccessPolicy()),
            new SecretRedactor());

        var questions = new ConcurrentQueue<UserInputRequest>();
        var approvals = new ConcurrentQueue<ApprovalRequest>();
        var permissions = new ConcurrentQueue<PermissionRequest>();
        var elicitations = new ConcurrentQueue<McpElicitationRequest>();
        var notices = new ConcurrentQueue<UnsupportedInteractionNotice>();
        var authStatuses = new ConcurrentQueue<InteractionAuthStatus>();
        service.UserInputRequested += (value, _) => { questions.Enqueue(value); return Task.CompletedTask; };
        service.ApprovalRequested += (value, _) => { approvals.Enqueue(value); return Task.CompletedTask; };
        service.PermissionRequested += (value, _) => { permissions.Enqueue(value); return Task.CompletedTask; };
        service.McpElicitationRequested += (value, _) => { elicitations.Enqueue(value); return Task.CompletedTask; };
        service.UnsupportedInteraction += (value, _) => { notices.Enqueue(value); return Task.CompletedTask; };
        service.InteractionAuthStatusChanged += (value, _) => { authStatuses.Enqueue(value); return Task.CompletedTask; };

        try
        {
            await service.InitializeAsync(connection, new WorkerOptions
            {
                WorkingDirectory = repositoryRoot,
                ExtensionVersion = "integration-test",
                ExperimentalApi = true,
            }, CancellationToken.None);

            Assert.IsTrue(service.GatewayOAuthRequired);
            InteractionAuthStatus login = await service.LoginGatewayOAuthAsync(CancellationToken.None);
            Assert.IsTrue(login.State is InteractionAuthState.LoginPending or InteractionAuthState.Authenticated);
            await WaitUntilAsync(
                () => authStatuses.Any(status => status.State == InteractionAuthState.Authenticated),
                "The gateway changed notification did not finish the login.");

            ThreadSummary thread = await service.StartThreadAsync(CancellationToken.None);
            Assert.AreEqual("fake-thread-1", thread.Id);

            _ = await connection.SendRequestAsync(
                "fake/interactions/start",
                new { },
                Timeout,
                CancellationToken.None);

            try
            {
                await WaitUntilAsync(
                    () => questions.Count == 2
                        && approvals.Count == 1
                        && permissions.Count == 1
                        && elicitations.Count == 2
                        && notices.Count >= 3,
                    "The fake server did not dispatch all interaction requests.");
            }
            catch (OperationCanceledException)
            {
                JsonElement diagnosticState = await connection.SendRequestAsync(
                    "fake/interactions/state",
                    new { },
                    Timeout,
                    CancellationToken.None);
                string responseSummary = string.Join(
                    ", ",
                    diagnosticState.GetProperty("responses").EnumerateArray().Select(response =>
                    {
                        string id = response.TryGetProperty("id", out JsonElement responseId) ? responseId.GetRawText() : "(no id)";
                        string method = response.TryGetProperty("method", out JsonElement responseMethod) ? responseMethod.GetString() ?? "(no method)" : "(no method)";
                        string outcome = response.TryGetProperty("error", out JsonElement error)
                            && error.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined)
                                ? $"error {error.GetProperty("code").GetInt32()}"
                                : "result";
                        return $"{id}:{method}={outcome}";
                    }));
                string[] methodsSeen = diagnosticState.GetProperty("methods").EnumerateArray()
                    .Select(method => method.GetString() ?? "(invalid)")
                    .ToArray();
                Assert.Fail(
                    $"Timed out waiting for interaction projections. Counts: questions={questions.Count}, approvals={approvals.Count}, permissions={permissions.Count}, elicitations={elicitations.Count}, notices={notices.Count}. "
                    + $"Fake responses: [{responseSummary}], pending={diagnosticState.GetProperty("pendingResponses").GetInt32()}, client methods=[{string.Join(", ", methodsSeen)}].");
            }

            UserInputRequest[] observedQuestions = questions.ToArray();
            Assert.AreEqual(2, observedQuestions.Length);
            Assert.IsTrue(observedQuestions.All(question => question.Questions.Count == 1));
            Assert.AreNotEqual(observedQuestions[0].RequestId, observedQuestions[1].RequestId);
            Assert.IsTrue(notices.Any(notice => notice.Kind == UnsupportedInteractionKind.SecretInput));
            Assert.IsTrue(notices.Any(notice => notice.Kind == UnsupportedInteractionKind.UserVerification));
            Assert.IsTrue(notices.Any(notice => notice.Kind == UnsupportedInteractionKind.McpElicitationSchema));
            Assert.IsFalse(notices.Any(notice => notice.Message.Contains("fake-secret-challenge", StringComparison.Ordinal)));
            Assert.IsFalse(notices.Any(notice => notice.Message.Contains("secret test value", StringComparison.Ordinal)));

            UserInputRequest freeTextQuestion = observedQuestions.Single(question => question.Questions[0].IsOther);
            await service.ResolveUserInputAsync(new ResolveUserInputRequest
            {
                RequestId = freeTextQuestion.RequestId,
                Action = UserInputAction.Submit,
                Answers = new Dictionary<string, UserInputAnswer>
                {
                    [freeTextQuestion.Questions[0].Id] = new()
                    {
                        Kind = UserInputAnswerKind.Other,
                        Text = "Klingon",
                    },
                },
            }, CancellationToken.None);

            UserInputRequest selectedQuestion = observedQuestions.Single(question => !question.Questions[0].IsOther);
            await service.ResolveUserInputAsync(new ResolveUserInputRequest
            {
                RequestId = selectedQuestion.RequestId,
                Action = UserInputAction.Submit,
                Answers = new Dictionary<string, UserInputAnswer>
                {
                    [selectedQuestion.Questions[0].Id] = new()
                    {
                        Kind = UserInputAnswerKind.SelectedOptions,
                        OptionIds = [selectedQuestion.Questions[0].Options[0].OptionId],
                    },
                },
            }, CancellationToken.None);

            PermissionRequest permission = permissions.Single();
            await service.ResolvePermissionSelectionAsync(new ResolvePermissionSelectionRequest
            {
                RequestId = permission.RequestId,
                SelectedPermissionIds = [permission.RequestedPermissions[0].PermissionId],
                Scope = PermissionScope.Turn,
            }, CancellationToken.None);

            ApprovalRequest approval = approvals.Single();
            Assert.IsTrue(approval.Choices.Count >= 2, "The command request must expose the server's offered choices.");
            string selectedChoice = approval.Choices[0].ChoiceId;
            await service.ResolveApprovalAsync(new ResolveApprovalRequest
            {
                RequestId = approval.RequestId,
                ChoiceId = selectedChoice,
            }, CancellationToken.None);

            McpElicitationRequest form = elicitations.Single(value => value.Kind == McpElicitationKind.Form);
            Assert.AreEqual(6, form.Fields.Count);
            McpElicitationField singleSelect = form.Fields.Single(field => field.Type == McpElicitationFieldType.SingleSelect);
            await service.ResolveMcpElicitationAsync(new ResolveMcpElicitationRequest
            {
                RequestId = form.RequestId,
                Action = McpElicitationAction.Accept,
                Values =
                [
                    new() { FieldName = "email", StringValue = "person@example.test" },
                    new() { FieldName = "ratio", NumberValue = 1.25 },
                    new() { FieldName = "count", IntegerValue = 3 },
                    new() { FieldName = "enabled", BooleanValue = false },
                    new() { FieldName = "mode", ChoiceIds = [singleSelect.Choices[0].ChoiceId] },
                    new() { FieldName = "tags", ChoiceIds = form.Fields.Single(field => field.Type == McpElicitationFieldType.MultiSelect).Choices.Take(2).Select(choice => choice.ChoiceId).ToArray() },
                ],
            }, CancellationToken.None);

            McpElicitationRequest url = elicitations.Single(value => value.Kind == McpElicitationKind.Url);
            Assert.IsFalse(string.IsNullOrWhiteSpace(url.OpenAuthorizationActionId));
            await service.ResolveMcpElicitationAsync(new ResolveMcpElicitationRequest
            {
                RequestId = url.RequestId,
                Action = McpElicitationAction.Decline,
            }, CancellationToken.None);

            await WaitUntilAsync(async () =>
            {
                JsonElement state = await connection.SendRequestAsync(
                    "fake/interactions/state",
                    new { },
                    Timeout,
                    CancellationToken.None);
                return state.GetProperty("responses").GetArrayLength() == 9
                    && state.GetProperty("pendingResponses").GetInt32() == 0;
            }, "The fake server did not receive one terminal response for every server request.");

            await service.StartMcpOAuthLoginAsync(new StartMcpOAuthLoginRequest
            {
                ServerName = "docs",
                ThreadId = thread.Id,
            }, CancellationToken.None);
            await WaitUntilAsync(
                () => authStatuses.Any(status => status.McpServers.Any(server =>
                    server.ServerName == "docs" && server.State == InteractionAuthState.ReauthenticationRequired)),
                "The startup failure requiring reauthentication was not reflected in status.");

            InteractionAuthStatus canceled = await service.CancelGatewayOAuthAsync(CancellationToken.None);
            Assert.AreEqual(InteractionAuthState.Failed, canceled.State);

            JsonElement finalState = await connection.SendRequestAsync(
                "fake/interactions/state",
                new { },
                Timeout,
                CancellationToken.None);
            JsonElement clientCapabilities = finalState.GetProperty("initializeParams").GetProperty("capabilities");
            Assert.IsFalse(clientCapabilities.TryGetProperty("userVerification", out _));
            Assert.IsFalse(clientCapabilities.TryGetProperty("nativeUserVerification", out _));

            JsonElement[] responses = finalState.GetProperty("responses").EnumerateArray().ToArray();
            Assert.AreEqual(9, responses.Length);
            Assert.AreEqual(0, finalState.GetProperty("pendingResponses").GetInt32());
            JsonElement verificationResponse = responses.Single(response =>
                response.GetProperty("method").GetString() == "mcpServer/elicitation/request"
                && response.GetProperty("id").GetInt32() == 18);
            Assert.AreEqual("cancel", verificationResponse.GetProperty("result").GetProperty("action").GetString());
            string diagnostics = JsonSerializer.Serialize(finalState);
            Assert.IsFalse(diagnostics.Contains("fake-secret-challenge", StringComparison.Ordinal));
            Assert.IsFalse(diagnostics.Contains("fake-secret-proof", StringComparison.Ordinal));
            Assert.AreEqual(1, finalState.GetProperty("methods").EnumerateArray().Count(method =>
                method.GetString() == "mcpServer/oauth/login"));
            Assert.IsFalse(finalState.GetProperty("methods").EnumerateArray().Any(method =>
                method.GetString() == "mcpServer/tool/call"));

            JsonElement stringQuestion = responses.Single(response =>
                response.GetProperty("method").GetString() == "item/tool/requestUserInput"
                && response.GetProperty("id").ValueKind == JsonValueKind.String);
            Assert.AreEqual("12", stringQuestion.GetProperty("id").GetString());
            Assert.AreEqual(
                "Klingon",
                stringQuestion.GetProperty("result").GetProperty("answers").GetProperty("language").GetProperty("answers")[0].GetString());

            JsonElement numberQuestion = responses.Single(response =>
                response.GetProperty("method").GetString() == "item/tool/requestUserInput"
                && response.GetProperty("id").ValueKind == JsonValueKind.Number
                && response.GetProperty("id").GetInt32() == 12);
            Assert.AreEqual(12, numberQuestion.GetProperty("id").GetInt32());
            Assert.AreEqual(1, numberQuestion.GetProperty("result").GetProperty("answers").GetProperty("topic").GetProperty("answers").GetArrayLength());

            JsonElement permissionResponse = responses.Single(response =>
                response.GetProperty("method").GetString() == "item/permissions/requestApproval");
            Assert.AreEqual(JsonValueKind.Object, permissionResponse.GetProperty("result").GetProperty("permissions").ValueKind);
            Assert.AreEqual("turn", permissionResponse.GetProperty("result").GetProperty("scope").GetString());

            JsonElement commandResponse = responses.Single(response =>
                response.GetProperty("method").GetString() == "item/commandExecution/requestApproval");
            Assert.IsFalse(string.IsNullOrWhiteSpace(commandResponse.GetProperty("result").GetProperty("decision").GetString()));

            JsonElement formResponse = responses.Single(response =>
                response.GetProperty("method").GetString() == "mcpServer/elicitation/request"
                && response.GetProperty("id").GetInt32() == 11);
            JsonElement content = formResponse.GetProperty("result").GetProperty("content");
            Assert.AreEqual(JsonValueKind.False, content.GetProperty("enabled").ValueKind);
            Assert.AreEqual("person@example.test", content.GetProperty("email").GetString());
            Assert.AreEqual(3, content.GetProperty("count").GetInt32());
            Assert.AreEqual(JsonValueKind.Number, content.GetProperty("ratio").ValueKind);

            JsonElement secretResponse = responses.Single(response =>
                response.GetProperty("id").ValueKind == JsonValueKind.Number
                && response.GetProperty("id").GetInt32() == 13);
            Assert.AreEqual(0, secretResponse.GetProperty("result").GetProperty("answers").EnumerateObject().Count());

            JsonElement startupOrder = finalState.GetProperty("methods");
            string[] methods = startupOrder.EnumerateArray().Select(method => method.GetString()!).ToArray();
            Assert.IsTrue(Array.IndexOf(methods, "account/gatewayOAuth/read") > Array.IndexOf(methods, "initialize"));
            Assert.IsTrue(Array.IndexOf(methods, "account/gatewayOAuth/read") < Array.IndexOf(methods, "thread/start"));
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
            }
        }
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "CodexForVisualStudio.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate the repository root from the test output directory.");
    }

    private static async Task WaitUntilAsync(Func<bool> condition, string failureMessage)
    {
        using var timeout = new CancellationTokenSource(Timeout);
        while (!condition())
        {
            await Task.Delay(TimeSpan.FromMilliseconds(20), timeout.Token);
        }

        Assert.IsTrue(condition(), failureMessage);
    }

    private static async Task WaitUntilAsync(Func<Task<bool>> condition, string failureMessage)
    {
        using var timeout = new CancellationTokenSource(Timeout);
        while (!await condition())
        {
            await Task.Delay(TimeSpan.FromMilliseconds(20), timeout.Token);
        }

        Assert.IsTrue(await condition(), failureMessage);
    }
}
