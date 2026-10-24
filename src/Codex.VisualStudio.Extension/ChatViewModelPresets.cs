using System.Collections.ObjectModel;
using System.Runtime.Serialization;
using Codex.VisualStudio.Contracts;

namespace Codex.VisualStudio.Extension;

// Presets shared with the Codex CLI through the connected server's tui.presets (0.164 tier only).
// Recall copies a preset into the composer and never sends it.
public sealed partial class ChatViewModel
{
    private const string PresetsTierRequiredText =
        "Presets require Codex CLI 0.164.0 or later. Update Codex CLI and reconnect to use presets.";

    private enum PresetConfirmationKind
    {
        None,
        Replace,
        Delete,
        Recall,
    }

    private bool isPresetsPanelOpen;
    private bool isPresetsLoading;
    private bool isPresetSaving;
    private string presetsStatusText = string.Empty;
    private string? presetUserLayerVersion;
    private long presetListSequence;
    private bool isPresetEditorOpen;
    private string presetEditorSlot = string.Empty;
    private string presetEditorInput = string.Empty;
    private string presetEditorLabel = string.Empty;
    private PresetConfirmationKind presetConfirmationKind;
    private PresetSlotViewModel? presetConfirmationSlot;
    private string presetConfirmationText = string.Empty;

    [DataMember]
    public ObservableCollection<PresetSlotViewModel> PresetSlots { get; } = [];

    [DataMember]
    public bool IsPresetsPanelOpen
    {
        get => isPresetsPanelOpen;
        private set => SetProperty(ref isPresetsPanelOpen, value);
    }

    [DataMember]
    public bool ArePresetsAvailable => Status.FeatureTier >= CodexFeatureTier.Tier164;

    [DataMember]
    public bool ShowPresetsTierNotice => IsPresetsPanelOpen && IsPresetsConnectionReady && !ArePresetsAvailable;

    [DataMember]
    public string PresetsTierNoticeText { get; } = PresetsTierRequiredText;

    [DataMember]
    public bool ShowPresetSlots => IsPresetsPanelOpen && ArePresetsAvailable && PresetSlots.Count > 0;

    [DataMember]
    public bool IsPresetsLoading
    {
        get => isPresetsLoading;
        private set => SetProperty(ref isPresetsLoading, value);
    }

    [DataMember]
    public string PresetsStatusText
    {
        get => presetsStatusText;
        private set
        {
            if (SetProperty(ref presetsStatusText, value))
            {
                OnPropertyChanged(nameof(HasPresetsStatus));
            }
        }
    }

    [DataMember]
    public bool HasPresetsStatus => presetsStatusText.Length > 0;

    [DataMember]
    public bool IsPresetEditorOpen
    {
        get => isPresetEditorOpen;
        private set => SetProperty(ref isPresetEditorOpen, value);
    }

    [DataMember]
    public string PresetEditorTitle => $"Preset {presetEditorSlot}";

    [DataMember]
    public string PresetEditorInput
    {
        get => presetEditorInput;
        set
        {
            if (SetProperty(ref presetEditorInput, value ?? string.Empty))
            {
                SavePresetCommand.RaiseCanExecuteChanged();
            }
        }
    }

    [DataMember]
    public string PresetEditorLabel
    {
        get => presetEditorLabel;
        set => SetProperty(ref presetEditorLabel, value ?? string.Empty);
    }

    [DataMember]
    public bool HasPresetConfirmation => presetConfirmationKind != PresetConfirmationKind.None;

    [DataMember]
    public string PresetConfirmationText
    {
        get => presetConfirmationText;
        private set => SetProperty(ref presetConfirmationText, value);
    }

    [DataMember]
    public string PresetConfirmationActionText => presetConfirmationKind switch
    {
        PresetConfirmationKind.Delete => "Delete",
        PresetConfirmationKind.Recall => "Replace draft",
        _ => "Replace",
    };

    [DataMember]
    public AsyncCommand OpenPresetsCommand { get; private set; } = null!;

    [DataMember]
    public AsyncCommand ClosePresetsCommand { get; private set; } = null!;

    [DataMember]
    public AsyncCommand RefreshPresetsCommand { get; private set; } = null!;

    [DataMember]
    public AsyncCommand SavePresetCommand { get; private set; } = null!;

    [DataMember]
    public AsyncCommand CancelPresetEditCommand { get; private set; } = null!;

