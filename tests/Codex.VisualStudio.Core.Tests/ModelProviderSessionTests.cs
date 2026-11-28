using System.Text.Json;
using Codex.AppServer.Protocol;
using Codex.VisualStudio.Contracts;
using Codex.VisualStudio.Worker;

namespace Codex.VisualStudio.Core.Tests;

public sealed partial class CodexSessionServiceTests
{
    [TestMethod]
    public void ModelProviderInfo_RemoteUiContractRoundTripsDerivedPropertiesInTheReceivingCulture()
    {
        System.Globalization.CultureInfo originalCulture = System.Globalization.CultureInfo.CurrentUICulture;
        try
        {
            System.Globalization.CultureInfo.CurrentUICulture = System.Globalization.CultureInfo.GetCultureInfo("en-US");
            var provider = new ModelProviderInfo
            {
                Id = "openai",
                Name = "OpenAI",
                BaseUrl = "https://api.example.test/v1",
                Type = "openai",
                IsDefault = true,
            };
            var serializer = new System.Runtime.Serialization.DataContractSerializer(typeof(ModelProviderInfo));
            using var stream = new System.IO.MemoryStream();
            serializer.WriteObject(stream, provider);

            System.Globalization.CultureInfo.CurrentUICulture = System.Globalization.CultureInfo.GetCultureInfo("ja-JP");
            stream.Position = 0;
            var roundTripped = (ModelProviderInfo)serializer.ReadObject(stream)!;

            Assert.AreEqual("openai", roundTripped.Id);
            Assert.AreEqual("OpenAI (既定)", roundTripped.DisplayName);
            Assert.AreEqual("OpenAI", roundTripped.DisplayType);
            Assert.IsTrue(roundTripped.HasBaseUrl);
            Assert.IsTrue(roundTripped.HasDisplayType);
        }
        finally
        {
            System.Globalization.CultureInfo.CurrentUICulture = originalCulture;
        }
    }

