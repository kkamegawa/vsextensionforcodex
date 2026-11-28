using Codex.VisualStudio.Contracts;
using Codex.VisualStudio.Extension;
using System.Globalization;
using System.IO;
using System.Text.Json;
using StreamJsonRpc;

namespace Codex.VisualStudio.Ui.Tests;

public sealed partial class ViewModelTests
{
    private static readonly string[] AzureModelIds = ["azure-model"];
    private static readonly string[] OpenAiModelIds = ["openai-model"];
    private static readonly string[] BuiltInFallbackModelIds = ["gpt-5-codex", "gpt-5"];
    private static readonly string[] RequiredModelProviderStringKeys =
    [
        "Provider.DisplayNameDefault",
        "Provider.Type.OpenAI",
        "Provider.Type.CustomGateway",
        "Provider.Type.Bedrock",
        "Provider.Type.GoogleCloud",
        "Provider.Error.NotConfigured",
        "Provider.Error.PolicyConflict",
        "Provider.Error.Mismatch",
        "Provider.ResumeError.NotConfigured",
        "Provider.ResumeError.PolicyConflict",
        "Provider.ResumeError.Mismatch",
        "Provider.ResumeError.ReadOnlyGuidance",
        "Provider.FixedDefault",
        "Provider.DefaultWriteOutcomeUnknown",
        "Provider.DefaultRefreshFailed",
        "Provider.DefaultWriteFailed",
        "Provider.BaseUrlLabel",
        "Provider.Unknown",
        "Provider.SavedConversation",
        "Provider.SavedConversationHelp",
        "Provider.SelectionHelp",
        "Provider.SetDefault",
        "Provider.SetDefaultTooltip",
        "Provider.SelectorAutomationName",
        "Provider.DefaultChanged",
        "Provider.ModelsUnavailable",
        "Provider.GatewayOAuthReadFailed",
        "Provider.GatewayOAuthOperationFailed",
    ];