    [DataMember]
    public AsyncCommand ConfirmPresetCommand { get; private set; } = null!;

    [DataMember]
    public AsyncCommand CancelPresetConfirmationCommand { get; private set; } = null!;

    private bool IsPresetsConnectionReady
        => Status.State is WorkerConnectionState.Ready or WorkerConnectionState.Busy or WorkerConnectionState.WaitingForApproval;

    private void InitializePresetCommands()
    {
        OpenPresetsCommand = new AsyncCommand(() => OpenPresetsAsync());
        ClosePresetsCommand = new AsyncCommand(ClosePresetsAsync, () => IsPresetsPanelOpen);
        RefreshPresetsCommand = new AsyncCommand(RefreshPresetsAsync, () => IsPresetsPanelOpen && ArePresetsAvailable && !IsPresetsLoading);
        SavePresetCommand = new AsyncCommand(SavePresetAsync, () => IsPresetEditorOpen && !isPresetSaving && !HasPresetConfirmation);
        CancelPresetEditCommand = new AsyncCommand(CancelPresetEditAsync, () => IsPresetEditorOpen && !isPresetSaving);
        ConfirmPresetCommand = new AsyncCommand(ConfirmPresetAsync, () => HasPresetConfirmation && !isPresetSaving);
        CancelPresetConfirmationCommand = new AsyncCommand(CancelPresetConfirmationAsync, () => HasPresetConfirmation);
    }

    private async Task<bool> ExecutePresetsAsync()
    {
        await OpenPresetsAsync().ConfigureAwait(false);
        return true;
    }

    private async Task OpenPresetsAsync()
    {
        await OnUiAsync(() =>
        {
            IsPresetsPanelOpen = true;
            RaisePresetStates();
        }).ConfigureAwait(false);
        if (ArePresetsAvailable)
        {
            await RefreshPresetsAsync().ConfigureAwait(false);
        }
    }

    private Task ClosePresetsAsync()
        => OnUiAsync(() =>
        {
            IsPresetsPanelOpen = false;
            CloseEditorAndConfirmation();
            PresetsStatusText = string.Empty;
            RaisePresetStates();
        });

