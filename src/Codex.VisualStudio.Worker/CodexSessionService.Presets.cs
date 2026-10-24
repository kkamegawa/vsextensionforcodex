using System.Text.Json;
using Codex.AppServer.Protocol;
using Codex.VisualStudio.Contracts;

namespace Codex.VisualStudio.Worker;

public partial interface ICodexSessionService
{
    Task<SaveFeedbackResult> SaveFeedbackAsync(SaveFeedbackRequest request, CancellationToken cancellationToken)
        => throw new NotSupportedException();

    Task<ListPresetsResult> ListPresetsAsync(CancellationToken cancellationToken)
        => throw new NotSupportedException();

    Task<WritePresetResult> WritePresetAsync(WritePresetRequest request, CancellationToken cancellationToken)
        => throw new NotSupportedException();
}

// 0.164.0 operations: diagnostic ZIP saving and presets shared with the CLI through tui.presets.
public sealed partial class CodexSessionService
{
    private const int MaxSavePathLength = 1024;
    private const int MaxPresetLabelLength = 256;
    private const string PresetKeyPrefix = "tui.presets.";

    private const string Tier164Required = "This feature requires Codex CLI 0.164.0 or later.";

    public async Task<SaveFeedbackResult> SaveFeedbackAsync(SaveFeedbackRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (RequireContext().FeatureTier < CodexFeatureTier.Tier164)
        {
            return Unsupported<SaveFeedbackResult>(Tier164Required);
        }

        ValidateFeedbackClassification(request.Classification);
        ValidateFeedbackReason(request.Reason);
        string savePath = request.SavePath?.Trim() ?? string.Empty;
        if (savePath.Length == 0
            || savePath.Length > MaxSavePathLength
            || savePath.Any(char.IsControl)
            || !ServerPath.TryCreate(savePath, out _))
        {
            throw new ArgumentException("The diagnostic ZIP path must be an absolute path on the app-server host.", nameof(request));
        }

        OperationCallResult call;
        try
        {
            call = await TrySendOperationAsync(
                "feedback/save",
                new
                {
                    classification = request.Classification,
                    reason = request.Reason,
                    includeLogs = request.IncludeLogs,
                    threadId = request.ThreadId,
                    savePath,
                    extraLogFiles = (string[]?)null,
                },
                TimeSpan.FromSeconds(120),
                cancellationToken).ConfigureAwait(false);
        }
        catch (JsonRpcRemoteException ex)
        {
            return new SaveFeedbackResult
            {
                Outcome = SaveFeedbackOutcome.Failed,
                Message = redactor.Redact($"The app-server did not save the diagnostic ZIP: {BoundMessage(ex.Message)}"),
            };
        }
        catch (Exception ex) when (ex is JsonRpcConnectionClosedException
            || (ex is OperationCanceledException && !cancellationToken.IsCancellationRequested))
        {
            // The request may have reached the server; never resend it automatically.
            return new SaveFeedbackResult
            {
                Outcome = SaveFeedbackOutcome.Unconfirmed,
                Message = "The connection ended before the app-server confirmed the save. Check the server path before saving again.",
            };
        }

        if (!call.IsSupported)
        {
            return Unsupported<SaveFeedbackResult>("Saving a diagnostic ZIP is not supported by this app-server.");
        }

        string? savedPath = GetString(call.Result, "savedPath");
        return new SaveFeedbackResult
        {
            Outcome = SaveFeedbackOutcome.Saved,
            SavedPath = savedPath is not null && savedPath.Length <= MaxSavePathLength && ServerPath.TryCreate(savedPath, out _)
                ? redactor.Redact(savedPath)
                : null,
        };
    }

