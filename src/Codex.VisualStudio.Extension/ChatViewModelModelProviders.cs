using System.Collections.ObjectModel;
using System.Runtime.Serialization;
using Codex.VisualStudio.Contracts;
using StreamJsonRpc;

namespace Codex.VisualStudio.Extension;

public sealed partial class ChatViewModel
{
    private readonly object modelProviderCatalogSync = new();
    private OwnerSnapshot? modelProviderCatalogOwner;
    private bool modelProviderCatalogLoaded;
    private bool isApplyingModelProviderCatalog;
    private bool modelProviderCanChangeDefault;
    private bool modelProviderStatusIsDefaultOutcome;
    private bool modelProviderStatusClearsAfterModelListSuccess;
    private int isChangingDefaultModelProvider;
    private long modelProviderCatalogGeneration;
    private long modelProviderSelectionGeneration;
    private string? selectedModelProviderId;
    private string modelProviderStatusText = string.Empty;
    private readonly string modelProviderFixedDefaultText = LocalizedStrings.Get("Provider.FixedDefault");
    private readonly string setDefaultModelProviderText = LocalizedStrings.Get("Provider.SetDefault");
    private readonly string setDefaultModelProviderToolTip = LocalizedStrings.Get("Provider.SetDefaultTooltip");
    private readonly string modelProviderSelectorAutomationName = LocalizedStrings.Get("Provider.SelectorAutomationName");
    private readonly string modelSelectorToolTip = LocalizedStrings.Get("Model.SelectorToolTip");
    private readonly string modelSelectorAutomationName = LocalizedStrings.Get("Model.SelectorAutomationName");

    [DataMember]
    public ObservableCollection<ModelProviderInfo> ModelProviders { get; } = [];

    [DataMember]
    public string? SelectedModelProviderId
    {
        get => selectedModelProviderId;
        set => SetSelectedModelProviderId(value, reloadModels: true);
    }

    [DataMember]
    public AsyncCommand SetDefaultModelProviderCommand { get; private set; } = null!;

    [DataMember]
    public bool HasModelProviderOptions => ModelProviders.Count > 0;

    [DataMember]
    public bool ShowModelProviderSelector => SelectedThread is null && HasModelProviderOptions;

    [DataMember]
    public bool IsModelProviderSelectorEnabled
        => SelectedThread is null
            && HasModelProviderOptions
            && Status.State is WorkerConnectionState.Ready or WorkerConnectionState.Busy or WorkerConnectionState.WaitingForApproval
            && Volatile.Read(ref isChangingDefaultModelProvider) == 0;

    [DataMember]
    public bool ShowSelectedThreadProvider
        => !string.IsNullOrWhiteSpace(SelectedThread?.ModelProviderId);

    [DataMember]
    public string SelectedThreadProviderText
    {
        get
        {
            ThreadSummary? thread = SelectedThread;
            if (thread is null || string.IsNullOrWhiteSpace(thread.ModelProviderId))
            {
                return string.Empty;
            }

            string? name = thread.ModelProviderName;
            bool fallbackIsAlreadySafe = false;
            if (string.IsNullOrWhiteSpace(name))
            {
                name = ModelProviders.FirstOrDefault(provider =>
                    string.Equals(provider.Id, thread.ModelProviderId, StringComparison.Ordinal))?.Name;
                fallbackIsAlreadySafe = true;
            }

            string safeName = string.IsNullOrWhiteSpace(name)
                ? LocalizedStrings.Get("Provider.Unknown")
                : fallbackIsAlreadySafe ? name : SafeMarkdownService.ToSafeLiteralText(name);
            return LocalizedStrings.Format("Provider.SavedConversation", safeName);
        }
    }

    [DataMember]
    public string ModelProviderSelectionHelpText
        => SelectedThread is not null
            ? LocalizedStrings.Get("Provider.SavedConversationHelp")
            : LocalizedStrings.Get("Provider.SelectionHelp");

    [DataMember]
    public string ModelProviderFixedDefaultText => modelProviderFixedDefaultText;

    [DataMember]
    public bool ShowFixedModelProviderDefaultNotice
        => SelectedThread is null && HasModelProviderOptions && !modelProviderCanChangeDefault;

