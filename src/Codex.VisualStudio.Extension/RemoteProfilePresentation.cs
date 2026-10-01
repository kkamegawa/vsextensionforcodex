using System.Collections.ObjectModel;
using System.Runtime.Serialization;
using Codex.VisualStudio.Contracts;

namespace Codex.VisualStudio.Extension;

/// <summary>
/// Remote app-server connection metadata exposed to Remote UI. Authentication material is
/// deliberately represented by a file path; token contents never cross the presentation boundary.
/// </summary>
[DataContract]
public sealed class RemoteProfileViewModel : ObservableObject
{
    private string name;
    private string endpoint;
    private string tokenFilePath;
    private string localRoot;
    private string serverRoot;
    private bool isEnabled;
    private bool isSelected;

    internal RemoteProfileViewModel(RemoteConnectionProfile profile, bool isPersisted)
    {
        name = profile.Name;
        endpoint = profile.Endpoint;
        tokenFilePath = profile.TokenFilePath ?? string.Empty;
        localRoot = profile.LocalRoot;
        serverRoot = profile.ServerRoot;
        isEnabled = profile.Enabled;
        PersistedProfile = isPersisted ? profile.Clone() : null;
    }

    private RemoteConnectionProfile? persistedProfile;

    // The last validated values written to settings. Edits stay in the view model until Save
    // validates them, so selection changes or removals never persist unvalidated input.
    internal RemoteConnectionProfile? PersistedProfile
    {
        get => persistedProfile;
        set
        {
            persistedProfile = value;
            OnPropertyChanged(nameof(RowStatusText));
        }
    }

    // True when the editor holds values that Save has not validated and persisted yet.
    internal bool HasUnsavedChanges
    {
        get
        {
            RemoteConnectionProfile current = ToSettings();
            return persistedProfile is null
                || !string.Equals(current.Name, persistedProfile.Name, StringComparison.Ordinal)
                || !string.Equals(current.Endpoint, persistedProfile.Endpoint, StringComparison.Ordinal)
                || !string.Equals(current.TokenFilePath, persistedProfile.TokenFilePath, StringComparison.Ordinal)
                || !string.Equals(current.LocalRoot, persistedProfile.LocalRoot, StringComparison.Ordinal)
                || !string.Equals(current.ServerRoot, persistedProfile.ServerRoot, StringComparison.Ordinal)
                || current.Enabled != persistedProfile.Enabled;
        }
    }

    // Short row marker in the profile list: unsaved edits win over the disabled state because
    // they must be saved before anything else applies.
    [DataMember]
    public string RowStatusText
        => persistedProfile is null ? "Not saved"
            : HasUnsavedChanges ? "Unsaved changes"
            : !persistedProfile.Enabled ? "Disabled"
            : string.Empty;

    [DataMember]
    public string Name
    {
        get => name;
        set
        {
            if (SetProperty(ref name, value))
            {
                OnPropertyChanged(nameof(DisplayText));
                OnPropertyChanged(nameof(RowStatusText));
            }
        }
    }

    [DataMember]
    public string Endpoint
    {
        get => endpoint;
        set
        {
            if (SetProperty(ref endpoint, value))
            {
                OnPropertyChanged(nameof(DisplayText));
                OnPropertyChanged(nameof(RowStatusText));
            }
        }
    }

    [DataMember]
    public string TokenFilePath
    {
        get => tokenFilePath;
        set
        {
            if (SetProperty(ref tokenFilePath, value))
            {
                OnPropertyChanged(nameof(RowStatusText));
            }
        }
    }

    [DataMember]
    public string LocalRoot
    {
        get => localRoot;
        set
        {
            if (SetProperty(ref localRoot, value))
            {
                OnPropertyChanged(nameof(RowStatusText));
            }
        }
    }

    [DataMember]
    public string ServerRoot
    {
        get => serverRoot;
        set
        {
            if (SetProperty(ref serverRoot, value))
            {
                OnPropertyChanged(nameof(RowStatusText));
            }
        }
    }

    [DataMember]
    public bool IsEnabled
    {
        get => isEnabled;
        set
        {
            if (SetProperty(ref isEnabled, value))
            {
                OnPropertyChanged(nameof(RowStatusText));
            }
        }
    }

    [DataMember]
    public bool IsSelected { get => isSelected; internal set => SetProperty(ref isSelected, value); }

    [DataMember]
    public string DisplayText => string.IsNullOrWhiteSpace(Name) ? Endpoint : Name;

    internal RemoteConnectionProfile ToSettings() => new()
    {
        Name = Name.Trim(),
        Endpoint = Endpoint.Trim(),
        TokenFilePath = string.IsNullOrWhiteSpace(TokenFilePath) ? null : TokenFilePath.Trim(),
        LocalRoot = LocalRoot.Trim(),
        ServerRoot = ServerRoot.Trim(),
        Enabled = IsEnabled,
    };
}