    public async Task<ListPresetsResult> ListPresetsAsync(CancellationToken cancellationToken)
    {
        if (RequireContext().FeatureTier < CodexFeatureTier.Tier164)
        {
            return Unsupported<ListPresetsResult>(Tier164Required);
        }

        PresetLayers? layers = await ReadPresetLayersAsync(cancellationToken).ConfigureAwait(false);
        if (layers is null)
        {
            return Unsupported<ListPresetsResult>("Reading presets is not supported by this app-server.");
        }

        return new ListPresetsResult
        {
            Slots = PresetRules.Slots.Select(layers.Describe).ToArray(),
            UserLayerVersion = layers.UserLayerVersion,
        };
    }

    public async Task<WritePresetResult> WritePresetAsync(WritePresetRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (RequireContext().FeatureTier < CodexFeatureTier.Tier164)
        {
            return Unsupported<WritePresetResult>(Tier164Required);
        }

        if (!PresetRules.IsSlot(request.Slot))
        {
            return Rejected("The preset slot must be 1 through 9 or 0.");
        }

        bool delete = request.Input is null;
        if (!delete && PresetRules.ValidateInput(request.Input) is { } inputError)
        {
            return Rejected(inputError);
        }

        string label = PresetRules.NormalizeLabel(request.Label);
        if (label.Length > MaxPresetLabelLength || label.Any(char.IsControl))
        {
            return Rejected("The preset label is too long or contains control characters.");
        }

        // Re-read the layers so the edit rules hold even if the Extension's view is stale.
        PresetLayers? layers = await ReadPresetLayersAsync(cancellationToken).ConfigureAwait(false);
        if (layers is null)
        {
            return Unsupported<WritePresetResult>("Reading presets is not supported by this app-server.");
        }

        if (!string.Equals(layers.UserLayerVersion, request.ExpectedVersion, StringComparison.Ordinal))
        {
            return new WritePresetResult
            {
                Outcome = WritePresetOutcome.Conflict,
                Message = "The presets changed since they were read. Review the current values and save again.",
            };
        }

        PresetSlotInfo current = layers.Describe(request.Slot);
        if (delete ? !current.CanDelete : !current.CanEdit)
        {
            return Rejected(delete
                ? "This preset is also defined outside your user configuration, so deleting it would reveal another value. Change it where it is defined."
                : "This preset is defined by a higher-priority configuration layer and cannot be edited here.");
        }

        object? value = delete ? null : new { input = request.Input, label };
        try
        {
            OperationCallResult call = await TrySendOperationAsync(
                "config/batchWrite",
                new
                {
                    edits = new[]
                    {
                        new { keyPath = PresetKeyPrefix + request.Slot, value, mergeStrategy = "replace" },
                    },
                    expectedVersion = request.ExpectedVersion,
                    filePath = (string?)null,
                },
                TimeSpan.FromSeconds(30),
                cancellationToken).ConfigureAwait(false);
            if (!call.IsSupported)
            {
                return Unsupported<WritePresetResult>("Writing presets is not supported by this app-server.");
            }
        }
        catch (JsonRpcRemoteException ex)
        {
            string? code = ex.ErrorData is { ValueKind: JsonValueKind.Object } data
                ? GetString(data, "config_write_error_code")
                : null;
            return new WritePresetResult
            {
                Outcome = code == "configVersionConflict" ? WritePresetOutcome.Conflict : WritePresetOutcome.Failed,
                Message = code == "configVersionConflict"
                    ? "The presets changed since they were read. Review the current values and save again."
                    : redactor.Redact($"The app-server rejected the preset: {BoundMessage(ex.Message)}"),
            };
        }

        return new WritePresetResult { Outcome = WritePresetOutcome.Written };

        static WritePresetResult Rejected(string message)
            => new() { Outcome = WritePresetOutcome.Rejected, Message = message };
    }

    private async Task<PresetLayers?> ReadPresetLayersAsync(CancellationToken cancellationToken)
    {
        OperationCallResult call = await TrySendOperationAsync(
            "config/read",
            new { includeLayers = true, cwd = MapLocalPathForServer(options.WorkingDirectory) },
            TimeSpan.FromSeconds(30),
            cancellationToken).ConfigureAwait(false);
        return call.IsSupported ? PresetLayers.Read(call.Result) : null;
    }