    [TestMethod]
    public async Task StartThreadWithoutExplicitProviderPreservesDefaultProviderFlow()
    {
        var connection = new RecordingConnection
        {
            Handler = (method, _) => method switch
            {
                "initialize" => JsonSerializer.SerializeToElement(new { userAgent = "codex-cli/0.169.0" }),
                "thread/start" => JsonSerializer.SerializeToElement(new { thread = new { id = "new-thread" } }),
                _ => JsonSerializer.SerializeToElement(new { }),
            },
        };
        await using var service = CreateService();
        await service.InitializeAsync(connection, Options(), CancellationToken.None);
        int gatewayReadsBeforeStart = connection.Requests.Count(request => request.Method == "account/gatewayOAuth/read");

        _ = await service.StartThreadAsync(CancellationToken.None);

        Assert.AreEqual(gatewayReadsBeforeStart, connection.Requests.Count(request => request.Method == "account/gatewayOAuth/read"));
        Assert.IsFalse(connection.Requests.Any(request => request.Method == "modelProvider/list"));
        Assert.IsFalse(JsonSerializer.Serialize(connection.Requests.Single(request => request.Method == "thread/start").Parameters).Contains("modelProvider", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task DefaultThreadStartDoesNotReuseReadyStatusFromScopedProvider()
    {
        int defaultReadCount = 0;
        var connection = new RecordingConnection
        {
            Handler = (method, _) => method switch
            {
                "initialize" => JsonSerializer.SerializeToElement(new { userAgent = "codex-cli/0.169.0" }),
                "thread/start" => JsonSerializer.SerializeToElement(new { thread = new { id = "new-thread" } }),
                _ => JsonSerializer.SerializeToElement(new { }),
            },
        };
        connection.GatewayOAuthReadHandler = (parameters, _) =>
        {
            JsonElement request = JsonSerializer.SerializeToElement(parameters);
            if (request.TryGetProperty("modelProvider", out JsonElement provider)
                && string.Equals(provider.GetString(), "azure", StringComparison.Ordinal))
            {
                return Task.FromResult(JsonSerializer.SerializeToElement(new
                {
                    providerId = "azure",
                    providerName = "Azure",
                    required = true,
                    status = "succeeded",
                }));
            }

            bool defaultReady = Interlocked.Increment(ref defaultReadCount) == 1;
            return Task.FromResult(JsonSerializer.SerializeToElement(new
            {
                providerId = "openai",
                providerName = "OpenAI",
                required = true,
                status = defaultReady ? "succeeded" : "notReady",
            }));
        };
        await using var service = CreateService();
        await service.InitializeAsync(connection, Options(), CancellationToken.None);
        _ = await service.ReadGatewayOAuthAsync("azure", CancellationToken.None);

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => service.StartThreadAsync(CancellationToken.None));

        Assert.AreEqual(2, defaultReadCount, "The default operation must read its own provider after the scoped Azure status changed the shared UI state.");
        Assert.IsFalse(connection.Requests.Any(request => request.Method == "thread/start"));
        RecordedRequest scopedRequest = connection.Requests.First(request =>
            request.Method == "account/gatewayOAuth/read"
            && JsonSerializer.SerializeToElement(request.Parameters).TryGetProperty("modelProvider", out _));
        JsonElement scopedParameters = JsonSerializer.SerializeToElement(scopedRequest.Parameters, WireJsonOptions);
        Assert.AreEqual("azure", scopedParameters.GetProperty("modelProvider").GetString());
        JsonElement parameters = LastParametersFor(connection, "account/gatewayOAuth/read");
        Assert.IsFalse(parameters.TryGetProperty("modelProvider", out _), "The default provider read must remain unscoped on the wire.");
    }

    [TestMethod]
    public async Task DefaultThreadStartStillWorksAfterScopedProviderReadWhenDefaultIsReady()
    {
        int defaultReadCount = 0;
        var connection = new RecordingConnection
        {
            Handler = (method, _) => method switch
            {
                "initialize" => JsonSerializer.SerializeToElement(new { userAgent = "codex-cli/0.169.0" }),
                "thread/start" => JsonSerializer.SerializeToElement(new { thread = new { id = "new-thread" } }),
                _ => JsonSerializer.SerializeToElement(new { }),
            },
        };
        connection.GatewayOAuthReadHandler = (parameters, _) =>
        {
            JsonElement request = JsonSerializer.SerializeToElement(parameters);
            bool isAzure = request.TryGetProperty("modelProvider", out JsonElement provider)
                && string.Equals(provider.GetString(), "azure", StringComparison.Ordinal);
            if (!isAzure)
            {
                Interlocked.Increment(ref defaultReadCount);
            }

            return Task.FromResult(JsonSerializer.SerializeToElement(new
            {
                providerId = isAzure ? "azure" : "openai",
                providerName = isAzure ? "Azure" : "OpenAI",
                required = true,
                status = "succeeded",
            }));
        };
        await using var service = CreateService();
        await service.InitializeAsync(connection, Options(), CancellationToken.None);
        _ = await service.ReadGatewayOAuthAsync("azure", CancellationToken.None);

        _ = await service.StartThreadAsync(CancellationToken.None);

        Assert.AreEqual(2, defaultReadCount, "The default operation should use its own successful status read instead of rejecting a provider-scope mismatch.");
        Assert.IsTrue(connection.Requests.Any(request => request.Method == "thread/start"));
    }

    [TestMethod]
    public async Task SavedProviderResumeChecksProviderScopeBeforeResuming()
    {
        var connection = new RecordingConnection
        {
            Handler = (method, _) => method switch
            {
                "initialize" => JsonSerializer.SerializeToElement(new { userAgent = "codex-cli/0.169.0" }),
                "thread/resume" => JsonSerializer.SerializeToElement(new
                {
                    thread = new { id = "saved-thread", modelProvider = new { id = "azure", name = "Azure" } },
                }),
                _ => JsonSerializer.SerializeToElement(new { }),
            },
        };
        connection.GatewayOAuthReadHandler = (parameters, _) =>
        {
            JsonElement request = JsonSerializer.SerializeToElement(parameters);
            bool isAzure = request.TryGetProperty("modelProvider", out JsonElement provider)
                && string.Equals(provider.GetString(), "azure", StringComparison.Ordinal);
            return Task.FromResult(JsonSerializer.SerializeToElement(new
            {
                providerId = isAzure ? "azure" : "openai",
                providerName = isAzure ? "Azure" : "OpenAI",
                required = true,
                status = "succeeded",
            }));
        };
        await using var service = CreateService();
        await service.InitializeAsync(connection, Options(), CancellationToken.None);
        _ = await service.ReadGatewayOAuthAsync("openai", CancellationToken.None);

        ThreadSummary resumed = await service.ResumeThreadAsync("saved-thread", "azure", CancellationToken.None);

        Assert.AreEqual("azure", resumed.ModelProviderId);
        Assert.AreEqual("azure", LastParametersFor(connection, "account/gatewayOAuth/read").GetProperty("modelProvider").GetString());
        Assert.IsFalse(JsonSerializer.Serialize(ParametersFor(connection, "thread/resume")).Contains("modelProvider", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task SavedProviderStartTurnChecksProviderScopeAndValidatesModelAgainstItsCatalog()
    {
        var connection = new RecordingConnection
        {
            Handler = (method, _) => method switch
            {
                "initialize" => JsonSerializer.SerializeToElement(new { userAgent = "codex-cli/0.169.0" }),
                "model/list" => JsonSerializer.SerializeToElement(new
                {
                    data = new[] { new { model = "azure-model", isDefault = true } },
                }),
                "turn/start" => JsonSerializer.SerializeToElement(new { turn = new { id = "turn-1" } }),
                _ => JsonSerializer.SerializeToElement(new { }),
            },
        };
        connection.GatewayOAuthReadHandler = (parameters, _) =>
        {
            JsonElement request = JsonSerializer.SerializeToElement(parameters);
            bool isAzure = request.TryGetProperty("modelProvider", out JsonElement provider)
                && string.Equals(provider.GetString(), "azure", StringComparison.Ordinal);
            return Task.FromResult(JsonSerializer.SerializeToElement(new
            {
                providerId = isAzure ? "azure" : "openai",
                providerName = isAzure ? "Azure" : "OpenAI",
                required = true,
                status = "succeeded",
            }));
        };
        await using var service = CreateService();
        await service.InitializeAsync(connection, Options(), CancellationToken.None);
        _ = await service.ReadGatewayOAuthAsync("openai", CancellationToken.None);

        _ = await service.StartTurnAsync(new StartTurnRequest
        {
            ThreadId = "saved-thread",
            ModelProviderId = "azure",
            Model = "azure-model",
            Text = "hello",
        }, CancellationToken.None);

        Assert.AreEqual("azure", LastParametersFor(connection, "account/gatewayOAuth/read").GetProperty("modelProvider").GetString());
        Assert.AreEqual("azure", ParametersFor(connection, "model/list").GetProperty("modelProvider").GetString());
        Assert.IsFalse(JsonSerializer.Serialize(ParametersFor(connection, "turn/start")).Contains("modelProvider", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task SavedProviderOperationsDoNotDispatchWhenItsGatewayOAuthIsNotReady()
    {
        var connection = new RecordingConnection
        {
            Handler = (method, _) => method switch
            {
                "initialize" => JsonSerializer.SerializeToElement(new { userAgent = "codex-cli/0.169.0" }),
                "model/list" => JsonSerializer.SerializeToElement(new
                {
                    data = new[] { new { model = "azure-model", isDefault = true } },
                }),
                _ => JsonSerializer.SerializeToElement(new { }),
            },
        };
        connection.GatewayOAuthReadHandler = (parameters, _) =>
        {
            JsonElement request = JsonSerializer.SerializeToElement(parameters);
            bool isAzure = request.ValueKind == JsonValueKind.Object
                && request.TryGetProperty("modelProvider", out JsonElement provider)
                && provider.GetString() == "azure";
            return Task.FromResult(JsonSerializer.SerializeToElement(new
            {
                providerId = isAzure ? "azure" : "openai",
                providerName = isAzure ? "Azure" : "OpenAI",
                required = isAzure,
                status = isAzure ? "notReady" : (string?)null,
            }));
        };
        await using var service = CreateService();
        await service.InitializeAsync(connection, Options(), CancellationToken.None);

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => service.ResumeThreadAsync("saved-thread", "azure", CancellationToken.None));
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => service.StartTurnAsync(new StartTurnRequest
            {
                ThreadId = "saved-thread",
                ModelProviderId = "azure",
                Model = "azure-model",
                Text = "hello",
            }, CancellationToken.None));

        Assert.IsFalse(connection.Requests.Any(request => request.Method is "thread/resume" or "model/list" or "turn/start"));
    }

    [TestMethod]
    public async Task SetDefaultModelProviderRejectsFixedDefaultsAndUnsupportedVersionsBeforeWriting()
    {
        foreach ((string version, bool canChangeDefault, bool unsupported) in new[]
        {
            ("0.169.0", false, false),
            ("0.168.0", true, true),
        })
        {
            var connection = new RecordingConnection
            {
                Handler = (method, _) => method switch
                {
                    "initialize" => JsonSerializer.SerializeToElement(new { userAgent = $"codex-cli/{version}" }),
                    "modelProvider/list" => ProviderCatalogResponse("openai", canChangeDefault),
                    _ => JsonSerializer.SerializeToElement(new { }),
                },
            };
            await using var service = CreateService();
            if (unsupported)
            {
                await Assert.ThrowsExactlyAsync<UnsupportedCodexVersionException>(
                    () => service.InitializeAsync(connection, Options(), CancellationToken.None));
                Assert.IsFalse(connection.Requests.Any(request => request.Method is "modelProvider/list" or "config/value/write" or "modelProvider/default/set"));
                continue;
            }

            await service.InitializeAsync(connection, Options(), CancellationToken.None);

            await Assert.ThrowsExactlyAsync<ModelProviderPreDispatchException>(
                () => service.SetDefaultModelProviderAsync("azure", CancellationToken.None));

            Assert.IsFalse(connection.Requests.Any(request => request.Method is "config/value/write" or "modelProvider/default/set"));
        }
    }

    [TestMethod]
    public async Task SetDefaultModelProviderRefreshesAfterUnknownWriteOutcomeWithoutRetryingMutation()
    {
        int catalogReads = 0;
        var connection = new RecordingConnection
        {
            AsyncHandler = (method, _, _) => method switch
            {
                "initialize" => Task.FromResult(JsonSerializer.SerializeToElement(new { userAgent = "codex-cli/0.169.0" })),
                "modelProvider/list" => Task.FromResult(ProviderCatalogResponse(catalogReads++ == 0 ? "openai" : "azure", canChangeDefault: true)),
                "config/value/write" => Task.FromException<JsonElement>(new IOException("write transport ended")),
                _ => Task.FromResult(JsonSerializer.SerializeToElement(new { })),
            },
        };
        await using var service = CreateService();
        await service.InitializeAsync(connection, Options(), CancellationToken.None);

        ListModelProvidersResult result = await service.SetDefaultModelProviderAsync("azure", CancellationToken.None);

        Assert.IsTrue(result.DefaultWriteOutcomeUnknown);
        Assert.IsFalse(result.DefaultCatalogRefreshFailed);
        Assert.AreEqual("azure", result.DefaultProviderId);
        Assert.AreEqual(1, connection.Requests.Count(request => request.Method == "config/value/write"));
        Assert.AreEqual(2, connection.Requests.Count(request => request.Method == "modelProvider/list"));
    }

    [TestMethod]
    public async Task SetDefaultModelProviderCancellationRefreshesTheOwnerBoundCatalogAsUnknown()
    {
        using var cancellation = new CancellationTokenSource();
        int catalogReads = 0;
        bool recoveryReadReceivedCanceledToken = true;
        var connection = new RecordingConnection
        {
            AsyncHandler = (method, _, token) => method switch
            {
                "initialize" => Task.FromResult(JsonSerializer.SerializeToElement(new { userAgent = "codex-cli/0.169.0" })),
                "modelProvider/list" => ReadCatalog(token),
                "config/value/write" => CancelWrite(cancellation),
                _ => Task.FromResult(JsonSerializer.SerializeToElement(new { })),
            },
        };
        Task<JsonElement> ReadCatalog(CancellationToken token)
        {
            int read = Interlocked.Increment(ref catalogReads);
            if (read == 2)
            {
                recoveryReadReceivedCanceledToken = token.IsCancellationRequested;
            }

            return Task.FromResult(ProviderCatalogResponse(read == 1 ? "openai" : "azure", canChangeDefault: true));
        }
        await using var service = CreateService();
        await service.InitializeAsync(connection, Options(), CancellationToken.None);

        ListModelProvidersResult result = await service.SetDefaultModelProviderAsync("azure", cancellation.Token);

        Assert.IsTrue(result.DefaultWriteOutcomeUnknown);
        Assert.AreEqual("azure", result.DefaultProviderId);
        Assert.AreEqual(1, connection.Requests.Count(request => request.Method == "config/value/write"));
        Assert.AreEqual(2, connection.Requests.Count(request => request.Method == "modelProvider/list"));
        Assert.IsFalse(recoveryReadReceivedCanceledToken, "The outcome refresh must not reuse the canceled mutation token.");
    }

    [TestMethod]
    public async Task SetDefaultModelProviderReportsKnownWriteWhenCatalogRefreshFails()
    {
        int catalogReads = 0;
        var connection = new RecordingConnection
        {
            AsyncHandler = (method, _, _) => method switch
            {
                "initialize" => Task.FromResult(JsonSerializer.SerializeToElement(new { userAgent = "codex-cli/0.169.0" })),
                "modelProvider/list" when catalogReads++ == 0 => Task.FromResult(ProviderCatalogResponse("openai", canChangeDefault: true)),
                "modelProvider/list" => Task.FromException<JsonElement>(new IOException("catalog read failed")),
                "config/value/write" => Task.FromResult(JsonSerializer.SerializeToElement(new { })),
                _ => Task.FromResult(JsonSerializer.SerializeToElement(new { })),
            },
        };
        await using var service = CreateService();
        await service.InitializeAsync(connection, Options(), CancellationToken.None);

        ListModelProvidersResult result = await service.SetDefaultModelProviderAsync("azure", CancellationToken.None);

        Assert.IsFalse(result.DefaultWriteOutcomeUnknown);
        Assert.IsTrue(result.DefaultCatalogRefreshFailed);
        Assert.AreEqual("azure", result.DefaultProviderId);
        Assert.IsTrue(result.Providers.Single(provider => provider.Id == "azure").IsDefault);
        Assert.AreEqual(1, connection.Requests.Count(request => request.Method == "config/value/write"));
    }

    [TestMethod]
    public async Task SetDefaultModelProviderUsesTypedMethodOn0171WithoutGatewayCredentials()
    {
        int catalogReads = 0;
        var connection = new RecordingConnection
        {
            AsyncHandler = (method, _, _) => method switch
            {
                "initialize" => Task.FromResult(JsonSerializer.SerializeToElement(new { userAgent = "codex-cli/0.171.0" })),
                "modelProvider/list" => Task.FromResult(ProviderCatalogResponse(
                    catalogReads++ == 0 ? "openai" : "azure",
                    canChangeDefault: true)),
                "modelProvider/default/set" => Task.FromResult(JsonSerializer.SerializeToElement(new { })),
                _ => Task.FromResult(JsonSerializer.SerializeToElement(new { })),
            },
        };
        connection.GatewayOAuthReadResponse = JsonSerializer.SerializeToElement(new
        {
            providerId = "openai",
            providerName = "OpenAI",
            required = true,
            status = "notReady",
        });
        await using var service = CreateService();
        await service.InitializeAsync(connection, Options(), CancellationToken.None);

        int gatewayOAuthRequestCountBeforeChange = connection.Requests.Count(request => request.Method is "account/gatewayOAuth/read" or "account/gatewayOAuth/login");
        ListModelProvidersResult result = await service.SetDefaultModelProviderAsync("azure", CancellationToken.None);

        Assert.AreEqual("azure", result.DefaultProviderId);
        Assert.IsTrue(result.Providers.Single(provider => provider.IsDefault).Id == "azure");
        Assert.AreEqual("azure", ParametersFor(connection, "modelProvider/default/set").GetProperty("providerId").GetString());
        Assert.AreEqual(1, connection.Requests.Count(request => request.Method == "modelProvider/default/set"));
        Assert.AreEqual(0, connection.Requests.Count(request => request.Method == "config/value/write"));
        Assert.AreEqual(gatewayOAuthRequestCountBeforeChange,
            connection.Requests.Count(request => request.Method is "account/gatewayOAuth/read" or "account/gatewayOAuth/login"),
            "Changing the default provider must not require gateway credentials.");
    }

    [TestMethod]
    public async Task SetDefaultModelProviderSerializesConcurrentMutationsAndRefreshesInOrder()
    {
        var firstWriteStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirstWrite = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        string defaultProviderId = "openai";
        int writeCount = 0;
        var connection = new RecordingConnection
        {
            AsyncHandler = (method, parameters, _) => method switch
            {
                "initialize" => Task.FromResult(JsonSerializer.SerializeToElement(new { userAgent = "codex-cli/0.169.0" })),
                "modelProvider/list" => Task.FromResult(ProviderCatalogResponse(defaultProviderId, canChangeDefault: true)),
                "config/value/write" => WriteDefault(parameters),
                _ => Task.FromResult(JsonSerializer.SerializeToElement(new { })),
            },
        };

        Task<JsonElement> WriteDefault(object? parameters)
        {
            defaultProviderId = JsonSerializer.SerializeToElement(parameters).GetProperty("value").GetString()!;
            if (Interlocked.Increment(ref writeCount) == 1)
            {
                firstWriteStarted.TrySetResult();
                return releaseFirstWrite.Task;
            }

            return Task.FromResult(JsonSerializer.SerializeToElement(new { }));
        }

        await using var service = CreateService();
        await service.InitializeAsync(connection, Options(), CancellationToken.None);

        Task<ListModelProvidersResult> first = service.SetDefaultModelProviderAsync("azure", CancellationToken.None);
        await firstWriteStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Task<ListModelProvidersResult> second = service.SetDefaultModelProviderAsync("openai", CancellationToken.None);

        Assert.IsFalse(second.IsCompleted, "The second mutation must remain blocked while the first write is unresolved.");
        Assert.AreEqual(1, Volatile.Read(ref writeCount), "The second mutation must wait for the first mutation and its catalog refresh.");
        Assert.AreEqual(1, connection.Requests.Count(request => request.Method == "modelProvider/list"));

        releaseFirstWrite.TrySetResult(JsonSerializer.SerializeToElement(new { }));
        ListModelProvidersResult firstResult = await first;
        ListModelProvidersResult secondResult = await second;

        Assert.AreEqual("azure", firstResult.DefaultProviderId);
        Assert.AreEqual("openai", secondResult.DefaultProviderId);
        Assert.AreEqual(2, Volatile.Read(ref writeCount));
        Assert.AreEqual(4, connection.Requests.Count(request => request.Method == "modelProvider/list"));
    }

    [TestMethod]
    public async Task SetDefaultModelProviderReportsTypedMethodRejectionAsDefiniteFailure()
    {
        var connection = new RecordingConnection
        {
            AsyncHandler = (method, _, _) => method switch
            {
                "initialize" => Task.FromResult(JsonSerializer.SerializeToElement(new { userAgent = "codex-cli/0.171.0" })),
                "modelProvider/list" => Task.FromResult(ProviderCatalogResponse("openai", canChangeDefault: true)),
                "modelProvider/default/set" => Task.FromException<JsonElement>(new JsonRpcRemoteException(-32000, "The server rejected the provider change.")),
                _ => Task.FromResult(JsonSerializer.SerializeToElement(new { })),
            },
        };
        await using var service = CreateService();
        await service.InitializeAsync(connection, Options(), CancellationToken.None);

        ModelProviderMutationRejectedException exception = await Assert.ThrowsExactlyAsync<ModelProviderMutationRejectedException>(
            () => service.SetDefaultModelProviderAsync("azure", CancellationToken.None));

        Assert.AreEqual(1, connection.Requests.Count(request => request.Method == "modelProvider/default/set"));
        Assert.AreEqual(0, connection.Requests.Count(request => request.Method == "config/value/write"));
        Assert.AreEqual(1, connection.Requests.Count(request => request.Method == "modelProvider/list"));
        Assert.AreEqual("The app-server rejected the default model provider change.", exception.Message);
    }

    [TestMethod]
    [DataRow("0.169.0", "config/value/write", "modelProvider/default/set")]
    [DataRow("0.170.0", "config/value/write", "modelProvider/default/set")]
    [DataRow("0.171.0", "modelProvider/default/set", "config/value/write")]
    public async Task DefaultProviderMutationDoesNotFallbackAfterMethodNotFound(string version, string expectedMethod, string fallbackMethod)
    {
        var connection = new RecordingConnection
        {
            AsyncHandler = (method, _, _) => method switch
            {
                "initialize" => Task.FromResult(JsonSerializer.SerializeToElement(new { userAgent = $"codex-cli/{version}" })),
                "modelProvider/list" => Task.FromResult(ProviderCatalogResponse("openai", canChangeDefault: true)),
                _ when method == expectedMethod => Task.FromException<JsonElement>(new JsonRpcRemoteException(-32601, "Method not found.")),
                _ => Task.FromResult(JsonSerializer.SerializeToElement(new { })),
            },
        };
        await using var service = CreateService();
        await service.InitializeAsync(connection, Options(), CancellationToken.None);

        await Assert.ThrowsExactlyAsync<ModelProviderMutationRejectedException>(
            () => service.SetDefaultModelProviderAsync("azure", CancellationToken.None));

        Assert.AreEqual(1, connection.Requests.Count(request => request.Method == expectedMethod));
        Assert.AreEqual(0, connection.Requests.Count(request => request.Method == fallbackMethod));
    }

    [TestMethod]
    public async Task ListModelProvidersIncludesEveryProviderAndAllowsSettingOneBeyondTheFormerDisplayLimit()
    {
        JsonElement[] data = Enumerable.Range(0, 130)
            .Select(index => JsonSerializer.SerializeToElement(new
            {
                id = $"provider-{index:D3}",
                name = $"Provider {index:D3}",
                settings = new { type = "custom" },
            }))
            .ToArray();
        int catalogReads = 0;
        var connection = new RecordingConnection
        {
            AsyncHandler = (method, _, _) => method switch
            {
                "initialize" => Task.FromResult(JsonSerializer.SerializeToElement(new { userAgent = "codex-cli/0.169.0" })),
                "modelProvider/list" => Task.FromResult(JsonSerializer.SerializeToElement(new
                {
                    defaultProviderId = catalogReads++ < 2 ? "provider-129" : "provider-128",
                    canChangeDefault = true,
                    data,
                })),
                _ => Task.FromResult(JsonSerializer.SerializeToElement(new { })),
            },
        };
        await using var service = CreateService();
        await service.InitializeAsync(connection, Options(), CancellationToken.None);

        ListModelProvidersResult listed = await service.ListModelProvidersAsync(CancellationToken.None);
        ListModelProvidersResult changed = await service.SetDefaultModelProviderAsync("provider-128", CancellationToken.None);

        Assert.AreEqual(130, listed.Providers.Count);
        Assert.IsTrue(listed.Providers.Any(provider => provider.Id == "provider-128"));
        Assert.AreEqual("provider-129", listed.Providers.Single(provider => provider.IsDefault).Id);
        Assert.AreEqual("provider-128", changed.DefaultProviderId);
        Assert.AreEqual("provider-128", ParametersFor(connection, "config/value/write").GetProperty("value").GetString());
        Assert.AreEqual(1, connection.Requests.Count(request => request.Method == "config/value/write"));
    }

    [TestMethod]
    public async Task GatewayOAuthReadIgnoresAStaleProviderResponse()
    {
        var openaiStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var azureStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var openaiResponse = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        var azureResponse = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        var connection = new RecordingConnection
        {
            AsyncHandler = (method, _, _) => method == "initialize"
                ? Task.FromResult(JsonSerializer.SerializeToElement(new { userAgent = "codex-cli/0.169.0" }))
                : Task.FromResult(JsonSerializer.SerializeToElement(new { })),
        };
        connection.GatewayOAuthReadHandler = (parameters, _) =>
        {
            JsonElement request = JsonSerializer.SerializeToElement(parameters);
            if (!request.TryGetProperty("modelProvider", out JsonElement providerElement))
            {
                return Task.FromResult(connection.GatewayOAuthReadResponse.Clone());
            }

            return providerElement.GetString() switch
            {
                "openai" => StartRead(openaiStarted, openaiResponse),
                "azure" => StartRead(azureStarted, azureResponse),
                _ => Task.FromResult(connection.GatewayOAuthReadResponse.Clone()),
            };
        };
        await using var service = CreateService();
        await service.InitializeAsync(connection, Options(), CancellationToken.None);

        Task<InteractionAuthStatus> openaiRead = service.ReadGatewayOAuthAsync("openai", CancellationToken.None);
        await openaiStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Task<InteractionAuthStatus> azureRead = service.ReadGatewayOAuthAsync("azure", CancellationToken.None);
        await azureStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        azureResponse.SetResult(JsonSerializer.SerializeToElement(new
        {
            providerId = "azure",
            providerName = "Azure",
            required = true,
            status = "notReady",
        }));
        InteractionAuthStatus current = await azureRead;
        openaiResponse.SetResult(JsonSerializer.SerializeToElement(new
        {
            providerId = "openai",
            providerName = "OpenAI",
            required = false,
            status = "succeeded",
        }));
        InteractionAuthStatus staleRequestResult = await openaiRead;

        string[] requestedProviderIds = connection.Requests
            .Where(request => request.Method == "account/gatewayOAuth/read")
            .Select(request => JsonSerializer.SerializeToElement(request.Parameters))
            .Where(parameters => parameters.TryGetProperty("modelProvider", out _))
            .Select(parameters => parameters.GetProperty("modelProvider").GetString()!)
            .ToArray();

        Assert.AreEqual("azure", current.ModelProviderId);
        Assert.AreEqual(InteractionAuthState.ReauthenticationRequired, current.State);
        Assert.AreEqual("azure", staleRequestResult.ModelProviderId);
        Assert.AreEqual(InteractionAuthState.ReauthenticationRequired, staleRequestResult.State);
        CollectionAssert.AreEqual(new[] { "openai", "azure" }, requestedProviderIds);
    }

    [TestMethod]
    public async Task GatewayOAuthLoginUsesTheProviderWhoseStatusWasChecked()
    {
        var connection = new RecordingConnection
        {
            AsyncHandler = (method, _, _) => method == "initialize"
                ? Task.FromResult(JsonSerializer.SerializeToElement(new { userAgent = "codex-cli/0.169.0" }))
                : Task.FromResult(JsonSerializer.SerializeToElement(new { })),
        };
        connection.GatewayOAuthReadHandler = (_, _) => Task.FromResult(JsonSerializer.SerializeToElement(new
        {
            providerId = "azure",
            providerName = "Azure",
            required = true,
            status = "notReady",
        }));
        await using var service = CreateService();
        await service.InitializeAsync(connection, Options(), CancellationToken.None);

        _ = await service.ReadGatewayOAuthAsync("azure", CancellationToken.None);
        _ = await service.LoginGatewayOAuthAsync("azure", CancellationToken.None);

        JsonElement loginParameters = ParametersFor(connection, "account/gatewayOAuth/login");
        Assert.AreEqual("azure", loginParameters.GetProperty("modelProvider").GetString());
    }

    [TestMethod]
    public async Task GatewayOAuthReadTreatsBlankProviderIdAsUnscoped()
    {
        JsonElement lastParameters = default;
        var connection = new RecordingConnection
        {
            AsyncHandler = (method, _, _) => method == "initialize"
                ? Task.FromResult(JsonSerializer.SerializeToElement(new { userAgent = "codex-cli/0.169.0" }))
                : Task.FromResult(JsonSerializer.SerializeToElement(new { })),
        };
        connection.GatewayOAuthReadHandler = (parameters, _) =>
        {
            lastParameters = JsonSerializer.SerializeToElement(parameters);
            return Task.FromResult(JsonSerializer.SerializeToElement(new
            {
                providerId = "openai",
                providerName = "OpenAI",
                required = false,
                status = "succeeded",
            }));
        };
        await using var service = CreateService();
        await service.InitializeAsync(connection, Options(), CancellationToken.None);

        await service.ReadGatewayOAuthAsync(" \t ", CancellationToken.None);

        Assert.IsFalse(lastParameters.TryGetProperty("modelProvider", out _));
    }

    [TestMethod]
    public async Task SetDefaultModelProviderCancellationBeforeDispatchDoesNotWrite()
    {
        using var cancellation = new CancellationTokenSource();
        var catalogReadStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pendingCatalogRead = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        var connection = new RecordingConnection
        {
            AsyncHandler = (method, _, token) => method switch
            {
                "initialize" => Task.FromResult(JsonSerializer.SerializeToElement(new { userAgent = "codex-cli/0.169.0" })),
                "modelProvider/list" => WaitForCatalogCancellation(token),
                _ => Task.FromResult(JsonSerializer.SerializeToElement(new { })),
            },
        };
        Task<JsonElement> WaitForCatalogCancellation(CancellationToken token)
        {
            catalogReadStarted.TrySetResult();
            token.Register(() => pendingCatalogRead.TrySetCanceled(token));
            return pendingCatalogRead.Task;
        }
        await using var service = CreateService();
        await service.InitializeAsync(connection, Options(), CancellationToken.None);

        Task<ListModelProvidersResult> pending = service.SetDefaultModelProviderAsync("azure", cancellation.Token);
        await catalogReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();

        await Assert.ThrowsExactlyAsync<TaskCanceledException>(() => pending);
        Assert.IsFalse(connection.Requests.Any(request => request.Method is "config/value/write" or "modelProvider/default/set"));
    }

    private static Task<JsonElement> StartRead(
        TaskCompletionSource started,
        TaskCompletionSource<JsonElement> response)
    {
        started.TrySetResult();
        return response.Task;
    }

    private static JsonElement ProviderCatalogResponse(string defaultProviderId, bool canChangeDefault)
        => JsonSerializer.SerializeToElement(new
        {
            defaultProviderId,
            canChangeDefault,
            data = new[]
            {
                new { id = "openai", name = "OpenAI", baseUrl = "https://api.example.test/v1", settings = new { type = "openai" } },
                new { id = "azure", name = "Azure", baseUrl = "https://azure.example.test/v1", settings = new { type = "custom" } },
            },
        });

    private static Task<JsonElement> CancelWrite(CancellationTokenSource cancellation)
    {
        cancellation.Cancel();
        return Task.FromException<JsonElement>(new OperationCanceledException(cancellation.Token));
    }

    private static JsonElement LastParametersFor(RecordingConnection connection, string method)
        => JsonSerializer.SerializeToElement(
            connection.Requests.Last(item => item.Method == method).Parameters,
            WireJsonOptions);
}
