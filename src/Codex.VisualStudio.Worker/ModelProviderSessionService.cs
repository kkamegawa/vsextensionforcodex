using System.Text.Json;
using Codex.AppServer.Protocol;
using Codex.VisualStudio.Contracts;

namespace Codex.VisualStudio.Worker;

public sealed partial class CodexSessionService
{
    private readonly SemaphoreSlim modelProviderDefaultMutationGate = new(1, 1);

    private static object GatewayOAuthParameters(string? providerId)
        => providerId is null ? new { } : new { modelProvider = providerId };

    public async Task<ListModelProvidersResult> ListModelProvidersAsync(CancellationToken cancellationToken)
    {
        ConnectionContext context = RequireContext();
        return await ListModelProvidersAsync(context, cancellationToken).ConfigureAwait(false);
    }

    private async Task<ListModelProvidersResult> ListModelProvidersAsync(
        ConnectionContext context,
        CancellationToken cancellationToken)
    {
        EnsureCurrent(context);
        JsonElement result = await context.Connection.SendReadOnlyRequestAsync(
            "modelProvider/list", new { }, TimeSpan.FromSeconds(15), cancellationToken).ConfigureAwait(false);
        EnsureCurrent(context);

        string? defaultProviderId = NormalizeWireIdentifier(GetString(result, "defaultProviderId"));
        bool canChangeDefault = GetBool(result, "canChangeDefault") == true;
        var providers = new List<ModelProviderInfo>();
        if (result.TryGetProperty("data", out JsonElement data) && data.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement provider in data.EnumerateArray())
            {
                string? id = NormalizeWireIdentifier(GetString(provider, "id"));
                string? name = GetBoundedString(provider, "name", 256);
                if (id is null || string.IsNullOrWhiteSpace(name))
                {
                    continue;
                }

                JsonElement settings = provider.TryGetProperty("settings", out JsonElement configured)
                    && configured.ValueKind == JsonValueKind.Object ? configured : default;
                var providerInfo = new ModelProviderInfo
                {
                    Id = id,
                    Name = redactor.Redact(name),
                    BaseUrl = SanitizeProviderBaseUrl(GetBoundedString(provider, "baseUrl", 2048)),
                    Type = NormalizeWireIdentifier(GetString(settings, "type")),
                    CanChangeDefault = canChangeDefault,
                    IsDefault = string.Equals(id, defaultProviderId, StringComparison.Ordinal),
                };
                providers.Add(providerInfo);
            }
        }