    [DataMember]
    public bool ShowModelProviderStatus => !string.IsNullOrWhiteSpace(modelProviderStatusText);

    [DataMember]
    public string ModelProviderStatusText => modelProviderStatusText;

    [DataMember]
    public string SetDefaultModelProviderText => setDefaultModelProviderText;

    [DataMember]
    public string SetDefaultModelProviderToolTip => setDefaultModelProviderToolTip;

    [DataMember]
    public string ModelProviderSelectorAutomationName => modelProviderSelectorAutomationName;

    [DataMember]
    public string ModelSelectorToolTip => modelSelectorToolTip;

    [DataMember]
    public string ModelSelectorAutomationName => modelSelectorAutomationName;

    [DataMember]
    public bool CanSetDefaultModelProvider
        => SelectedThread is null
            && Volatile.Read(ref isChangingDefaultModelProvider) == 0
            && modelProviderCanChangeDefault
            && !string.IsNullOrWhiteSpace(selectedModelProviderId)
            && ModelProviders.Any(provider =>
                string.Equals(provider.Id, selectedModelProviderId, StringComparison.Ordinal)
                && !provider.IsDefault);

    [DataMember]
    public bool ShowSetDefaultModelProvider => CanSetDefaultModelProvider;

    private string? CurrentModelProviderId
        => SelectedThread is { } thread
            ? thread.ModelProviderId
            : selectedModelProviderId;

    private void InitializeModelProviderCommands()
        => SetDefaultModelProviderCommand = new AsyncCommand(SetDefaultModelProviderAsync, () => CanSetDefaultModelProvider);

    private void SetSelectedModelProviderId(string? value, bool reloadModels)
    {
        if (isApplyingModelProviderCatalog)
        {
            return;
        }

        if (value is null
            && selectedModelProviderId is not null
            && ModelProviders.Any(provider => string.Equals(provider.Id, selectedModelProviderId, StringComparison.Ordinal)))
        {
            return;
        }

        if (value is not null
            && !ModelProviders.Any(provider => string.Equals(provider.Id, value, StringComparison.Ordinal)))
        {
            return;
        }

        string? previousActiveProviderId = CurrentModelProviderId;
        if (!SetProperty(ref selectedModelProviderId, value))
        {
            return;
        }

        bool activeProviderChanged = !string.Equals(previousActiveProviderId, CurrentModelProviderId, StringComparison.Ordinal);
        if (activeProviderChanged)
        {
            Interlocked.Increment(ref modelProviderSelectionGeneration);
            SetModelProviderStatus(string.Empty);
        }

        NotifyModelProviderPresentationChanged();
        if (reloadModels && activeProviderChanged && value is not null && SelectedThread is null)
        {
            _ = PopulateModelsAsync();
            _ = ReadGatewayOAuthAsync();
        }
    }

    private void OnSelectedThreadProviderChanged(string? previousProviderId)
    {
        string? currentProviderId = CurrentModelProviderId;
        bool providerChanged = !string.Equals(previousProviderId, currentProviderId, StringComparison.Ordinal);
        if (providerChanged)
        {
            Interlocked.Increment(ref modelProviderSelectionGeneration);
        }

        NotifyModelProviderPresentationChanged();
        if (providerChanged)
        {
            _ = PopulateModelsAsync();
            _ = ReadGatewayOAuthAsync();
        }
    }

    private void OnModelProviderStatusChanged()
        => NotifyModelProviderPresentationChanged();

    private async Task RefreshModelProviderCatalogAfterUnknownWriteAsync(OwnerSnapshot owner)
    {
        lock (modelProviderCatalogSync)
        {
            if (modelProviderCatalogOwner is { } catalogOwner && catalogOwner.Equals(owner))
            {
                modelProviderCatalogLoaded = false;
            }
        }

        Interlocked.Increment(ref modelProviderCatalogGeneration);
        try
        {
            await EnsureModelProviderCatalogAsync(owner).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            ExtensionDiagnostics.Write($"Provider catalog refresh after an unknown write failed ({exception.GetType().Name}).");
        }
    }