    [TestMethod]
    [DoNotParallelize]
    public void ModelProviderStringsArePresentInEnglishAndJapaneseResources()
    {
        using Stream? englishStream = typeof(LocalizedStrings).Assembly.GetManifestResourceStream("Codex.VisualStudio.Contracts.Strings.en.json");
        using Stream? japaneseStream = typeof(LocalizedStrings).Assembly.GetManifestResourceStream("Codex.VisualStudio.Contracts.Strings.ja.json");
        Assert.IsNotNull(englishStream);
        Assert.IsNotNull(japaneseStream);
        Dictionary<string, string> english = JsonSerializer.Deserialize<Dictionary<string, string>>(englishStream)!;
        Dictionary<string, string> japanese = JsonSerializer.Deserialize<Dictionary<string, string>>(japaneseStream)!;
        CollectionAssert.AreEquivalent(english.Keys.ToArray(), japanese.Keys.ToArray());
        foreach (string key in RequiredModelProviderStringKeys)
        {
            Assert.IsTrue(english.ContainsKey(key), $"English localization is missing the required key {key}.");
            Assert.IsTrue(japanese.ContainsKey(key), $"Japanese localization is missing the required key {key}.");
        }

        CultureInfo previousCulture = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("en-US");
            foreach ((string key, string expected) in english)
            {
                Assert.AreEqual(expected, LocalizedStrings.Get(key), $"English localization missing for {key}.");
                Assert.IsFalse(string.Equals(key, expected, StringComparison.Ordinal), $"English localization must not fall back to the key {key}.");
            }

            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("ja-JP");
            foreach ((string key, string expected) in japanese)
            {
                Assert.AreEqual(expected, LocalizedStrings.Get(key), $"Japanese localization missing for {key}.");
                Assert.IsFalse(string.Equals(key, expected, StringComparison.Ordinal), $"Japanese localization must not fall back to the key {key}.");
            }
        }
        finally
        {
            CultureInfo.CurrentUICulture = previousCulture;
        }
    }

    [TestMethod]
    public async Task ChatViewModel_ModelProviderSelectionScopesModelsOAuthAndNewThreads()
    {
        var bridge = new FakeWorkerBridge
        {
            ModelProvidersResult = ProviderCatalog(defaultProviderId: "openai"),
            ListModelsHandler = (request, _) =>
            {
                string modelId = request.ModelProviderId is null ? "default-model" : $"{request.ModelProviderId}-model";
                return Task.FromResult(new ListModelsResult
                {
                    Models = [new ModelInfo { Id = modelId }],
                    DefaultModel = modelId,
                });
            },
        };
        using var vm = new ChatViewModel(bridge, autoConnect: false);

        await vm.PopulateModelsAsync();
        await bridge.PublishStateAsync(new WorkerStatus { State = WorkerConnectionState.Ready });

        Assert.AreEqual("openai", vm.SelectedModelProviderId);
        Assert.AreEqual("openai", bridge.LastListModelsRequest?.ModelProviderId);
        Assert.IsTrue(vm.IsModelProviderSelectorEnabled);
        Assert.IsFalse(vm.CanSetDefaultModelProvider);

        vm.SelectedModelProviderId = "azure";
        await WaitForAsync(() => bridge.LastListModelsRequest?.ModelProviderId == "azure");
        await WaitForAsync(() => vm.Models.Contains("azure-model"));
        await WaitForAsync(() => bridge.LastReadGatewayOAuthRequest?.ProviderId == "azure");
        CollectionAssert.AreEqual(AzureModelIds, vm.Models.ToArray());

        await RunCommandAsync(vm.NewThreadCommand);

        Assert.AreEqual("azure", bridge.LastStartThreadRequest?.ModelProviderId);
        Assert.AreEqual("azure", vm.SelectedThread?.ModelProviderId);

        int modelListCallsBeforeSavedThread = bridge.ModelListCallCount;
        vm.SelectedThread = new ThreadSummary { Id = "saved-thread", ModelProviderId = "openai" };
        await WaitForAsync(() =>
            bridge.ModelListCallCount > modelListCallsBeforeSavedThread
            && bridge.LastListModelsRequest?.ModelProviderId == "openai");
        Assert.AreEqual("openai", bridge.LastListModelsRequest?.ModelProviderId, "A saved conversation must use its stored provider instead of the new-thread selection.");
        Assert.IsFalse(vm.ShowModelProviderSelector);

        int modelListCallsBeforeLegacyThread = bridge.ModelListCallCount;
        vm.SelectedThread = new ThreadSummary { Id = "legacy-thread" };
        await WaitForAsync(() => bridge.ModelListCallCount > modelListCallsBeforeLegacyThread);
        Assert.IsNull(bridge.LastListModelsRequest?.ModelProviderId, "A saved conversation without provider metadata must not inherit the current new-thread choice.");
    }

    [TestMethod]
    public async Task ChatViewModel_SavedThreadProviderLoadsModelsAndOAuthWhenCatalogIsNotLoaded()
    {
        var bridge = new FakeWorkerBridge
        {
            ReadGatewayOAuthHandler = (request, _) => Task.FromResult(new InteractionAuthStatus
            {
                IsLocal = true,
                IsSupported = true,
                ModelProviderId = request.ProviderId,
                Message = $"OAuth status for {request.ProviderId}",
                State = InteractionAuthState.Authenticated,
            }),
            ModelListResult = new ListModelsResult
            {
                Models = [new ModelInfo { Id = "azure-model" }],
                DefaultModel = "azure-model",
            },
        };
        using var vm = new ChatViewModel(bridge, autoConnect: false);

        vm.SelectedThread = new ThreadSummary { Id = "saved-thread", ModelProviderId = "azure" };

        await WaitForAsync(() => bridge.LastListModelsRequest?.ModelProviderId == "azure"
            && bridge.ReadGatewayOAuthCallCount > 0);

        Assert.AreEqual(1, bridge.ListModelProvidersCallCount);
        Assert.AreEqual("azure", bridge.LastListModelsRequest?.ModelProviderId);
        Assert.AreEqual("azure", bridge.LastReadGatewayOAuthRequest?.ProviderId);
        Assert.AreEqual(InteractionAuthState.Authenticated, vm.InteractionAuthStatus?.State);
        CollectionAssert.AreEqual(AzureModelIds, vm.Models.ToArray());
    }

    [TestMethod]
    public async Task ChatViewModel_EmptyFailedProviderCatalogCanBeRetried()
    {
        int catalogCalls = 0;
        var bridge = new FakeWorkerBridge
        {
            ListModelProvidersHandler = (_, _) => Interlocked.Increment(ref catalogCalls) == 1
                ? Task.FromResult(new ListModelProvidersResult
                {
                    DefaultCatalogRefreshFailed = true,
                    Providers = [],
                })
                : Task.FromResult(ProviderCatalog(defaultProviderId: "azure")),
        };
        using var vm = new ChatViewModel(bridge, autoConnect: false);

        await vm.PopulateModelsAsync();

        Assert.AreEqual(0, vm.ModelProviders.Count);
        Assert.AreEqual(LocalizedStrings.Get("Provider.DefaultRefreshFailed"), vm.ModelProviderStatusText);

        await vm.PopulateModelsAsync();

        Assert.AreEqual(2, bridge.ListModelProvidersCallCount);
        Assert.AreEqual("azure", vm.SelectedModelProviderId);
        Assert.AreEqual(2, vm.ModelProviders.Count);
        Assert.IsTrue(vm.ModelProviders.Any(provider => provider.Id == "openai"));
        Assert.IsTrue(vm.ModelProviders.Any(provider => provider.Id == "azure"));
        Assert.IsFalse(vm.ShowModelProviderStatus);
    }

    [TestMethod]
    public async Task ChatViewModel_InitialProviderCatalogLoadsModelsOnlyOnce()
    {
        var bridge = new FakeWorkerBridge
        {
            ModelProvidersResult = ProviderCatalog(defaultProviderId: "openai"),
            ListModelsHandler = (request, _) => Task.FromResult(new ListModelsResult
            {
                Models = [new ModelInfo { Id = $"{request.ModelProviderId}-model" }],
                DefaultModel = $"{request.ModelProviderId}-model",
            }),
        };
        using var vm = new ChatViewModel(bridge, autoConnect: false);

        await vm.PopulateModelsAsync();

        Assert.AreEqual(1, bridge.ListModelProvidersCallCount);
        Assert.AreEqual(1, bridge.ModelListCallCount);
        Assert.AreEqual("openai", bridge.LastListModelsRequest?.ModelProviderId);
    }

    [TestMethod]
    public async Task ChatViewModel_SuccessfulModelReadDoesNotClearProviderCatalogFailure()
    {
        var bridge = new FakeWorkerBridge
        {
            ListModelProvidersHandler = (_, _) => Task.FromException<ListModelProvidersResult>(new InvalidOperationException("catalog unavailable")),
            ModelListResult = new ListModelsResult
            {
                Models = [new ModelInfo { Id = "gpt-5" }],
                DefaultModel = "gpt-5",
            },
        };
        using var vm = new ChatViewModel(bridge, autoConnect: false);

        await vm.PopulateModelsAsync();

        Assert.AreEqual(1, bridge.ModelListCallCount);
        Assert.IsTrue(vm.Models.Contains("gpt-5"));
        Assert.AreEqual(LocalizedStrings.Get("Provider.ModelsUnavailable"), vm.ModelProviderStatusText);
    }

    [TestMethod]
    public async Task ChatViewModel_NewThreadProviderRejectionFailsTrackedMutationAndKeepsGuidance()
    {
        var bridge = new FakeWorkerBridge
        {
            ModelProvidersResult = ProviderCatalog(defaultProviderId: "openai"),
            StartThreadHandler = (_, _) => Task.FromException<ThreadSummary>(
                new RemoteInvocationException(
                    "provider response included sensitive text",
                    WorkerErrorCodes.UpstreamOperationFailed,
                    ProviderErrorCodes.ModelProviderNotConfigured)),
        };
        using var vm = new ChatViewModel(bridge, autoConnect: false);
        await vm.PopulateModelsAsync();
        await bridge.PublishStateAsync(new WorkerStatus { State = WorkerConnectionState.Ready });
        vm.SelectedModelProviderId = "azure";
        await WaitForAsync(() => bridge.LastListModelsRequest?.ModelProviderId == "azure");

        await RunCommandAsync(vm.NewThreadCommand);

        Assert.AreEqual(1, bridge.StartThreadCallCount);
        Assert.IsNull(vm.SelectedThread);
        Assert.AreEqual(LocalizedStrings.Get("Provider.Error.NotConfigured"), vm.ModelProviderStatusText);
        Assert.IsFalse(vm.ModelProviderStatusText.Contains("sensitive", StringComparison.Ordinal));
        Assert.IsFalse(vm.RecoveryStatusText.Contains("provider response included sensitive text", StringComparison.Ordinal));
        Assert.IsFalse(vm.Items.Any(item => item.Text.Contains("provider response included sensitive text", StringComparison.Ordinal)));
    }

    [TestMethod]
    public async Task ChatViewModel_NewThreadUnknownProviderErrorShowsSanitizedDetailsAndUnknownOutcome()
    {
        const string message = "Provider request failed at [endpoint redacted].";
        var bridge = new FakeWorkerBridge
        {
            ModelProvidersResult = ProviderCatalog(defaultProviderId: "openai"),
            StartThreadHandler = (_, _) => Task.FromException<ThreadSummary>(
                new RemoteInvocationException(message, WorkerErrorCodes.UpstreamOperationFailed, errorData: null)),
        };
        using var vm = new ChatViewModel(bridge, autoConnect: false);
        await vm.PopulateModelsAsync();
        await bridge.PublishStateAsync(new WorkerStatus { State = WorkerConnectionState.Ready });
        vm.SelectedModelProviderId = "azure";
        await WaitForAsync(() => bridge.LastListModelsRequest?.ModelProviderId == "azure");

        await RunCommandAsync(vm.NewThreadCommand);

        Assert.AreEqual(1, bridge.StartThreadCallCount);
        Assert.IsNull(vm.SelectedThread);
        Assert.AreEqual(message, vm.ModelProviderStatusText);
        StringAssert.Contains(vm.RecoveryStatusText, "outcome is unknown");
    }

    [TestMethod]
    public async Task ChatViewModel_NewThreadCapturesProviderBeforeTrackedMutationUiHopCompletes()
    {
        var mutationUiHopStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseMutationUiHop = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var startRequestReceived = new TaskCompletionSource<StartThreadRequest>(TaskCreationOptions.RunContinuationsAsynchronously);
        var startThreadResponse = new TaskCompletionSource<ThreadSummary>(TaskCreationOptions.RunContinuationsAsynchronously);
        var bridge = new FakeWorkerBridge
        {
            ModelProvidersResult = ProviderCatalog(defaultProviderId: "openai"),
            StartThreadHandler = (request, _) =>
            {
                startRequestReceived.TrySetResult(request);
#pragma warning disable VSTHRD003 // The test controls the response to keep thread creation in flight.
                return startThreadResponse.Task;
#pragma warning restore VSTHRD003
            },
        };
        Func<Action, Task> trackedMutationUiAsync = async action =>
        {
            mutationUiHopStarted.TrySetResult();
            await releaseMutationUiHop.Task.WaitAsync(TimeSpan.FromSeconds(5));
            action();
        };
        using var vm = new ChatViewModel(bridge, autoConnect: false, trackedMutationUiAsync: trackedMutationUiAsync);
        await vm.PopulateModelsAsync();
        await bridge.PublishStateAsync(new WorkerStatus { State = WorkerConnectionState.Ready });

        vm.SelectedModelProviderId = "azure";
        await WaitForAsync(() => bridge.LastListModelsRequest?.ModelProviderId == "azure");

        Task starting = RunCommandAsync(vm.NewThreadCommand);
        try
        {
            await mutationUiHopStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.IsNull(bridge.LastStartThreadRequest, "The tracked mutation must still be waiting before its RPC dispatch.");

            vm.SelectedModelProviderId = "openai";
            releaseMutationUiHop.TrySetResult();
            StartThreadRequest request = await startRequestReceived.Task.WaitAsync(TimeSpan.FromSeconds(5));
            startThreadResponse.TrySetResult(new ThreadSummary { Id = "thread-azure", ModelProviderId = request.ModelProviderId });
#pragma warning disable VSTHRD003 // The test awaits the specific NewThread command it started above.
            await starting.WaitAsync(TimeSpan.FromSeconds(5));
#pragma warning restore VSTHRD003

            Assert.AreEqual("azure", request.ModelProviderId);
            Assert.AreEqual("openai", vm.SelectedModelProviderId, "Changing the picker while the tracked mutation waits applies to future conversations.");
            Assert.AreEqual("azure", vm.SelectedThread?.ModelProviderId, "The conversation keeps the provider selected when its start command began.");
        }
        finally
        {
            releaseMutationUiHop.TrySetResult();
            startThreadResponse.TrySetResult(new ThreadSummary { Id = "thread-azure", ModelProviderId = "azure" });
        }
    }

    [TestMethod]
    public async Task ChatViewModel_NewThreadKeepsProviderCapturedWhenSelectionChangesDuringStart()
    {
        var startRequestReceived = new TaskCompletionSource<StartThreadRequest>(TaskCreationOptions.RunContinuationsAsynchronously);
        var startThreadResponse = new TaskCompletionSource<ThreadSummary>(TaskCreationOptions.RunContinuationsAsynchronously);
        var bridge = new FakeWorkerBridge
        {
            ModelProvidersResult = ProviderCatalog(defaultProviderId: "openai"),
            StartThreadHandler = (request, _) =>
            {
                startRequestReceived.TrySetResult(request);
#pragma warning disable VSTHRD003 // The test controls the response to keep thread creation in flight.
                return startThreadResponse.Task;
#pragma warning restore VSTHRD003
            },
        };
        using var vm = new ChatViewModel(bridge, autoConnect: false);
        await vm.PopulateModelsAsync();
        await bridge.PublishStateAsync(new WorkerStatus { State = WorkerConnectionState.Ready });

        vm.SelectedModelProviderId = "azure";
        await WaitForAsync(() => bridge.LastListModelsRequest?.ModelProviderId == "azure");

        Task starting = RunCommandAsync(vm.NewThreadCommand);
        StartThreadRequest request = await startRequestReceived.Task.WaitAsync(TimeSpan.FromSeconds(5));
        vm.SelectedModelProviderId = "openai";
        startThreadResponse.SetResult(new ThreadSummary { Id = "thread-azure", ModelProviderId = request.ModelProviderId });
#pragma warning disable VSTHRD003 // The test awaits the specific NewThread command it started above.
        await starting.WaitAsync(TimeSpan.FromSeconds(5));
#pragma warning restore VSTHRD003

        Assert.AreEqual("azure", request.ModelProviderId);
        Assert.AreEqual("openai", vm.SelectedModelProviderId, "Changing the picker while the request is in flight applies to future conversations.");
        Assert.AreEqual("azure", vm.SelectedThread?.ModelProviderId, "The conversation keeps the provider selected when its start command began.");
    }
    [TestMethod]
    public async Task ChatViewModel_StaleProviderCatalogResponseDoesNotOverwriteNewerCatalog()
    {
        var firstResponse = new TaskCompletionSource<ListModelProvidersResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        int catalogCalls = 0;
        var bridge = new FakeWorkerBridge
        {
            ListModelProvidersHandler = (_, _) => Interlocked.Increment(ref catalogCalls) == 1
                ? firstResponse.Task
                : Task.FromResult(ProviderCatalog(defaultProviderId: "azure")),
        };
        using var vm = new ChatViewModel(bridge, autoConnect: false);

        Task firstPopulation = vm.PopulateModelsAsync();
        await WaitForAsync(() => Volatile.Read(ref catalogCalls) == 1);
        Task secondPopulation = vm.PopulateModelsAsync();
        await WaitForAsync(() => Volatile.Read(ref catalogCalls) == 2);
        await secondPopulation;

        firstResponse.SetResult(ProviderCatalog(defaultProviderId: "openai"));
        await firstPopulation;

        Assert.AreEqual("azure", vm.ModelProviders.Single(provider => provider.IsDefault).Id);
    }

    [TestMethod]
    public async Task ChatViewModel_SetDefaultModelProviderWritesOnceAndRefreshesCatalog()
    {
        var bridge = new FakeWorkerBridge
        {
            ModelProvidersResult = ProviderCatalog(defaultProviderId: "openai"),
            SetDefaultModelProviderHandler = (_, _) => Task.FromResult(new ListModelProvidersResult
            {
                CanChangeDefault = true,
                DefaultProviderId = "azure",
                Providers =
                [
                    new ModelProviderInfo { Id = "azure", Name = "Azure", Type = "custom" },
                    new ModelProviderInfo { Id = "openai", Name = "OpenAI", Type = "openai" },
                ],
            }),
        };
        using var vm = new ChatViewModel(bridge, autoConnect: false);
        await vm.PopulateModelsAsync();

        vm.SelectedModelProviderId = "azure";
        bool sawProviderMove = false;
        bool bindingClearedSelectionDuringCatalogUpdate = false;
        vm.ModelProviders.CollectionChanged += (_, args) =>
        {
            if (args.Action == System.Collections.Specialized.NotifyCollectionChangedAction.Move)
            {
                sawProviderMove = true;
                vm.SelectedModelProviderId = null;
                bindingClearedSelectionDuringCatalogUpdate |= vm.SelectedModelProviderId is null;
            }
        };
        await RunCommandAsync(vm.SetDefaultModelProviderCommand);

        Assert.IsTrue(sawProviderMove, "The reordered result must exercise the selector's collection-move path.");
        Assert.IsFalse(bindingClearedSelectionDuringCatalogUpdate);
        Assert.AreEqual("azure", vm.SelectedModelProviderId);
        Assert.AreEqual(1, bridge.SetDefaultModelProviderCallCount, "The default action must issue one write with no retry.");
        Assert.AreEqual("azure", bridge.LastSetDefaultModelProviderRequest?.ProviderId);
        Assert.IsTrue(vm.ModelProviders.Single(provider => provider.Id == "azure").IsDefault);
        Assert.IsFalse(vm.CanSetDefaultModelProvider);
        Assert.IsTrue(vm.ShowModelProviderStatus);
    }

    [TestMethod]
    public async Task ChatViewModel_DefaultCatalogRemovingSelectedProviderRefreshesGatewayOAuth()
    {
        var bridge = new FakeWorkerBridge
        {
            ModelProvidersResult = ProviderCatalog(defaultProviderId: "openai"),
            ListModelsHandler = (request, _) => Task.FromResult(new ListModelsResult
            {
                Models = [new ModelInfo { Id = $"{request.ModelProviderId}-model" }],
                DefaultModel = $"{request.ModelProviderId}-model",
            }),
            ReadGatewayOAuthHandler = (request, _) => Task.FromResult(new InteractionAuthStatus
            {
                IsLocal = true,
                IsSupported = true,
                ModelProviderId = request.ProviderId,
                Message = $"OAuth status for {request.ProviderId}",
                State = InteractionAuthState.Authenticated,
            }),
            SetDefaultModelProviderHandler = (_, _) => Task.FromResult(new ListModelProvidersResult
            {
                CanChangeDefault = true,
                DefaultProviderId = "openai",
                Providers = [new ModelProviderInfo { Id = "openai", Name = "OpenAI", Type = "openai" }],
            }),
        };
        using var vm = new ChatViewModel(bridge, autoConnect: false);
        await vm.PopulateModelsAsync();
        await WaitForAsync(() => vm.InteractionAuthStatus?.StatusText == "OAuth status for openai");
        vm.SelectedModelProviderId = "azure";
        await WaitForAsync(() => vm.InteractionAuthStatus?.StatusText == "OAuth status for azure");
        int readsBeforeDefaultChange = bridge.ReadGatewayOAuthCallCount;

        await RunCommandAsync(vm.SetDefaultModelProviderCommand);

        await WaitForAsync(() => bridge.ReadGatewayOAuthCallCount > readsBeforeDefaultChange
            && bridge.LastReadGatewayOAuthRequest?.ProviderId == "openai"
            && vm.InteractionAuthStatus?.StatusText == "OAuth status for openai");

        Assert.AreEqual("openai", vm.SelectedModelProviderId);
        Assert.AreEqual("OAuth status for openai", vm.InteractionAuthStatus?.StatusText);
    }

    [TestMethod]
    public async Task ChatViewModel_NullDefaultProviderRefreshPreservesCatalogAndReportsRefreshFailure()
    {
        var bridge = new FakeWorkerBridge
        {
            ModelProvidersResult = ProviderCatalog(defaultProviderId: "openai"),
            SetDefaultModelProviderHandler = (_, _) => Task.FromResult(new ListModelProvidersResult
            {
                CanChangeDefault = true,
                DefaultProviderId = "azure",
                Providers = null!,
            }),
        };
        using var vm = new ChatViewModel(bridge, autoConnect: false);
        await vm.PopulateModelsAsync();
        vm.SelectedModelProviderId = "azure";

        await RunCommandAsync(vm.SetDefaultModelProviderCommand);

        Assert.AreEqual(2, vm.ModelProviders.Count);
        Assert.IsTrue(vm.ModelProviders.Any(provider => provider.Id == "openai"));
        Assert.IsTrue(vm.ModelProviders.Any(provider => provider.Id == "azure"));
        Assert.AreEqual(LocalizedStrings.Get("Provider.DefaultRefreshFailed"), vm.ModelProviderStatusText);
        Assert.AreNotEqual(LocalizedStrings.Get("Provider.DefaultWriteOutcomeUnknown"), vm.ModelProviderStatusText);
    }

    [TestMethod]
    public async Task ChatViewModel_SetDefaultModelProviderFailureKeepsCatalogAndDoesNotRetry()
    {
        const string secret = "secret-provider-write";
        var bridge = new FakeWorkerBridge
        {
            ModelProvidersResult = ProviderCatalog(defaultProviderId: "openai"),
            SetDefaultModelProviderHandler = (_, _) => Task.FromException<ListModelProvidersResult>(
                new RemoteInvocationException(
                    $"Bearer {secret}",
                    WorkerErrorCodes.UpstreamOperationRejected,
                    "setDefaultRejected")),
        };
        using var vm = new ChatViewModel(bridge, autoConnect: false);
        await vm.PopulateModelsAsync();
        vm.SelectedModelProviderId = "azure";

        await RunCommandAsync(vm.SetDefaultModelProviderCommand);

        Assert.AreEqual(1, bridge.SetDefaultModelProviderCallCount, "A rejected write is definite and must not be retried.");
        Assert.IsTrue(vm.ModelProviders.Single(provider => provider.Id == "openai").IsDefault);
        Assert.IsFalse(vm.ModelProviders.Single(provider => provider.Id == "azure").IsDefault);
        Assert.IsTrue(vm.ShowModelProviderStatus);
        Assert.AreEqual(LocalizedStrings.Get("Provider.DefaultWriteFailed"), vm.ModelProviderStatusText);
        Assert.IsFalse(vm.ModelProviderStatusText.Contains(secret, StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task ChatViewModel_UnknownRemoteProviderFailureUsesTheConfirmedRefreshedDefault()
    {
        int catalogCalls = 0;
        var bridge = new FakeWorkerBridge
        {
            ListModelProvidersHandler = (_, _) => Task.FromResult(ProviderCatalog(
                Interlocked.Increment(ref catalogCalls) == 1 ? "openai" : "azure")),
            SetDefaultModelProviderHandler = (_, _) => Task.FromException<ListModelProvidersResult>(
                new RemoteInvocationException(
                    "provider response was lost",
                    WorkerErrorCodes.UpstreamOperationFailed,
                    "connectionLost")),
        };
        using var vm = new ChatViewModel(bridge, autoConnect: false);
        await vm.PopulateModelsAsync();
        vm.SelectedModelProviderId = "azure";

        await RunCommandAsync(vm.SetDefaultModelProviderCommand);

        Assert.AreEqual(1, bridge.SetDefaultModelProviderCallCount, "An unknown write outcome must not be retried.");
        Assert.IsTrue(vm.ModelProviders.Single(provider => provider.Id == "azure").IsDefault);
        Assert.AreEqual(LocalizedStrings.Get("Provider.DefaultChanged"), vm.ModelProviderStatusText);
    }

    [TestMethod]
    public async Task ChatViewModel_DefaultProviderUnknownOutcomeUsesTheConfirmedCatalog()
    {
        var bridge = new FakeWorkerBridge
        {
            ModelProvidersResult = ProviderCatalog(defaultProviderId: "openai"),
            SetDefaultModelProviderHandler = (_, _) => Task.FromResult(new ListModelProvidersResult
            {
                CanChangeDefault = true,
                DefaultProviderId = "azure",
                DefaultWriteOutcomeUnknown = true,
                Providers = ProviderCatalog("azure").Providers,
            }),
        };
        using var vm = new ChatViewModel(bridge, autoConnect: false);
        await vm.PopulateModelsAsync();
        vm.SelectedModelProviderId = "azure";

        await RunCommandAsync(vm.SetDefaultModelProviderCommand);

        Assert.AreEqual(1, bridge.SetDefaultModelProviderCallCount);
        Assert.AreEqual("azure", vm.ModelProviders.Single(provider => provider.IsDefault).Id);
        Assert.AreEqual(LocalizedStrings.Get("Provider.DefaultChanged"), vm.ModelProviderStatusText);
    }

    [TestMethod]
    public async Task ChatViewModel_DefaultCatalogRefreshFailureShowsKnownChangedDefault()
    {
        var bridge = new FakeWorkerBridge
        {
            ModelProvidersResult = ProviderCatalog(defaultProviderId: "openai"),
            SetDefaultModelProviderHandler = (_, _) => Task.FromResult(new ListModelProvidersResult
            {
                CanChangeDefault = true,
                DefaultProviderId = "azure",
                DefaultCatalogRefreshFailed = true,
                Providers = ProviderCatalog("openai").Providers,
            }),
        };
        using var vm = new ChatViewModel(bridge, autoConnect: false);
        await vm.PopulateModelsAsync();
        vm.SelectedModelProviderId = "azure";

        await RunCommandAsync(vm.SetDefaultModelProviderCommand);

        Assert.AreEqual(1, bridge.SetDefaultModelProviderCallCount);
        Assert.AreEqual("azure", vm.ModelProviders.Single(provider => provider.IsDefault).Id);
        Assert.AreEqual(LocalizedStrings.Get("Provider.DefaultRefreshFailed"), vm.ModelProviderStatusText);
    }

    [TestMethod]
    public async Task ChatViewModel_UnknownWriteWithEmptyCatalogPreservesTheLastUsableSelector()
    {
        int catalogCalls = 0;
        var bridge = new FakeWorkerBridge
        {
            ListModelProvidersHandler = (_, _) => Interlocked.Increment(ref catalogCalls) == 1
                ? Task.FromResult(ProviderCatalog(defaultProviderId: "openai"))
                : Task.FromResult(ProviderCatalog(defaultProviderId: "azure")),
            SetDefaultModelProviderHandler = (_, _) => Task.FromResult(new ListModelProvidersResult
            {
                CanChangeDefault = true,
                DefaultWriteOutcomeUnknown = true,
                Providers = Array.Empty<ModelProviderInfo>(),
            }),
        };
        using var vm = new ChatViewModel(bridge, autoConnect: false);
        await vm.PopulateModelsAsync();
        vm.SelectedModelProviderId = "azure";

        await RunCommandAsync(vm.SetDefaultModelProviderCommand);

        Assert.AreEqual(2, vm.ModelProviders.Count);
        Assert.AreEqual("azure", vm.SelectedModelProviderId);
        Assert.AreEqual(2, bridge.ListModelProvidersCallCount, "An empty uncertain result must trigger a fresh provider catalog read.");
        Assert.IsTrue(vm.ModelProviders.Single(provider => provider.Id == "azure").IsDefault);
        Assert.AreEqual(LocalizedStrings.Get("Provider.DefaultChanged"), vm.ModelProviderStatusText);
    }

    [TestMethod]
    public async Task ChatViewModel_UnknownWriteWithEmptyCatalogKeepsUnknownGuidanceWhenRefreshDoesNotConfirmDefault()
    {
        int catalogCalls = 0;
        var bridge = new FakeWorkerBridge
        {
            ListModelProvidersHandler = (_, _) =>
            {
                Interlocked.Increment(ref catalogCalls);
                return Task.FromResult(ProviderCatalog(defaultProviderId: "openai"));
            },
            SetDefaultModelProviderHandler = (_, _) => Task.FromResult(new ListModelProvidersResult
            {
                CanChangeDefault = true,
                DefaultWriteOutcomeUnknown = true,
                Providers = Array.Empty<ModelProviderInfo>(),
            }),
        };
        using var vm = new ChatViewModel(bridge, autoConnect: false);
        await vm.PopulateModelsAsync();
        vm.SelectedModelProviderId = "azure";

        await RunCommandAsync(vm.SetDefaultModelProviderCommand);

        Assert.AreEqual(2, bridge.ListModelProvidersCallCount);
        Assert.AreEqual("openai", vm.ModelProviders.Single(provider => provider.IsDefault).Id);
        Assert.AreEqual(LocalizedStrings.Get("Provider.DefaultWriteOutcomeUnknown"), vm.ModelProviderStatusText);
    }

    [TestMethod]
    public async Task ChatViewModel_DefaultWriteDoesNotReportSuccessWhenRefreshedCatalogDisagrees()
    {
        var bridge = new FakeWorkerBridge
        {
            ModelProvidersResult = ProviderCatalog(defaultProviderId: "openai"),
            SetDefaultModelProviderHandler = (_, _) => Task.FromResult(ProviderCatalog(defaultProviderId: "openai")),
        };
        using var vm = new ChatViewModel(bridge, autoConnect: false);
        await vm.PopulateModelsAsync();
        vm.SelectedModelProviderId = "azure";

        await RunCommandAsync(vm.SetDefaultModelProviderCommand);

        Assert.AreEqual(1, bridge.SetDefaultModelProviderCallCount);
        Assert.IsTrue(vm.ModelProviders.Single(provider => provider.Id == "openai").IsDefault);
        Assert.AreEqual(LocalizedStrings.Get("Provider.DefaultRefreshFailed"), vm.ModelProviderStatusText);
        Assert.AreNotEqual(LocalizedStrings.Get("Provider.DefaultChanged"), vm.ModelProviderStatusText);
    }

    [TestMethod]
    public async Task ChatViewModel_ChangingFutureProviderSelectionDoesNotInvalidateSavedThreadModelRead()
    {
        var modelReadStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pendingModelRead = new TaskCompletionSource<ListModelsResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var bridge = new FakeWorkerBridge
        {
            ModelProvidersResult = ProviderCatalog(defaultProviderId: "openai"),
            ListModelsHandler = (request, _) => Task.FromResult(new ListModelsResult
            {
                Models = [new ModelInfo { Id = $"{request.ModelProviderId}-model" }],
                DefaultModel = $"{request.ModelProviderId}-model",
            }),
        };
        using var vm = new ChatViewModel(bridge, autoConnect: false);
        await vm.PopulateModelsAsync();

        bridge.ListModelsHandler = (request, _) =>
        {
            if (request.ModelProviderId == "azure")
            {
                modelReadStarted.TrySetResult();
#pragma warning disable VSTHRD003 // The test controls this response to hold the in-flight model read.
                return pendingModelRead.Task;
#pragma warning restore VSTHRD003
            }

            return Task.FromResult(new ListModelsResult
            {
                Models = [new ModelInfo { Id = "openai-model" }],
                DefaultModel = "openai-model",
            });
        };

        vm.SelectedThread = new ThreadSummary { Id = "saved-thread", ModelProviderId = "azure" };
        await modelReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // The saved thread keeps using Azure while this value changes the provider for future threads.
        vm.SelectedModelProviderId = "openai";
        pendingModelRead.SetResult(new ListModelsResult
        {
            Models = [new ModelInfo { Id = "azure-model" }],
            DefaultModel = "azure-model",
        });
        await WaitForAsync(() => vm.Models.Contains("azure-model"));

        Assert.AreEqual("azure", bridge.LastListModelsRequest?.ModelProviderId);
        Assert.IsTrue(vm.Models.Contains("azure-model"));
    }

    [TestMethod]
    public async Task ChatViewModel_CatalogFallbackDoesNotReloadTheSavedThreadProvider()
    {
        var bridge = new FakeWorkerBridge
        {
            ModelProvidersResult = ProviderCatalog(defaultProviderId: "openai"),
            ListModelsHandler = (request, _) =>
            {
                string modelId = $"{request.ModelProviderId}-model";
                return Task.FromResult(new ListModelsResult { Models = [new ModelInfo { Id = modelId }], DefaultModel = modelId });
            },
        };
        using var vm = new ChatViewModel(bridge, autoConnect: false);
        vm.SelectedThread = new ThreadSummary { Id = "saved-thread", ModelProviderId = "azure" };
        await WaitForAsync(() => bridge.LastListModelsRequest?.ModelProviderId == "azure"
            && vm.Models.Contains("azure-model"));

        Assert.AreEqual("azure", bridge.LastListModelsRequest?.ModelProviderId);
        Assert.AreEqual(1, bridge.ModelListCallCount, "The automatic catalog load should fetch the saved conversation's provider models once.");
    }
    [TestMethod]
    public async Task ChatViewModel_ProviderCatalogDeduplicatesAndReconcilesItemsById()
    {
        var bridge = new FakeWorkerBridge
        {
            ModelProvidersResult = new ListModelProvidersResult
            {
                CanChangeDefault = true,
                DefaultProviderId = "openai",
                Providers =
                [
                    new ModelProviderInfo { Id = "openai", Name = "OpenAI", Type = "openai" },
                    new ModelProviderInfo { Id = "azure", Name = "Azure", Type = "custom" },
                    new ModelProviderInfo { Id = "bedrock", Name = "Bedrock", Type = "bedrock" },
                ],
            },
            SetDefaultModelProviderHandler = (_, _) => Task.FromResult(new ListModelProvidersResult
            {
                CanChangeDefault = true,
                DefaultProviderId = "azure",
                Providers =
                [
                    new ModelProviderInfo { Id = "azure", Name = "Azure Updated", Type = "custom" },
                    new ModelProviderInfo { Id = "azure", Name = "Duplicate Azure", Type = "custom" },
                    new ModelProviderInfo { Id = "openai", Name = "OpenAI Updated", Type = "openai" },
                ],
            }),
        };
        using var vm = new ChatViewModel(bridge, autoConnect: false);
        await vm.PopulateModelsAsync();
        ModelProviderInfo openAiProvider = vm.ModelProviders.Single(provider => provider.Id == "openai");
        ModelProviderInfo azureProvider = vm.ModelProviders.Single(provider => provider.Id == "azure");
        vm.SelectedModelProviderId = "azure";
        bool sawRemoval = false;
        bool bindingClearedSelectionDuringCatalogUpdate = false;
        vm.ModelProviders.CollectionChanged += (_, args) =>
        {
            if (args.Action is System.Collections.Specialized.NotifyCollectionChangedAction.Move
                or System.Collections.Specialized.NotifyCollectionChangedAction.Remove)
            {
                sawRemoval |= args.Action == System.Collections.Specialized.NotifyCollectionChangedAction.Remove;
                vm.SelectedModelProviderId = null;
                bindingClearedSelectionDuringCatalogUpdate |= vm.SelectedModelProviderId is null;
            }
        };

        await RunCommandAsync(vm.SetDefaultModelProviderCommand);

        Assert.IsTrue(sawRemoval, "The stale third provider must be removed from the reconciled catalog.");
        Assert.IsFalse(bindingClearedSelectionDuringCatalogUpdate);
        Assert.AreEqual(2, vm.ModelProviders.Count);
        Assert.AreEqual("azure", vm.ModelProviders[0].Id);
        Assert.AreEqual("openai", vm.ModelProviders[1].Id);
        Assert.AreSame(azureProvider, vm.ModelProviders[0]);
        Assert.AreSame(openAiProvider, vm.ModelProviders[1]);
        Assert.AreEqual("Azure Updated", azureProvider.Name);
        Assert.AreEqual("OpenAI Updated", openAiProvider.Name);
        Assert.AreEqual("azure", vm.ModelProviders.Single(provider => provider.IsDefault).Id);
        Assert.AreEqual("azure", vm.SelectedModelProviderId);
    }

    [TestMethod]
    public async Task ChatViewModel_StaleModelListResponseDoesNotReplaceTheSelectedProviderModels()
    {
        var staleModelList = new TaskCompletionSource<ListModelsResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        int modelListCalls = 0;
        var bridge = new FakeWorkerBridge
        {
            ModelProvidersResult = ProviderCatalog(defaultProviderId: "openai"),
            ModelListResult = new ListModelsResult
            {
                Models = [new ModelInfo { Id = "openai-model" }],
                DefaultModel = "openai-model",
            },
        };
        using var vm = new ChatViewModel(bridge, autoConnect: false);
        await vm.PopulateModelsAsync();

        bridge.ListModelsHandler = (_, _) => Interlocked.Increment(ref modelListCalls) == 1
            ? staleModelList.Task
            : Task.FromResult(new ListModelsResult
            {
                Models = [new ModelInfo { Id = "azure-model" }],
                DefaultModel = "azure-model",
            });
        Task stalePopulation = vm.PopulateModelsAsync();
        await WaitForAsync(() => Volatile.Read(ref modelListCalls) == 1);

        vm.SelectedModelProviderId = "azure";
        await WaitForAsync(() =>
            Volatile.Read(ref modelListCalls) == 2
            && vm.Models.Contains("azure-model"));
        staleModelList.SetResult(new ListModelsResult
        {
            Models = [new ModelInfo { Id = "openai-model" }],
            DefaultModel = "openai-model",
        });
        await stalePopulation;

        CollectionAssert.AreEqual(AzureModelIds, vm.Models.ToArray());
        Assert.AreEqual("azure-model", vm.SelectedModel);
        Assert.AreEqual("azure", bridge.LastListModelsRequest?.ModelProviderId);
    }

    [TestMethod]
    public async Task ChatViewModel_ModelListFailureDropsModelsFromThePreviousProvider()
    {
        var bridge = new FakeWorkerBridge
        {
            ModelProvidersResult = ProviderCatalog(defaultProviderId: "openai"),
            ModelListResult = new ListModelsResult
            {
                Models = [new ModelInfo { Id = "openai-model" }],
                DefaultModel = "openai-model",
            },
        };
        using var vm = new ChatViewModel(bridge, autoConnect: false);
        await vm.PopulateModelsAsync();
        bridge.ListModelsHandler = (_, _) => Task.FromException<ListModelsResult>(new InvalidOperationException("provider model list failed"));

        vm.SelectedModelProviderId = "azure";
        await WaitForAsync(() => vm.ShowModelProviderStatus && vm.SelectedModel == "gpt-5-codex");

        CollectionAssert.AreEqual(BuiltInFallbackModelIds, vm.Models.ToArray());
        Assert.IsFalse(vm.Models.Contains("openai-model"));
    }

    [TestMethod]
    public async Task ChatViewModel_SameProviderModelListRefreshFailureKeepsLastUsableModelsAndSelection()
    {
        int modelListCalls = 0;
        var bridge = new FakeWorkerBridge
        {
            ModelProvidersResult = ProviderCatalog(defaultProviderId: "openai"),
            ListModelsHandler = (_, _) => Interlocked.Increment(ref modelListCalls) == 1
                ? Task.FromResult(new ListModelsResult
                {
                    Models = [new ModelInfo { Id = "openai-model" }],
                    DefaultModel = "openai-model",
                })
                : Task.FromException<ListModelsResult>(new InvalidOperationException("transient model list failure")),
        };
        using var vm = new ChatViewModel(bridge, autoConnect: false);
        await vm.PopulateModelsAsync();
        vm.SelectedModel = "openai-model";

        await vm.PopulateModelsAsync();

        CollectionAssert.AreEqual(OpenAiModelIds, vm.Models.ToArray());
        Assert.AreEqual("openai-model", vm.SelectedModel);
        Assert.AreEqual(LocalizedStrings.Get("Provider.ModelsUnavailable"), vm.ModelProviderStatusText);
    }

    [TestMethod]
    public async Task ChatViewModel_SwitchingProviderWithEmptyModelListKeepsBuiltInFallback()
    {
        var bridge = new FakeWorkerBridge
        {
            ModelProvidersResult = ProviderCatalog(defaultProviderId: "openai"),
            ListModelsHandler = (request, _) => request.ModelProviderId == "openai"
                ? Task.FromResult(new ListModelsResult
                {
                    Models = [new ModelInfo { Id = "openai-model" }],
                    DefaultModel = "openai-model",
                })
                : Task.FromResult(new ListModelsResult()),
        };
        using var vm = new ChatViewModel(bridge, autoConnect: false);
        await vm.PopulateModelsAsync();

        vm.SelectedModelProviderId = "azure";
        await WaitForAsync(() => bridge.LastListModelsRequest?.ModelProviderId == "azure"
            && vm.Models.SequenceEqual(BuiltInFallbackModelIds));

        CollectionAssert.AreEqual(BuiltInFallbackModelIds, vm.Models.ToArray());
        Assert.AreEqual(BuiltInFallbackModelIds[0], vm.SelectedModel);
        Assert.AreEqual("azure", bridge.LastListModelsRequest?.ModelProviderId);
    }

    [TestMethod]
    public async Task ChatViewModel_EmptyModelListClearsPreviousModelsAndSelection()
    {
        int modelListCalls = 0;
        var bridge = new FakeWorkerBridge
        {
            ModelProvidersResult = ProviderCatalog(defaultProviderId: "openai"),
            ListModelsHandler = (_, _) => Interlocked.Increment(ref modelListCalls) == 1
                ? Task.FromResult(new ListModelsResult
                {
                    Models = [new ModelInfo { Id = "openai-model" }],
                    DefaultModel = "openai-model",
                })
                : Task.FromResult(new ListModelsResult()),
        };
        using var vm = new ChatViewModel(bridge, autoConnect: false);

        await vm.PopulateModelsAsync();
        Assert.AreEqual("openai-model", vm.SelectedModel);
        Assert.IsTrue(vm.Models.Contains("openai-model"));

        await vm.PopulateModelsAsync();

        Assert.AreEqual(2, bridge.ModelListCallCount);
        Assert.AreEqual(0, vm.Models.Count);
        Assert.IsNull(vm.SelectedModel);
        Assert.AreEqual("openai", bridge.LastListModelsRequest?.ModelProviderId);
    }

    [TestMethod]
    public async Task ChatViewModel_UnprojectedRemoteModelErrorDoesNotDisplayServerMessage()
    {
        const string secret = "Bearer secret-unprojected-provider-error";
        const string endpoint = "private.provider.example.test/v1";
        var bridge = new FakeWorkerBridge
        {
            ModelProvidersResult = ProviderCatalog(defaultProviderId: "openai"),
            ListModelsHandler = (_, _) => Task.FromException<ListModelsResult>(
                new RemoteInvocationException($"{secret}; {endpoint}", -32603, errorData: null)),
        };
        using var vm = new ChatViewModel(bridge, autoConnect: false);

        await vm.PopulateModelsAsync();

        Assert.AreEqual(LocalizedStrings.Get("Provider.ModelsUnavailable"), vm.ModelProviderStatusText);
        Assert.IsFalse(vm.ModelProviderStatusText.Contains(secret, StringComparison.Ordinal));
        Assert.IsFalse(vm.ModelProviderStatusText.Contains(endpoint, StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task ChatViewModel_GatewayOAuthReadFailureShowsLocalizedSafeMessage()
    {
        const string secret = "bearer-secret-oauth-status";
        var bridge = new FakeWorkerBridge
        {
            ModelProvidersResult = ProviderCatalog(defaultProviderId: "openai"),
            ReadGatewayOAuthHandler = (_, _) => Task.FromException<InteractionAuthStatus>(
                new RemoteInvocationException(secret, WorkerErrorCodes.UpstreamOperationFailed, errorData: null)),
        };
        using var vm = new ChatViewModel(bridge, autoConnect: false);

        await vm.PopulateModelsAsync();
        await WaitForAsync(() => vm.InteractionAuthStatus?.State == InteractionAuthState.Failed);

        Assert.IsNotNull(vm.InteractionAuthStatus);
        Assert.AreEqual(LocalizedStrings.Get("Provider.GatewayOAuthReadFailed"), vm.InteractionAuthStatus.StatusText);
        Assert.IsFalse(vm.InteractionAuthStatus.StatusText.Contains(secret, StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task ChatViewModel_GatewayOAuthLoginFailureRetainsProviderScopeAndRetrySupport()
    {
        const string secret = "bearer-secret-oauth-login";
        int loginCalls = 0;
        var bridge = new FakeWorkerBridge
        {
            ModelProvidersResult = ProviderCatalog(defaultProviderId: "openai"),
            LoginGatewayOAuthHandler = (_, _) =>
            {
                Interlocked.Increment(ref loginCalls);
                return Task.FromException<InteractionAuthStatus>(new InvalidOperationException(secret));
            },
        };
        using var vm = new ChatViewModel(bridge, autoConnect: false);
        await vm.PopulateModelsAsync();
        await bridge.PublishStateAsync(new WorkerStatus { State = WorkerConnectionState.Ready });
        int previousStatusReads = bridge.ReadGatewayOAuthCallCount;
        bridge.ReadGatewayOAuthHandler = (request, _) => Task.FromResult(new InteractionAuthStatus
        {
            IsLocal = true,
            IsSupported = true,
            ModelProviderId = request.ProviderId,
            State = InteractionAuthState.Unavailable,
        });

        vm.SelectedModelProviderId = "azure";
        await WaitForAsync(() =>
            bridge.ReadGatewayOAuthCallCount > previousStatusReads
            && vm.ShowGatewayOAuthLogin);
        await RunCommandAsync(vm.LoginGatewayOAuthCommand);

        Assert.AreEqual(1, Volatile.Read(ref loginCalls));
        Assert.AreEqual("azure", bridge.LastReadGatewayOAuthRequest?.ProviderId);
        Assert.AreEqual("azure", bridge.LastLoginGatewayOAuthRequest?.ProviderId);
        Assert.IsTrue(vm.ShowGatewayOAuthLogin, "A failed provider-scoped login should preserve the selected provider scope and remain retryable.");
        Assert.IsNotNull(vm.InteractionAuthStatus, "A login failure should expose safe retry guidance.");
        Assert.AreEqual(LocalizedStrings.Get("Provider.GatewayOAuthOperationFailed"), vm.InteractionAuthStatus.StatusText);
        Assert.IsFalse(vm.InteractionAuthStatus.StatusText.Contains(secret, StringComparison.Ordinal));

        await RunCommandAsync(vm.LoginGatewayOAuthCommand);

        Assert.AreEqual(2, Volatile.Read(ref loginCalls), "The failed scoped login remains available for an explicit retry.");
        Assert.AreEqual("azure", bridge.LastLoginGatewayOAuthRequest?.ProviderId);
    }

    [TestMethod]
    public async Task ChatViewModel_StaleProviderCatalogFailureDoesNotReplaceTheCurrentStatus()
    {
        var staleCatalogStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var staleCatalog = new TaskCompletionSource<ListModelProvidersResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        int catalogCalls = 0;
        var bridge = new FakeWorkerBridge
        {
            ListModelProvidersHandler = (_, _) =>
            {
                if (Interlocked.Increment(ref catalogCalls) == 1)
                {
                    staleCatalogStarted.TrySetResult();
                    return staleCatalog.Task.ContinueWith(static completed => completed.GetAwaiter().GetResult(), CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                }

                return Task.FromResult(ProviderCatalog(defaultProviderId: "azure"));
            },
        };
        using var vm = new ChatViewModel(bridge, autoConnect: false);

        Task stalePopulation = vm.PopulateModelsAsync();
        await staleCatalogStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await vm.PopulateModelsAsync();
        staleCatalog.SetException(new InvalidOperationException("stale provider catalog failed"));
        await stalePopulation;

        Assert.IsFalse(vm.ShowModelProviderStatus, "A stale catalog failure must not overwrite the current provider catalog state.");
        Assert.IsTrue(vm.ModelProviders.Single(provider => provider.IsDefault).Id == "azure");
    }

    [TestMethod]
    public async Task ChatViewModel_OwnerChangeSuppressesTwoWaySelectionWritebackDuringCatalogClear()
    {
        var bridge = new FakeWorkerBridge
        {
            ModelProvidersResult = ProviderCatalog(defaultProviderId: "openai"),
        };
        using var vm = new ChatViewModel(bridge, autoConnect: false);
        await bridge.PublishStateAsync(OwnerStatus("provider-owner-a", 1, 1));
        await vm.PopulateModelsAsync();
        vm.SelectedModelProviderId = "azure";
        bool bindingWroteNullDuringReset = false;
        vm.ModelProviders.CollectionChanged += (_, args) =>
        {
            if (args.Action == System.Collections.Specialized.NotifyCollectionChangedAction.Reset)
            {
                vm.SelectedModelProviderId = null;
                bindingWroteNullDuringReset = vm.SelectedModelProviderId is null;
            }
        };

        await bridge.PublishStateAsync(OwnerStatus("provider-owner-b", 2, 2));

        Assert.IsFalse(bindingWroteNullDuringReset, "The owner clear must ignore the ComboBox's transient null selection writeback.");
        Assert.IsNull(vm.SelectedModelProviderId);
        Assert.AreEqual(0, vm.ModelProviders.Count);
    }

    [TestMethod]
    public async Task ChatViewModel_ProviderRemoteFailureDisplaysSanitizedServerGuidanceAsSafeText()
    {
        var bridge = new FakeWorkerBridge
        {
            ModelProvidersResult = ProviderCatalog(defaultProviderId: "openai"),
            ListModelsHandler = (_, _) => Task.FromException<ListModelsResult>(
                new RemoteInvocationException("**Model request failed** at [endpoint redacted].", WorkerErrorCodes.UpstreamOperationFailed, errorData: null)),
        };
        using var vm = new ChatViewModel(bridge, autoConnect: false);
        await vm.PopulateModelsAsync();
        vm.SelectedModelProviderId = "azure";
        await WaitForAsync(() => vm.ShowModelProviderStatus);

        Assert.AreEqual("Model request failed at [endpoint redacted].", vm.ModelProviderStatusText);
    }

    [TestMethod]
    public async Task ChatViewModel_ModelProviderCatalogRejectsUnsafeIdentifiersAndCredentialUrls()
    {
        var bridge = new FakeWorkerBridge
        {
            ModelProvidersResult = new ListModelProvidersResult
            {
                CanChangeDefault = true,
                DefaultProviderId = "openai",
                Providers =
                [
                    new ModelProviderInfo
                    {
                        Id = "openai",
                        Name = "OpenAI",
                        BaseUrl = "https://user:secret@api.example.test/v1",
                        Type = "openai",
                    },
                    new ModelProviderInfo
                    {
                        Id = "unsafe\r\nprovider",
                        Name = "Untrusted provider",
                        BaseUrl = "https://api.example.test/v1",
                        Type = "custom",
                    },
                ],
            },
        };
        using var vm = new ChatViewModel(bridge, autoConnect: false);

        await vm.PopulateModelsAsync();

        ModelProviderInfo provider = vm.ModelProviders.Single();
        Assert.AreEqual("openai", provider.Id);
        Assert.IsNull(provider.BaseUrl);
        Assert.IsFalse(vm.ModelProviders.Any(value => value.Id.Contains('\r') || value.Id.Contains('\n')));
    }

    private static ListModelProvidersResult ProviderCatalog(string defaultProviderId)
        => new()
        {
            CanChangeDefault = true,
            DefaultProviderId = defaultProviderId,
            Providers =
            [
                new ModelProviderInfo { Id = "openai", Name = "OpenAI", Type = "openai" },
                new ModelProviderInfo { Id = "azure", Name = "Azure", Type = "custom" },
            ],
        };
}