        return new ListModelProvidersResult
        {
            Providers = providers,
            DefaultProviderId = defaultProviderId,
            CanChangeDefault = canChangeDefault,
        };
    }

    public async Task<ListModelProvidersResult> SetDefaultModelProviderAsync(
        string providerId,
        CancellationToken cancellationToken)
    {
        await modelProviderDefaultMutationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await SetDefaultModelProviderCoreAsync(providerId, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            modelProviderDefaultMutationGate.Release();
        }
    }

    private async Task<ListModelProvidersResult> SetDefaultModelProviderCoreAsync(
        string providerId,
        CancellationToken cancellationToken)
    {
        string normalizedId;
        ConnectionContext context;
        ListModelProvidersResult current;
        CodexFeatureTier tier;
        try
        {
            normalizedId = NormalizeWireIdentifier(providerId)
                ?? throw new ArgumentException("The model provider ID is invalid.", nameof(providerId));
            context = RequireContext();
            EnsureCurrent(context);

            current = await ListModelProvidersAsync(context, cancellationToken).ConfigureAwait(false);
            EnsureCurrent(context);
            if (!current.CanChangeDefault)
            {
                throw new InvalidOperationException(LocalizedStrings.Get("Provider.FixedDefault"));
            }

            if (!current.Providers.Any(provider => string.Equals(provider.Id, normalizedId, StringComparison.Ordinal)))
            {
                throw new InvalidOperationException("The selected model provider is no longer available.");
            }

            if (!CodexCliVersion.TryParse(CodexVersion, out CodexCliVersion? version) || version is null)
            {
                throw new InvalidOperationException("The app-server version is unavailable; the default provider was not changed.");
            }

            CodexFeatureTier featureTier = CodexCliVersion.GetFeatureTier(version);
            if (featureTier == CodexFeatureTier.Unsupported)
            {
                throw new InvalidOperationException("The app-server version does not support changing the default model provider.");
            }

            EnsureCurrent(context);
            tier = featureTier;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (JsonRpcConnectionClosedException)
        {
            throw;
        }
        catch (ModelProviderPreDispatchException)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw new ModelProviderPreDispatchException(exception.Message, exception);
        }
        try
        {
            if (tier >= CodexFeatureTier.Tier171)
            {
                await SendModelProviderMutationAsync(
                    context,
                    "modelProvider/default/set",
                    new { providerId = normalizedId },
                    cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await SendModelProviderMutationAsync(
                    context,
                    "config/value/write",
                    new
                    {
                        keyPath = "model_provider",
                        value = normalizedId,
                        mergeStrategy = "replace",
                    },
                    cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            EnsureCurrent(context);
            WorkerDiagnostics.Write("default model provider write was canceled; refreshing provider catalog because its outcome is unknown");
            try
            {
                ListModelProvidersResult refreshed = await ListModelProvidersAsync(context, CancellationToken.None).ConfigureAwait(false);
                refreshed.DefaultWriteOutcomeUnknown = true;
                return refreshed;
            }
            catch (Exception refreshException)
            {
                EnsureCurrent(context);
                WorkerDiagnostics.Write("provider catalog refresh failed after canceled default write", refreshException);
                current.DefaultWriteOutcomeUnknown = true;
                return current;
            }
        }
        catch (JsonRpcRemoteException exception)
        {
            EnsureCurrent(context);
            WorkerDiagnostics.Write("default model provider write was rejected by the app-server", exception);
            throw new ModelProviderMutationRejectedException(
                "The app-server rejected the default model provider change.",
                exception);
        }
        catch (Exception exception)
        {
            EnsureCurrent(context);
            WorkerDiagnostics.Write("default model provider write outcome unknown; refreshing provider catalog", exception);
            try
            {
                ListModelProvidersResult refreshed = await ListModelProvidersAsync(context, CancellationToken.None).ConfigureAwait(false);
                refreshed.DefaultWriteOutcomeUnknown = true;
                return refreshed;
            }
            catch (Exception refreshException)
            {
                EnsureCurrent(context);
                WorkerDiagnostics.Write("provider catalog refresh failed after unknown default write outcome", refreshException);
                current.DefaultWriteOutcomeUnknown = true;
                return current;
            }
        }

        EnsureCurrent(context);
        try
        {
            return await ListModelProvidersAsync(context, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            EnsureCurrent(context);
            return CreateDefaultCatalogRefreshFailure(current, normalizedId);
        }
        catch (Exception exception)
        {
            EnsureCurrent(context);
            WorkerDiagnostics.Write("default provider changed but provider catalog refresh failed", exception);
            return CreateDefaultCatalogRefreshFailure(current, normalizedId);
        }
    }

    private async Task SendModelProviderMutationAsync(
        ConnectionContext context,
        string method,
        object parameters,
        CancellationToken cancellationToken)
    {
        _ = await context.Connection.SendRequestAsync(method, parameters, TimeSpan.FromSeconds(60), cancellationToken)
            .ConfigureAwait(false);
        EnsureCurrent(context);
    }

    private static ListModelProvidersResult CreateDefaultCatalogRefreshFailure(
        ListModelProvidersResult current,
        string normalizedId)
    {
        foreach (ModelProviderInfo provider in current.Providers)
        {
            provider.IsDefault = string.Equals(provider.Id, normalizedId, StringComparison.Ordinal);
        }

        current.DefaultProviderId = normalizedId;
        current.DefaultCatalogRefreshFailed = true;
        return current;
    }

    private string? SanitizeProviderBaseUrl(string? value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out Uri? uri)
            || uri.Scheme is not ("http" or "https")
            || !string.IsNullOrEmpty(uri.UserInfo))
        {
            return null;
        }

        return redactor.Redact(uri.GetLeftPart(UriPartial.Path));
    }

    private static string? NormalizeOptionalProviderId(string? providerId)
        => string.IsNullOrWhiteSpace(providerId)
            ? null
            : NormalizeWireIdentifier(providerId)
                ?? throw new ArgumentException("The model provider ID is invalid.", nameof(providerId));

    private async Task EnsureCredentialReadyForProviderAsync(
        ConnectionContext context,
        string? providerId,
        CancellationToken cancellationToken)
    {
        if (!context.IsLocal)
        {
            return;
        }

        if (providerId is null && context.GatewayOAuthRequestProviderId is null)
        {
            EnsureCredentialReady(context);
            return;
        }

        JsonElement result = await context.Connection.SendRequestAsync(
            "account/gatewayOAuth/read",
            GatewayOAuthParameters(providerId),
            TimeSpan.FromSeconds(15),
            cancellationToken).ConfigureAwait(false);
        EnsureCurrent(context);
        if (!TryReadGatewayOAuthResponse(
                result,
                out string? responseProviderId,
                out _,
                out bool required,
                out string? status,
                out string? safeError)
            || (providerId is not null && !string.Equals(responseProviderId, providerId, StringComparison.Ordinal)))
        {
            throw new InvalidOperationException("Gateway authorization status is invalid for the selected model provider.");
        }

        if (safeError is not null || (required && !string.Equals(status, "succeeded", StringComparison.Ordinal)))
        {
            throw new InvalidOperationException(
                safeError is not null
                    ? "Gateway authorization status is unavailable. Retry the status check before continuing."
                    : "Complete gateway authorization or cancel it before using this app-server operation.");
        }
    }

    private static string? ReadModelProviderId(JsonElement thread)
    {
        if (!thread.TryGetProperty("modelProvider", out JsonElement provider))
        {
            return null;
        }

        string? value = provider.ValueKind switch
        {
            JsonValueKind.Object => GetString(provider, "id"),
            JsonValueKind.String => provider.GetString(),
            _ => null,
        };
        return NormalizeWireIdentifier(value);
    }

    private string? ReadModelProviderName(JsonElement thread)
        => thread.TryGetProperty("modelProvider", out JsonElement provider)
            && provider.ValueKind == JsonValueKind.Object
                ? redactor.Redact(GetBoundedString(provider, "name", 256))
                : null;
}

public sealed class ModelProviderMutationRejectedException : InvalidOperationException
{
    public ModelProviderMutationRejectedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

public sealed class ModelProviderPreDispatchException : InvalidOperationException
{
    public ModelProviderPreDispatchException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