    private void ClearModelProviderOwnerState()
    {
        Interlocked.Increment(ref modelProviderCatalogGeneration);
        Interlocked.Increment(ref modelProviderSelectionGeneration);
        lock (modelProviderCatalogSync)
        {
            modelProviderCatalogOwner = null;
            modelProviderCatalogLoaded = false;
        }
        modelProviderCanChangeDefault = false;
        isApplyingModelProviderCatalog = true;
        try
        {
            ModelProviders.Clear();
            selectedModelProviderId = null;
        }
        finally
        {
            isApplyingModelProviderCatalog = false;
        }
        modelProviderStatusText = string.Empty;
        modelProviderStatusIsDefaultOutcome = false;
        modelProviderStatusClearsAfterModelListSuccess = false;
        NotifyModelProviderPresentationChanged();
    }

    private void NotifyModelProviderPresentationChanged()
    {
        OnPropertyChanged(nameof(SelectedModelProviderId));
        OnPropertyChanged(nameof(HasModelProviderOptions));
        OnPropertyChanged(nameof(ShowModelProviderSelector));
        OnPropertyChanged(nameof(IsModelProviderSelectorEnabled));
        OnPropertyChanged(nameof(ShowSelectedThreadProvider));
        OnPropertyChanged(nameof(SelectedThreadProviderText));
        OnPropertyChanged(nameof(ModelProviderSelectionHelpText));
        OnPropertyChanged(nameof(ShowFixedModelProviderDefaultNotice));
        OnPropertyChanged(nameof(ShowModelProviderStatus));
        OnPropertyChanged(nameof(ModelProviderStatusText));
        OnPropertyChanged(nameof(CanSetDefaultModelProvider));
        OnPropertyChanged(nameof(ShowSetDefaultModelProvider));
        SetDefaultModelProviderCommand?.RaiseCanExecuteChanged();
    }

