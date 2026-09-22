using System.Collections.ObjectModel;
using System.Runtime.Serialization;

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

    internal RemoteProfileViewModel(RemoteConnectionProfile profile)
    {
        name = profile.Name;
        endpoint = profile.Endpoint;
        tokenFilePath = profile.TokenFilePath ?? string.Empty;
        localRoot = profile.LocalRoot;
        serverRoot = profile.ServerRoot;
        isEnabled = profile.Enabled;
    }

    [DataMember]
    public string Name
    {
        get => name;
        set
        {
            if (SetProperty(ref name, value))
            {
                OnPropertyChanged(nameof(DisplayText));
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
            }
        }
    }

    [DataMember]
    public string TokenFilePath { get => tokenFilePath; set => SetProperty(ref tokenFilePath, value); }

    [DataMember]
    public string LocalRoot { get => localRoot; set => SetProperty(ref localRoot, value); }

    [DataMember]
    public string ServerRoot { get => serverRoot; set => SetProperty(ref serverRoot, value); }

    [DataMember]
    public bool IsEnabled { get => isEnabled; set => SetProperty(ref isEnabled, value); }

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

    internal RemoteProfilesPresentationViewModel(ExtensionSettings settings, IExtensionSettingsStore store)
    {
        this.settings = settings;
        this.store = store;
        foreach (RemoteConnectionProfile profile in settings.RemoteProfiles.Where(static p => p is not null))
        {
            Profiles.Add(new RemoteProfileViewModel(profile));
        }

        SelectedProfile = Profiles.FirstOrDefault(profile =>
            string.Equals(profile.Name, settings.SelectedRemoteProfileName, StringComparison.Ordinal));
        AddCommand = new AsyncCommand(AddProfileAsync);
        RemoveCommand = new AsyncCommand(RemoveProfileAsync, () => SelectedProfile is not null);
        SaveCommand = new AsyncCommand(SaveProfileAsync, () => SelectedProfile is not null);
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
                    settings.SelectedRemoteProfileName = value.Name;
                }
                else
                {
                    settings.SelectedRemoteProfileName = null;
                }

                SaveSettings();
                OnPropertyChanged(nameof(HasSelection));
            }
        }
    }

    [DataMember]
    public bool HasSelection => SelectedProfile is not null;

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

    private Task AddProfileAsync()
    {
        var profile = new RemoteProfileViewModel(new RemoteConnectionProfile
        {
            Name = "Remote app-server",
            Endpoint = "wss://",
            Enabled = false,
        });
        Profiles.Add(profile);
        SelectedProfile = profile;
        return SaveProfileAsync();
    }

    private Task RemoveProfileAsync()
    {
        if (SelectedProfile is null)
        {
            return Task.CompletedTask;
        }

        int index = Profiles.IndexOf(SelectedProfile);
        Profiles.Remove(SelectedProfile);
        SelectedProfile = index >= 0 && index < Profiles.Count ? Profiles[index] : Profiles.LastOrDefault();
        SaveSettings();
        RemoveCommand.RaiseCanExecuteChanged();
        SaveCommand.RaiseCanExecuteChanged();
        return Task.CompletedTask;
    }

    private Task SaveProfileAsync()
    {
        if (SelectedProfile is null)
        {
            return Task.CompletedTask;
        }

        if (!TryValidate(SelectedProfile, out string error))
        {
            StatusText = error;
            return Task.CompletedTask;
        }

        if (Profiles.GroupBy(profile => profile.Name.Trim(), StringComparer.OrdinalIgnoreCase)
            .Any(group => group.Count() > 1))
        {
            StatusText = "Profile names must be unique.";
            return Task.CompletedTask;
        }

        settings.RemoteProfiles = Profiles.Select(static profile => profile.ToSettings()).ToList();
        settings.SelectedRemoteProfileName = SelectedProfile.Name.Trim();
        SaveSettings();
        StatusText = "Remote profile saved. Reconnect to apply it.";
        return Task.CompletedTask;
    }

    private void SaveSettings()
    {
        settings.RemoteProfiles = Profiles.Select(static profile => profile.ToSettings()).ToList();
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

        if (!Uri.TryCreate(profile.Endpoint, UriKind.Absolute, out Uri? endpoint)
            || !string.Equals(endpoint.Scheme, "wss", StringComparison.OrdinalIgnoreCase)
            && !IsLoopbackWebSocket(endpoint))
        {
            error = "Use a wss endpoint. Plain ws is allowed only for loopback.";
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

        return true;
    }

    private static bool IsLoopbackWebSocket(Uri endpoint)
        => string.Equals(endpoint.Scheme, "ws", StringComparison.OrdinalIgnoreCase)
            && (string.Equals(endpoint.Host, "localhost", StringComparison.OrdinalIgnoreCase)
                || Uri.CheckHostName(endpoint.Host) == UriHostNameType.IPv4 && System.Net.IPAddress.TryParse(endpoint.Host, out System.Net.IPAddress? address) && System.Net.IPAddress.IsLoopback(address));
}
