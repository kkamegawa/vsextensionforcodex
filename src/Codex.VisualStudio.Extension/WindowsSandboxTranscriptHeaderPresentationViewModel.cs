using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.Serialization;
using Codex.VisualStudio.Contracts;

namespace Codex.VisualStudio.Extension;

[DataContract]
public sealed class WindowsSandboxTranscriptHeaderPresentationViewModel : ObservableObject
{
    private static readonly HashSet<string> ForwardedPropertyNames =
    [
        nameof(IsThreadEmpty),
        nameof(IsWindowsSandboxSetupInProgress),
        nameof(SelectedWindowsSandboxBackendId),
        nameof(SelectedWindowsSandboxMode),
        nameof(Suggestions),
        nameof(ShowWindowsSandboxPanel),
        nameof(ShowWindowsSandboxSetup),
        nameof(WindowsSandboxBackendHelpText),
        nameof(HasWindowsSandboxBackendPending),
        nameof(WindowsSandboxBackendPendingText),
        nameof(WindowsSandboxBackendStatusText),
        nameof(WindowsSandboxSolutionRootText),
        nameof(WindowsSandboxStatusText),
    ];

    private readonly ChatViewModel owner;

    public WindowsSandboxTranscriptHeaderPresentationViewModel(ChatViewModel owner)
    {
        this.owner = owner;
        owner.PropertyChanged += OnOwnerPropertyChanged;
    }

    [DataMember]
    public bool IsThreadEmpty => owner.IsThreadEmpty;

    [DataMember]
    public ObservableCollection<SuggestionChip> Suggestions => owner.Suggestions;

    [DataMember]
    public bool ShowWindowsSandboxPanel => owner.ShowWindowsSandboxPanel;

    [DataMember]
    public bool ShowWindowsSandboxSetup => owner.ShowWindowsSandboxSetup;

    [DataMember]
    public ObservableCollection<WindowsSandboxBackendOption> WindowsSandboxBackends => owner.WindowsSandboxBackends;

    [DataMember]
    public string SelectedWindowsSandboxBackendId
    {
        get => owner.SelectedWindowsSandboxBackendId;
        set => owner.SelectedWindowsSandboxBackendId = value;
    }

    [DataMember]
    public string WindowsSandboxBackendHelpText => owner.WindowsSandboxBackendHelpText;

    [DataMember]
    public bool HasWindowsSandboxBackendPending => owner.HasWindowsSandboxBackendPending;

    [DataMember]
    public string WindowsSandboxBackendPendingText => owner.WindowsSandboxBackendPendingText;

    [DataMember]
    public string WindowsSandboxBackendStatusText => owner.WindowsSandboxBackendStatusText;

    [DataMember]
    public AsyncCommand CheckWindowsSandboxReadinessCommand => owner.CheckWindowsSandboxReadinessCommand;

    [DataMember]
    public string WindowsSandboxSolutionRootText => owner.WindowsSandboxSolutionRootText;

    [DataMember]
    public string WindowsSandboxStatusText => owner.WindowsSandboxStatusText;

    [DataMember]
    public bool IsWindowsSandboxSetupInProgress => owner.IsWindowsSandboxSetupInProgress;

    [DataMember]
    public ObservableCollection<WindowsSandboxSetupMode> WindowsSandboxSetupModes => owner.WindowsSandboxSetupModes;

    [DataMember]
    public WindowsSandboxSetupMode SelectedWindowsSandboxMode
    {
        get => owner.SelectedWindowsSandboxMode;
        set => owner.SelectedWindowsSandboxMode = value;
    }

    [DataMember]
    public AsyncCommand StartWindowsSandboxSetupCommand => owner.StartWindowsSandboxSetupCommand;

    public void Detach()
        => owner.PropertyChanged -= OnOwnerPropertyChanged;

    private void OnOwnerPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (string.IsNullOrEmpty(e.PropertyName))
        {
            OnPropertyChanged(e.PropertyName);
        }
        else if (ForwardedPropertyNames.Contains(e.PropertyName))
        {
            OnPropertyChanged(e.PropertyName);
        }
    }
}