    private async Task EnsureModelProviderCatalogAsync(OwnerSnapshot owner, bool callerWillPopulateModels = false)
    {
        long catalogGeneration;
        lock (modelProviderCatalogSync)
        {
            if (modelProviderCatalogLoaded && modelProviderCatalogOwner is { } catalogOwner && catalogOwner.Equals(owner))
            {
                return;
            }

            catalogGeneration = Interlocked.Increment(ref modelProviderCatalogGeneration);
        }

        try
        {
            await OnUiAsync(() =>
            {
                if (!IsCurrentOwner(owner))
                {
                    return;
                }

                if (modelProviderCatalogOwner is { } previousOwner && !previousOwner.Equals(owner))
                {
                    isApplyingModelProviderCatalog = true;
                    try
                    {
                        ModelProviders.Clear();
                        selectedModelProviderId = null;
                    }
                    finally
                    {
                        isApplyingModelProviderCatalog = false;
                    }

                    modelProviderCanChangeDefault = false;
                    modelProviderStatusText = string.Empty;
                    modelProviderStatusIsDefaultOutcome = false;
                    modelProviderStatusClearsAfterModelListSuccess = false;
                    NotifyModelProviderPresentationChanged();
                }
            }).ConfigureAwait(false);
            if (!IsCurrentOwner(owner))
            {
                return;
            }

            ListModelProvidersResult result = await bridge.ListModelProvidersAsync(
                StampOwner(new ListModelProvidersRequest(), owner),
                lifetime.Token).ConfigureAwait(false);
            bool catalogApplied = false;
            bool activeProviderChanged = false;
            await OnUiAsync(() =>
            {
                if (IsCurrentOwner(owner)
                    && catalogGeneration == Interlocked.Read(ref modelProviderCatalogGeneration))
                {
                    activeProviderChanged = ApplyModelProviderCatalog(result, owner, reloadModelsOnProviderChange: !callerWillPopulateModels);
                    catalogApplied = true;
                }
            }).ConfigureAwait(false);
            if (catalogApplied
                && IsCurrentOwner(owner)
                && catalogGeneration == Interlocked.Read(ref modelProviderCatalogGeneration)
                && !activeProviderChanged
                && CurrentModelProviderId is not null)
            {
                _ = ReadGatewayOAuthAsync();
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            ExtensionDiagnostics.Write($"Model provider catalog refresh failed ({exception.GetType().Name}).");
            await OnUiAsync(() =>
            {
                if (IsCurrentOwner(owner)
                    && catalogGeneration == Interlocked.Read(ref modelProviderCatalogGeneration))
                {
                    SetModelProviderStatus(DescribeModelProviderFailure(exception));
                }
            }).ConfigureAwait(false);
        }
    }

    private bool ApplyModelProviderCatalog(
        ListModelProvidersResult result,
        OwnerSnapshot owner,
        bool reloadModelsOnProviderChange = true)
    {
        if (!IsCurrentOwner(owner))
        {
            return false;
        }

        if (result.Providers is null
            || ((result.DefaultWriteOutcomeUnknown || result.DefaultCatalogRefreshFailed)
                && result.Providers.Count == 0))
        {
            SetModelProviderStatus(
                LocalizedStrings.Get(result.DefaultWriteOutcomeUnknown
                    ? "Provider.DefaultWriteOutcomeUnknown"
                    : "Provider.DefaultRefreshFailed"),
                isDefaultOutcome: true);
            NotifyModelProviderPresentationChanged();
            return false;
        }

        string? previousSelection = selectedModelProviderId;
        string? previousActiveProviderId = CurrentModelProviderId;
        isApplyingModelProviderCatalog = true;
        try
        {
            var existingProvidersById = new Dictionary<string, ModelProviderInfo>(StringComparer.Ordinal);
            foreach (ModelProviderInfo existingProvider in ModelProviders)
            {
                existingProvidersById.TryAdd(existingProvider.Id, existingProvider);
            }

            var appliedProviderIds = new HashSet<string>(StringComparer.Ordinal);
            int targetIndex = 0;
            foreach (ModelProviderInfo provider in result.Providers ?? Array.Empty<ModelProviderInfo>())
            {
                if (!IsSafeModelProviderId(provider.Id))
                {
                    continue;
                }

                string name = SafeMarkdownService.ToSafeLiteralText(provider.Name);
                if (string.IsNullOrWhiteSpace(name) || !appliedProviderIds.Add(provider.Id))
                {
                    continue;
                }

                ModelProviderInfo currentProvider;
                if (existingProvidersById.TryGetValue(provider.Id, out ModelProviderInfo? existingProvider))
                {
                    currentProvider = existingProvider;
                    currentProvider.Name = name;
                    currentProvider.BaseUrl = SanitizeModelProviderBaseUrl(provider.BaseUrl);
                    currentProvider.Type = provider.Type;
                    currentProvider.CanChangeDefault = result.CanChangeDefault;
                    currentProvider.IsDefault = string.Equals(provider.Id, result.DefaultProviderId, StringComparison.Ordinal);

                    int currentIndex = ModelProviders.IndexOf(currentProvider);
                    if (currentIndex < 0)
                    {
                        ModelProviders.Insert(targetIndex, currentProvider);
                    }
                    else if (currentIndex != targetIndex)
                    {
                        ModelProviders.Move(currentIndex, targetIndex);
                    }
                }
                else
                {
                    currentProvider = new ModelProviderInfo
                    {
                        Id = provider.Id,
                        Name = name,
                        BaseUrl = SanitizeModelProviderBaseUrl(provider.BaseUrl),
                        Type = provider.Type,
                        CanChangeDefault = result.CanChangeDefault,
                        IsDefault = string.Equals(provider.Id, result.DefaultProviderId, StringComparison.Ordinal),
                    };
                    existingProvidersById.Add(provider.Id, currentProvider);
                    ModelProviders.Insert(targetIndex, currentProvider);
                }

                targetIndex++;
            }

            while (ModelProviders.Count > targetIndex)
            {
                ModelProviders.RemoveAt(ModelProviders.Count - 1);
            }
        }
        finally
        {
            isApplyingModelProviderCatalog = false;
        }

        modelProviderCanChangeDefault = result.CanChangeDefault;
        lock (modelProviderCatalogSync)
        {
            modelProviderCatalogOwner = owner;
            modelProviderCatalogLoaded = true;
        }

        string? nextSelection = ModelProviders.Any(provider => string.Equals(provider.Id, previousSelection, StringComparison.Ordinal))
            ? previousSelection
            : ModelProviders.FirstOrDefault(provider => provider.IsDefault)?.Id
                ?? ModelProviders.FirstOrDefault()?.Id;
        SetSelectedModelProviderId(nextSelection, reloadModels: false);
        bool activeProviderChanged = !string.Equals(previousActiveProviderId, CurrentModelProviderId, StringComparison.Ordinal);

        string? statusKey = result.DefaultWriteOutcomeUnknown
            ? "Provider.DefaultWriteOutcomeUnknown"
            : result.DefaultCatalogRefreshFailed
                ? "Provider.DefaultRefreshFailed"
                : null;
        SetModelProviderStatus(statusKey is null ? string.Empty : LocalizedStrings.Get(statusKey), isDefaultOutcome: statusKey is not null);
        NotifyModelProviderPresentationChanged();
        if (activeProviderChanged && CurrentModelProviderId is not null)
        {
            if (reloadModelsOnProviderChange)
            {
                _ = PopulateModelsAsync();
            }

            _ = ReadGatewayOAuthAsync();
        }

        return activeProviderChanged;
    }

    private void ResetModelCatalogToBuiltInFallback()
    {
        modelCatalog = new ListModelsResult();
        modelCatalogProviderId = null;
        hasLoadedModelCatalogForProvider = false;
        string[] fallbackModels = ["gpt-5-codex", "gpt-5"];
        for (int i = 0; i < fallbackModels.Length; i++)
        {
            int existingIndex = Models.IndexOf(fallbackModels[i]);
            if (existingIndex < 0)
            {
                Models.Insert(i, fallbackModels[i]);
            }
            else if (existingIndex != i)
            {
                Models.Move(existingIndex, i);
            }
        }

        if (SelectedModel is null || !fallbackModels.Contains(SelectedModel, StringComparer.Ordinal))
        {
            SelectedModel = fallbackModels[0];
        }

        for (int i = Models.Count - 1; i >= 0; i--)
        {
            if (!fallbackModels.Contains(Models[i], StringComparer.Ordinal))
            {
                Models.RemoveAt(i);
            }
        }

        RefreshServiceTiers();
        UpdateComposerSuggestions(ComposerText);
        RefreshReasoningEfforts();
        OnPropertyChanged(nameof(ComposerAdmissionReason));
        OnPropertyChanged(nameof(HasComposerAdmissionReason));
    }

    private static bool IsSafeModelProviderId(string? value)
        => !string.IsNullOrWhiteSpace(value)
            && value.Length <= 256
            && !value.Any(char.IsControl);

    private static string? SanitizeModelProviderBaseUrl(string? value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out Uri? uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            || !string.IsNullOrEmpty(uri.UserInfo))
        {
            return null;
        }

        string safe = SafeMarkdownService.ToSafeLiteralText(uri.GetLeftPart(UriPartial.Path));
        return safe.Length <= 2048 ? safe : safe[..2048];
    }