[DataContract]
public sealed class RemoteProfilesPresentationViewModel : ObservableObject
{
    private readonly ExtensionSettings settings;
    private readonly IExtensionSettingsStore store;
    private RemoteProfileViewModel? selectedProfile;
    private string statusText = string.Empty;

    // True while the caller already holds OperationGate, so a selection change persists inline
    // instead of queueing behind the operation that is changing it.
    private bool persistInline;

    internal RemoteProfilesPresentationViewModel(
        ExtensionSettings settings,
        IExtensionSettingsStore store,
        SemaphoreSlim? operationGate = null)
    {
        this.settings = settings;
        this.store = store;
        OperationGate = operationGate ?? new SemaphoreSlim(1, 1);
        foreach (RemoteConnectionProfile profile in settings.RemoteProfiles.Where(static p => p is not null))
        {
            Profiles.Add(new RemoteProfileViewModel(profile, isPersisted: true));
        }

        Profiles.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasNoProfiles));
        AddCommand = new AsyncCommand(AddProfileAsync);
        RemoveCommand = new AsyncCommand(RemoveProfileAsync, () => SelectedProfile is not null);
        SaveCommand = new AsyncCommand(SaveProfileAsync, () => SelectedProfile is not null);

        // Restoring the persisted selection is not a user change, so it must not rewrite settings.
        selectedProfile = Profiles.FirstOrDefault(profile =>
            string.Equals(profile.PersistedProfile?.Name, settings.SelectedRemoteProfileName, StringComparison.Ordinal));
        if (selectedProfile is not null)
        {
            selectedProfile.IsSelected = true;
        }
    }

    [DataMember]
    public ObservableCollection<RemoteProfileViewModel> Profiles { get; } = [];

    [DataMember]
    public RemoteProfileViewModel? SelectedProfile
    {
        get => selectedProfile;
        set
        {
            if (ReferenceEquals(selectedProfile, value))
            {
                return;
            }

            if (selectedProfile is not null)
            {
                selectedProfile.IsSelected = false;
            }

            if (SetProperty(ref selectedProfile, value))
            {
                if (value is not null)
                {
                    value.IsSelected = true;
                }

                // Only a saved profile can be applied; an unsaved selection falls back to local stdio.
                string? selectedName = value?.PersistedProfile?.Name;
                if (persistInline)
                {
                    PersistSelection(selectedName);
                }
                else
                {
                    _ = RunGatedAsync(() => PersistSelection(selectedName));
                }

                OnPropertyChanged(nameof(HasSelection));
            }
        }
    }

    [DataMember]
    public bool HasSelection => SelectedProfile is not null;

    [DataMember]
    public bool HasNoProfiles => Profiles.Count == 0;

    [DataMember]
    public string StatusText
    {
        get => statusText;
        private set => SetProperty(ref statusText, value);
    }

    [DataMember]
    public AsyncCommand AddCommand { get; }

    [DataMember]
    public AsyncCommand RemoveCommand { get; }

    [DataMember]
    public AsyncCommand SaveCommand { get; }

    // Serializes every same-instance settings mutation (selection persistence, Save, rename,
    // enable/disable, delete, explicit switch to local) with reconnect snapshot validation and
    // the Worker RPC dispatch that follows it.
    internal SemaphoreSlim OperationGate { get; }

    // Raised after saved profile metadata changes (Save or removal), so dependent presentation
    // such as a health result can be cleared.
    internal event EventHandler? SavedProfilesChanged;

    // Resolves the saved, enabled selection that a reconnect would apply. The Worker bridge reads
    // the same persisted selection, so an unsaved or disabled profile must be refused here rather
    // than silently falling back to local stdio.
    internal bool TryGetApplicableProfile(out string profileName, out string error)
    {
        profileName = string.Empty;
        error = string.Empty;
        RemoteProfileViewModel? profile = SelectedProfile;
        if (profile is null)
        {
            error = "Select a remote profile first.";
            return false;
        }

        if (profile.HasUnsavedChanges || profile.PersistedProfile is null)
        {
            error = "Save the profile before connecting with it.";
            return false;
        }

        if (!profile.PersistedProfile.Enabled)
        {
            error = "Enable the profile and save it before connecting with it.";
            return false;
        }

        profileName = profile.PersistedProfile.Name;
        return true;
    }

    // The saved profile a new connection will use, or null for local stdio. Mirrors the
    // WorkerBridge rule: only an enabled profile named by the persisted selection applies.
    internal string? AppliedProfileName
        => settings.RemoteProfiles.FirstOrDefault(profile => profile.Enabled
            && string.Equals(profile.Name, settings.SelectedRemoteProfileName, StringComparison.Ordinal))?.Name;

    internal void ClearSelection() => SelectedProfile = null;

    // Call only while holding OperationGate.
    internal void ClearSelectionUnderGate()
    {
        persistInline = true;
        try
        {
            SelectedProfile = null;
        }
        finally
        {
            persistInline = false;
        }
    }

    // The editor row for a saved profile name, if the editor still shows it.
    internal RemoteProfileViewModel? FindByPersistedName(string name)
        => Profiles.FirstOrDefault(profile => string.Equals(profile.PersistedProfile?.Name, name, StringComparison.Ordinal));

    internal void ReportStatus(string text) => StatusText = text;

    private Task AddProfileAsync()
    {
        var profile = new RemoteProfileViewModel(
            new RemoteConnectionProfile
            {
                Name = "Remote app-server",
                Endpoint = "wss://",
                Enabled = false,
            },
            isPersisted: false);
        Profiles.Add(profile);
        SelectedProfile = profile;
        StatusText = "Enter the endpoint, both roots, and the token file, then save the profile.";
        return Task.CompletedTask;
    }

    private async Task RemoveProfileAsync()
    {
        await OperationGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (SelectedProfile is null)
            {
                return;
            }

            int index = Profiles.IndexOf(SelectedProfile);
            Profiles.Remove(SelectedProfile);

            // The removed profile is no longer the selection, so the setter always persists the
            // remaining saved profiles and the new selection.
            persistInline = true;
            try
            {
                SelectedProfile = index >= 0 && index < Profiles.Count ? Profiles[index] : Profiles.LastOrDefault();
            }
            finally
            {
                persistInline = false;
            }
        }
        finally
        {
            OperationGate.Release();
        }

        SavedProfilesChanged?.Invoke(this, EventArgs.Empty);
    }

    private async Task SaveProfileAsync()
    {
        await OperationGate.WaitAsync().ConfigureAwait(false);
        try
        {
            SaveProfileCore();
        }
        finally
        {
            OperationGate.Release();
        }

        SavedProfilesChanged?.Invoke(this, EventArgs.Empty);
    }

    // Save validates metadata only. It never opens or reads the token file; existence,
    // readability, and contents are checked by the Worker on an explicit connection.
    private void SaveProfileCore()
    {
        RemoteProfileViewModel? profile = SelectedProfile;
        if (profile is null)
        {
            return;
        }

        if (!TryValidate(profile, out string error))
        {
            StatusText = error;
            return;
        }

        string name = profile.Name.Trim();
        bool duplicate = Profiles.Any(other => !ReferenceEquals(other, profile)
            && (string.Equals(other.Name.Trim(), name, StringComparison.OrdinalIgnoreCase)
                || string.Equals(other.PersistedProfile?.Name, name, StringComparison.OrdinalIgnoreCase)));
        if (duplicate)
        {
            StatusText = "Profile names must be unique.";
            return;
        }

        profile.PersistedProfile = profile.ToSettings();
        settings.SelectedRemoteProfileName = profile.PersistedProfile.Name;
        PersistSettings();
        StatusText = "Remote profile saved. Reconnect to apply it.";
    }

    private void PersistSelection(string? selectedName)
    {
        settings.SelectedRemoteProfileName = selectedName;
        PersistSettings();
    }

    private async Task RunGatedAsync(Action action)
    {
        try
        {
            await OperationGate.WaitAsync().ConfigureAwait(false);
            try
            {
                action();
            }
            finally
            {
                OperationGate.Release();
            }
        }
        catch (Exception ex)
        {
            ExtensionDiagnostics.Write("Remote profile selection persistence failed", ex);
        }
    }

    private void PersistSettings()
    {
        settings.RemoteProfiles = Profiles
            .Where(static profile => profile.PersistedProfile is not null)
            .Select(static profile => profile.PersistedProfile!.Clone())
            .ToList();
        store.Save(settings);
        RemoveCommand.RaiseCanExecuteChanged();
        SaveCommand.RaiseCanExecuteChanged();
    }

    private static bool TryValidate(RemoteProfileViewModel profile, out string error)
    {
        error = string.Empty;
        if (string.IsNullOrWhiteSpace(profile.Name) || string.IsNullOrWhiteSpace(profile.Endpoint))
        {
            error = "A profile name and endpoint are required.";
            return false;
        }

        // The shared pure policy also used by the Worker and the transport. It performs no DNS
        // lookup; the Worker verifies the exact "localhost" name before connecting.
        RemoteEndpointValidation endpoint = RemoteEndpointPolicy.Validate(profile.Endpoint);
        if (!endpoint.IsValid)
        {
            error = endpoint.Message;
            return false;
        }

        if (string.IsNullOrWhiteSpace(profile.LocalRoot) || string.IsNullOrWhiteSpace(profile.ServerRoot))
        {
            error = "Local and server roots are required for path mapping.";
            return false;
        }

        if (profile.IsEnabled && string.IsNullOrWhiteSpace(profile.TokenFilePath))
        {
            error = "An enabled remote profile requires a token file path.";
            return false;
        }

        if (profile.IsEnabled && !TokenFilePathPolicy.IsSyntacticallyValid(profile.TokenFilePath))
        {
            error = "The token file path must be an absolute path on a local drive (for example C:\\tokens\\app-server.token).";
            return false;
        }

        return true;
    }
}