    private async Task RefreshPresetsAsync()
    {
        OwnerSnapshot owner = CaptureOwnerSnapshot();
        if (!ArePresetsAvailable)
        {
            return;
        }

        long sequence = Interlocked.Increment(ref presetListSequence);
        await OnUiAsync(() =>
        {
            IsPresetsLoading = true;
            RaisePresetStates();
        }).ConfigureAwait(false);
        ListPresetsResult? result = null;
        string? failure = null;
        try
        {
            result = await bridge.ListPresetsAsync(StampOwner(new ListPresetsRequest(), owner), lifetime.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            ExtensionDiagnostics.Write("Reading presets failed", ex);
            failure = "The presets could not be read. See diagnostics.log.";
        }

        await OnUiAsync(() =>
        {
            // A newer read, an owner change, or a connection change makes this result stale.
            if (sequence != Volatile.Read(ref presetListSequence) || !IsCurrentOwner(owner))
            {
                return;
            }

            IsPresetsLoading = false;
            if (result is null || !result.IsSupported)
            {
                PresetsStatusText = ToSafePresetStatus(failure ?? result?.UnavailableReason ?? PresetsTierRequiredText);
            }
            else
            {
                ApplyPresetSlots(result);
            }

            RaisePresetStates();
        }).ConfigureAwait(false);
    }

    private void ApplyPresetSlots(ListPresetsResult result)
    {
        presetUserLayerVersion = result.UserLayerVersion;
        PresetSlots.Clear();
        var bySlot = result.Slots.Where(static slot => PresetRules.IsSlot(slot.Slot))
            .GroupBy(static slot => slot.Slot, StringComparer.Ordinal)
            .ToDictionary(static group => group.Key, static group => group.First(), StringComparer.Ordinal);
        foreach (string slot in PresetRules.Slots)
        {
            PresetSlotInfo info = bySlot.TryGetValue(slot, out PresetSlotInfo? found)
                ? found
                : new PresetSlotInfo { Slot = slot, SourceLayer = PresetLayerKind.None, CanEdit = true };
            PresetSlots.Add(new PresetSlotViewModel(info, markdown, RecallPresetAsync, EditPresetAsync, DeletePresetAsync));
        }
    }

    private Task EditPresetAsync(PresetSlotViewModel slot)
        => OnUiAsync(() =>
        {
            if (!slot.CanEdit)
            {
                return;
            }

            ClearPresetConfirmation();
            presetEditorSlot = slot.Slot;
            presetEditorInput = slot.RawInput ?? string.Empty;
            presetEditorLabel = slot.RawLabel ?? string.Empty;
            IsPresetEditorOpen = true;
            PresetsStatusText = string.Empty;
            OnPropertyChanged(nameof(PresetEditorTitle));
            OnPropertyChanged(nameof(PresetEditorInput));
            OnPropertyChanged(nameof(PresetEditorLabel));
            RaisePresetStates();
        });

    private async Task SavePresetAsync()
    {
        string? error = PresetRules.ValidateInput(presetEditorInput);
        if (error is not null)
        {
            await OnUiAsync(() => PresetsStatusText = error).ConfigureAwait(false);
            return;
        }

        PresetSlotViewModel? current = PresetSlots.FirstOrDefault(slot => string.Equals(slot.Slot, presetEditorSlot, StringComparison.Ordinal));
        if (current?.IsDefined == true)
        {
            await OnUiAsync(() => BeginPresetConfirmation(
                PresetConfirmationKind.Replace,
                current,
                $"Replace preset {current.Slot} ({current.Title}) in your user configuration?")).ConfigureAwait(false);
            return;
        }

        await WritePresetAsync(presetEditorSlot, presetEditorInput, presetEditorLabel).ConfigureAwait(false);
    }

    private Task CancelPresetEditAsync()
        => OnUiAsync(() =>
        {
            CloseEditorAndConfirmation();
            RaisePresetStates();
        });

    private Task DeletePresetAsync(PresetSlotViewModel slot)
        => OnUiAsync(() =>
        {
            if (slot.CanDelete)
            {
                BeginPresetConfirmation(
                    PresetConfirmationKind.Delete,
                    slot,
                    $"Delete preset {slot.Slot} ({slot.Title}) from your user configuration? The Codex CLI shares this preset.");
            }
        });

    private Task RecallPresetAsync(PresetSlotViewModel slot)
        => OnUiAsync(() =>
        {
            if (!slot.IsDefined)
            {
                return;
            }

            if (ComposerText.Length > 0 || SlashCommands.HasActiveCommand)
            {
                BeginPresetConfirmation(
                    PresetConfirmationKind.Recall,
                    slot,
                    $"Replace the current draft with preset {slot.Slot} ({slot.Title})? The preset is not sent until you send it.");
                return;
            }

            ApplyPresetRecall(slot);
        });

    private async Task ConfirmPresetAsync()
    {
        PresetConfirmationKind kind = presetConfirmationKind;
        PresetSlotViewModel? slot = presetConfirmationSlot;
        if (slot is null)
        {
            return;
        }

        switch (kind)
        {
            case PresetConfirmationKind.Recall:
                await OnUiAsync(() =>
                {
                    ClearPresetConfirmation();
                    ApplyPresetRecall(slot);
                }).ConfigureAwait(false);
                break;
            case PresetConfirmationKind.Replace:
                await WritePresetAsync(slot.Slot, presetEditorInput, presetEditorLabel).ConfigureAwait(false);
                break;
            case PresetConfirmationKind.Delete:
                await WritePresetAsync(slot.Slot, null, null).ConfigureAwait(false);
                break;
        }
    }

    private Task CancelPresetConfirmationAsync()
        => OnUiAsync(() =>
        {
            ClearPresetConfirmation();
            RaisePresetStates();
        });

    private void ApplyPresetRecall(PresetSlotViewModel slot)
    {
        if (SlashCommands.HasActiveCommand)
        {
            SlashCommands.DiscardActiveCommand();
        }

        SetComposerText(slot.RawInput ?? string.Empty);
        IsPresetsPanelOpen = false;
        CloseEditorAndConfirmation();
        PresetsStatusText = string.Empty;
        RaisePresetStates();
        SlashCommands.ShowStatus($"Preset {slot.Slot} was placed in the composer. Review it and send it when ready.");
    }

    private async Task WritePresetAsync(string slot, string? input, string? label)
    {
        OwnerSnapshot owner = CaptureOwnerSnapshot();
        await OnUiAsync(() =>
        {
            isPresetSaving = true;
            RaisePresetStates();
        }).ConfigureAwait(false);
        WritePresetResult? result = null;
        try
        {
            result = await bridge.WritePresetAsync(
                StampOwner(new WritePresetRequest
                {
                    Slot = slot,
                    Input = input,
                    Label = label,
                    ExpectedVersion = presetUserLayerVersion,
                }, owner),
                lifetime.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            ExtensionDiagnostics.Write("Writing a preset failed", ex);
        }

        bool reload = false;
        await OnUiAsync(() =>
        {
            isPresetSaving = false;
            if (!IsCurrentOwner(owner))
            {
                RaisePresetStates();
                return;
            }

            ClearPresetConfirmation();
            if (result is null)
            {
                PresetsStatusText = "The preset could not be saved. See diagnostics.log.";
            }
            else if (!result.IsSupported)
            {
                PresetsStatusText = ToSafePresetStatus(result.UnavailableReason ?? PresetsTierRequiredText);
            }
            else
            {
                switch (result.Outcome)
                {
                    case WritePresetOutcome.Written:
                        CloseEditorAndConfirmation();
                        PresetsStatusText = input is null ? $"Preset {slot} was deleted." : $"Preset {slot} was saved.";
                        reload = true;
                        break;
                    case WritePresetOutcome.Conflict:
                        // The editor keeps its contents; the slots are re-read and nothing is retried.
                        PresetsStatusText = ToSafePresetStatus(result.Message ?? "The presets changed since they were read.");
                        reload = true;
                        break;
                    default:
                        PresetsStatusText = ToSafePresetStatus(result.Message ?? "The preset could not be saved.");
                        break;
                }
            }

            RaisePresetStates();
        }).ConfigureAwait(false);
        if (reload)
        {
            await RefreshPresetsAsync().ConfigureAwait(false);
        }
    }

    private string ToSafePresetStatus(string message) => markdown.ToSafeText(message).Trim();

    private void BeginPresetConfirmation(PresetConfirmationKind kind, PresetSlotViewModel slot, string text)
    {
        presetConfirmationKind = kind;
        presetConfirmationSlot = slot;
        PresetConfirmationText = text;
        OnPropertyChanged(nameof(HasPresetConfirmation));
        OnPropertyChanged(nameof(PresetConfirmationActionText));
        RaisePresetStates();
    }

    private void ClearPresetConfirmation()
    {
        if (presetConfirmationKind == PresetConfirmationKind.None)
        {
            return;
        }

        presetConfirmationKind = PresetConfirmationKind.None;
        presetConfirmationSlot = null;
        PresetConfirmationText = string.Empty;
        OnPropertyChanged(nameof(HasPresetConfirmation));
        OnPropertyChanged(nameof(PresetConfirmationActionText));
    }

    private void CloseEditorAndConfirmation()
    {
        ClearPresetConfirmation();
        IsPresetEditorOpen = false;
        presetEditorSlot = string.Empty;
        presetEditorInput = string.Empty;
        presetEditorLabel = string.Empty;
        OnPropertyChanged(nameof(PresetEditorTitle));
        OnPropertyChanged(nameof(PresetEditorInput));
        OnPropertyChanged(nameof(PresetEditorLabel));
    }

    // Slots and the user-layer version belong to one connection and are never reused across one.
    private void OnPresetsStatusChanged(long? previousGeneration, WorkerStatus value)
    {
        if (previousGeneration != value.Target?.Generation || value.FeatureTier < CodexFeatureTier.Tier164)
        {
            ResetPresets();
        }

        RaisePresetStates();
    }

    private void ResetPresets()
    {
        Interlocked.Increment(ref presetListSequence);
        presetUserLayerVersion = null;
        PresetSlots.Clear();
        IsPresetsLoading = false;
        isPresetSaving = false;
        CloseEditorAndConfirmation();
        PresetsStatusText = string.Empty;
    }

    private void RaisePresetStates()
    {
        OnPropertyChanged(nameof(ArePresetsAvailable));
        OnPropertyChanged(nameof(ShowPresetsTierNotice));
        OnPropertyChanged(nameof(ShowPresetSlots));
        ClosePresetsCommand?.RaiseCanExecuteChanged();
        RefreshPresetsCommand?.RaiseCanExecuteChanged();
        SavePresetCommand?.RaiseCanExecuteChanged();
        CancelPresetEditCommand?.RaiseCanExecuteChanged();
        ConfirmPresetCommand?.RaiseCanExecuteChanged();
        CancelPresetConfirmationCommand?.RaiseCanExecuteChanged();
    }
}