    private string DescribeModelProviderFailure(Exception exception)
    {
        if (GetProviderErrorCode(exception) is { } providerErrorCode)
        {
            string key = providerErrorCode switch
            {
                ProviderErrorCodes.ModelProviderNotConfigured => "Provider.Error.NotConfigured",
                ProviderErrorCodes.ModelProviderPolicyConflict => "Provider.Error.PolicyConflict",
                ProviderErrorCodes.ModelProviderMismatch => "Provider.Error.Mismatch",
                _ => "Provider.ModelsUnavailable",
            };
            return LocalizedStrings.Get(key);
        }

        if (exception is RemoteInvocationException { ErrorCode: WorkerErrorCodes.UpstreamOperationFailed } remote)
        {
            string safeMessage = markdown.ToSafeText(remote.Message).Trim();
            return safeMessage.Length > 512
                ? string.Concat(safeMessage.AsSpan(0, 511), "…")
                : string.IsNullOrWhiteSpace(safeMessage)
                    ? LocalizedStrings.Get("Provider.ModelsUnavailable")
                    : safeMessage;
        }

        return LocalizedStrings.Get("Provider.ModelsUnavailable");
    }

    private void SetModelProviderStatus(string value)
        => SetModelProviderStatus(value, isDefaultOutcome: false, clearAfterModelListSuccess: false);