    private static string BoundMessage(string message)
    {
        string singleLine = new(message.Select(character => char.IsControl(character) ? ' ' : character).ToArray());
        return singleLine.Length > 512 ? singleLine[..512] + "..." : singleLine;
    }

    // config/read layers in high-to-low order. The effective config schema has no tui key, so
    // slots are read from each layer's raw config.
    internal sealed class PresetLayers
    {
        private readonly List<(PresetLayerKind Kind, IReadOnlyDictionary<string, JsonElement> Slots)> layers = [];

        public string? UserLayerVersion { get; private set; }

        public static PresetLayers Read(JsonElement result)
        {
            var presetLayers = new PresetLayers();
            if (!result.TryGetProperty("layers", out JsonElement layerArray) || layerArray.ValueKind != JsonValueKind.Array)
            {
                return presetLayers;
            }

            foreach (JsonElement layer in layerArray.EnumerateArray())
            {
                if (layer.ValueKind != JsonValueKind.Object
                    || (layer.TryGetProperty("disabledReason", out JsonElement disabled) && disabled.ValueKind != JsonValueKind.Null))
                {
                    continue;
                }

                PresetLayerKind kind = ReadLayerKind(layer);
                if (kind == PresetLayerKind.User && presetLayers.UserLayerVersion is null)
                {
                    presetLayers.UserLayerVersion = GetString(layer, "version");
                }

                var slots = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
                if (layer.TryGetProperty("config", out JsonElement config)
                    && config.ValueKind == JsonValueKind.Object
                    && config.TryGetProperty("tui", out JsonElement tui)
                    && tui.ValueKind == JsonValueKind.Object
                    && tui.TryGetProperty("presets", out JsonElement presets)
                    && presets.ValueKind == JsonValueKind.Object)
                {
                    foreach (JsonProperty preset in presets.EnumerateObject())
                    {
                        if (PresetRules.IsSlot(preset.Name) && preset.Value.ValueKind != JsonValueKind.Null)
                        {
                            slots[preset.Name] = preset.Value.Clone();
                        }
                    }
                }

                presetLayers.layers.Add((kind, slots));
            }

            return presetLayers;
        }

        public PresetSlotInfo Describe(string slot)
        {
            var defining = layers.Where(layer => layer.Slots.ContainsKey(slot)).ToList();
            if (defining.Count == 0)
            {
                return new PresetSlotInfo { Slot = slot, SourceLayer = PresetLayerKind.None, CanEdit = true, CanDelete = false };
            }

            (PresetLayerKind kind, IReadOnlyDictionary<string, JsonElement> slots) = defining[0];
            JsonElement preset = slots[slot];
            string? input = preset.ValueKind == JsonValueKind.Object ? GetString(preset, "input") : null;
            string? label = preset.ValueKind == JsonValueKind.Object ? GetString(preset, "label") : null;
            return new PresetSlotInfo
            {
                Slot = slot,
                Input = input is not null && PresetRules.ValidateInput(input) is null ? input : null,
                Label = label is not null && label.Length <= MaxPresetLabelLength ? label.Trim() : null,
                SourceLayer = kind,
                CanEdit = kind == PresetLayerKind.User,
                CanDelete = defining.All(layer => layer.Kind == PresetLayerKind.User),
            };
        }

        private static PresetLayerKind ReadLayerKind(JsonElement layer)
        {
            string? type = layer.TryGetProperty("name", out JsonElement name) && name.ValueKind == JsonValueKind.Object
                ? GetString(name, "type")
                : null;
            return type switch
            {
                "user" => PresetLayerKind.User,
                "project" => PresetLayerKind.Project,
                "system" => PresetLayerKind.System,
                "mdm" or "enterpriseManaged" or "legacyManagedConfigTomlFromFile" or "legacyManagedConfigTomlFromMdm" => PresetLayerKind.Managed,
                "sessionFlags" => PresetLayerKind.SessionFlags,
                _ => PresetLayerKind.Other,
            };
        }
    }
}