    private void SetModelProviderStatus(string value, bool isDefaultOutcome)
        => SetModelProviderStatus(value, isDefaultOutcome, clearAfterModelListSuccess: false);

    private void SetModelProviderStatus(string value, bool isDefaultOutcome, bool clearAfterModelListSuccess)
    {
        if (string.Equals(modelProviderStatusText, value, StringComparison.Ordinal)
            && modelProviderStatusIsDefaultOutcome == isDefaultOutcome
            && modelProviderStatusClearsAfterModelListSuccess == clearAfterModelListSuccess)
        {
            return;
        }

        modelProviderStatusText = value;
        modelProviderStatusIsDefaultOutcome = isDefaultOutcome;
        modelProviderStatusClearsAfterModelListSuccess = clearAfterModelListSuccess;
        OnPropertyChanged(nameof(ModelProviderStatusText));
        OnPropertyChanged(nameof(ShowModelProviderStatus));
    }

    private bool IsCurrentModelProviderRequest(OwnerSnapshot owner, string? providerId, long selectionGeneration)
        => IsCurrentOwner(owner)
            && selectionGeneration == Interlocked.Read(ref modelProviderSelectionGeneration)
            && string.Equals(CurrentModelProviderId, providerId, StringComparison.Ordinal);

    private async Task SetDefaultModelProviderAsync()
    {
        OwnerSnapshot requestOwner = CaptureOwnerSnapshot();
        string? providerId = selectedModelProviderId;
        long selectionGeneration = Interlocked.Read(ref modelProviderSelectionGeneration);
        long initialCatalogGeneration = Interlocked.Read(ref modelProviderCatalogGeneration);
        bool catalogBelongsToOwner;
        lock (modelProviderCatalogSync)
        {
            catalogBelongsToOwner = modelProviderCatalogLoaded
                && modelProviderCatalogOwner is { } catalogOwner
                && catalogOwner.Equals(requestOwner);
        }

        if (!CanSetDefaultModelProvider || providerId is null || !catalogBelongsToOwner)
        {
            return;
        }

        if (Interlocked.CompareExchange(ref isChangingDefaultModelProvider, 1, 0) != 0)
        {
            return;
        }

        bool mutationStarted = false;
        long writeCatalogGeneration = -1;
        bool refreshAfterUncertainCatalogResult = false;
        try
        {
            await OnUiAsync(() =>
            {
                if (IsCurrentModelProviderRequest(requestOwner, providerId, selectionGeneration)
                    && initialCatalogGeneration == Interlocked.Read(ref modelProviderCatalogGeneration))
                {
                    Volatile.Write(ref isChangingDefaultModelProvider, 1);
                    NotifyModelProviderPresentationChanged();
                }
            }).ConfigureAwait(false);
            if (!IsCurrentModelProviderRequest(requestOwner, providerId, selectionGeneration)
                || initialCatalogGeneration != Interlocked.Read(ref modelProviderCatalogGeneration))
            {
                return;
            }

            writeCatalogGeneration = Interlocked.Increment(ref modelProviderCatalogGeneration);
            mutationStarted = true;
            ListModelProvidersResult result = await bridge.SetDefaultModelProviderAsync(
                StampOwner(new SetDefaultModelProviderRequest { ProviderId = providerId }, requestOwner),
                lifetime.Token).ConfigureAwait(false);
            await OnUiAsync(() =>
            {
                if (!IsCurrentOwner(requestOwner))
                {
                    return;
                }

                if (writeCatalogGeneration != Interlocked.Read(ref modelProviderCatalogGeneration))
                {
                    bool currentCatalogConfirmsDefault = ModelProviders.Any(provider => provider.IsDefault
                        && string.Equals(provider.Id, providerId, StringComparison.Ordinal));
                    if (currentCatalogConfirmsDefault)
                    {
                        SetModelProviderStatus(LocalizedStrings.Get("Provider.DefaultChanged"), isDefaultOutcome: true);
                    }
                    else
                    {
                        SetModelProviderStatus(LocalizedStrings.Get("Provider.DefaultWriteOutcomeUnknown"), isDefaultOutcome: true);
                        refreshAfterUncertainCatalogResult = true;
                    }

                    return;
                }

                Interlocked.Increment(ref modelProviderCatalogGeneration);
                ApplyModelProviderCatalog(result, requestOwner);
                bool returnedCatalogConfirmsDefault = ModelProviders.Any(provider => provider.IsDefault
                    && string.Equals(provider.Id, providerId, StringComparison.Ordinal));
                if (returnedCatalogConfirmsDefault && !result.DefaultCatalogRefreshFailed)
                {
                    SetModelProviderStatus(LocalizedStrings.Get("Provider.DefaultChanged"), isDefaultOutcome: true);
                }

                if ((result.DefaultWriteOutcomeUnknown || result.DefaultCatalogRefreshFailed)
                    && (result.Providers is null || result.Providers.Count == 0))
                {
                    refreshAfterUncertainCatalogResult = true;
                }

                if (!returnedCatalogConfirmsDefault
                    && !result.DefaultWriteOutcomeUnknown
                    && !result.DefaultCatalogRefreshFailed)
                {
                    SetModelProviderStatus(
                        LocalizedStrings.Get("Provider.DefaultRefreshFailed"),
                        isDefaultOutcome: true);
                }
            }).ConfigureAwait(false);
            if (refreshAfterUncertainCatalogResult && IsCurrentOwner(requestOwner))
            {
                await RefreshModelProviderCatalogAfterUnknownWriteAsync(requestOwner).ConfigureAwait(false);
                await OnUiAsync(() =>
                {
                    if (IsCurrentOwner(requestOwner))
                    {
                        bool currentCatalogConfirmsDefault = ModelProviders.Any(provider => provider.IsDefault
                            && string.Equals(provider.Id, providerId, StringComparison.Ordinal));
                        SetModelProviderStatus(
                            LocalizedStrings.Get(currentCatalogConfirmsDefault
                                ? "Provider.DefaultChanged"
                                : "Provider.DefaultWriteOutcomeUnknown"),
                            isDefaultOutcome: true);
                    }
                }).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
        {
        }
        catch (NotSupportedException)
        {
            await OnUiAsync(() =>
            {
                if (IsCurrentOwner(requestOwner))
                {
                    SetModelProviderStatus(LocalizedStrings.Get("Provider.DefaultWriteFailed"));
                }
            }).ConfigureAwait(false);
        }
        catch (RemoteInvocationException exception) when (exception.ErrorCode is WorkerErrorCodes.PreDispatchRejected or WorkerErrorCodes.UpstreamOperationRejected)
        {
            await OnUiAsync(() =>
            {
                if (IsCurrentOwner(requestOwner))
                {
                    SetModelProviderStatus(LocalizedStrings.Get("Provider.DefaultWriteFailed"));
                }
            }).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            ExtensionDiagnostics.Write($"Default model provider write outcome unknown ({exception.GetType().Name}).");
            if (mutationStarted && IsCurrentOwner(requestOwner))
            {
                await RefreshModelProviderCatalogAfterUnknownWriteAsync(requestOwner).ConfigureAwait(false);
            }

            await OnUiAsync(() =>
            {
                if (mutationStarted && IsCurrentOwner(requestOwner))
                {
                    bool currentCatalogConfirmsDefault = ModelProviders.Any(provider => provider.IsDefault
                        && string.Equals(provider.Id, providerId, StringComparison.Ordinal));
                    SetModelProviderStatus(
                        LocalizedStrings.Get(currentCatalogConfirmsDefault
                            ? "Provider.DefaultChanged"
                            : "Provider.DefaultWriteOutcomeUnknown"),
                        isDefaultOutcome: true);
                }
                else if (IsCurrentOwner(requestOwner))
                {
                    SetModelProviderStatus(LocalizedStrings.Get("Provider.DefaultWriteFailed"));
                }
            }).ConfigureAwait(false);
        }
        finally
        {
            Volatile.Write(ref isChangingDefaultModelProvider, 0);
            await OnUiAsync(() =>
            {
                NotifyModelProviderPresentationChanged();
            }).ConfigureAwait(false);
        }
    }
}
