using System.Collections.ObjectModel;
using System.Runtime.Serialization;
using Codex.VisualStudio.Contracts;
using Microsoft.VisualStudio.Extensibility.UI;

namespace Codex.VisualStudio.Extension;

[DataContract]
public sealed class ShellCommandConfirmationPresentation
{
    internal ShellCommandConfirmationPresentation(ShellCommandConfirmationSnapshot snapshot, SafeMarkdownService markdown)
    {
        ConnectionProfile = markdown.ToSafeText(snapshot.ConnectionProfile ?? "Local").Trim();
        ThreadTitle = markdown.ToSafeText(snapshot.ThreadTitle ?? snapshot.ThreadId).Trim();
        Command = SafeMarkdownService.ToSafeLiteralText(snapshot.Command);
        WorkingDirectory = SafeMarkdownService.ToSafeLiteralText(snapshot.ServerWorkingDirectory ?? "Not reported by the server");
        Timeout = snapshot.TimeoutMs is { } timeout ? $"{timeout} ms" : "Server default";
        UnsandboxedWarning = snapshot.RunsUnsandboxedWithFullAccess
            ? "This command runs unsandboxed with full access on the server. Review the target and command before confirming."
            : "Review the target and exact command before confirming.";
    }

    [DataMember] public string ConnectionProfile { get; }
    [DataMember] public string ThreadTitle { get; }
    [DataMember] public string Command { get; }
    [DataMember] public string WorkingDirectory { get; }
    [DataMember] public string Timeout { get; }
    [DataMember] public string UnsandboxedWarning { get; }
}

[DataContract]
public sealed class PlanStepPresentationViewModel
{
    internal PlanStepPresentationViewModel(PlanStepInfo step, SafeMarkdownService markdown)
    {
        Text = markdown.ToSafeText(step.Text).Trim();
        StatusText = markdown.ToSafeText(step.Status).Trim();
        IsCompleted = string.Equals(step.Status, "completed", StringComparison.OrdinalIgnoreCase)
            || string.Equals(step.Status, "done", StringComparison.OrdinalIgnoreCase);
    }

    [DataMember]
    public string Text { get; }

    [DataMember]
    public bool HasText => !string.IsNullOrWhiteSpace(Text);

    [DataMember]
    public string StatusText { get; }

    [DataMember]
    public bool IsCompleted { get; }
}

[DataContract]
public sealed class AppServerNoticePresentationViewModel
{
    internal AppServerNoticePresentationViewModel(AppServerNotice notice, SafeMarkdownService markdown)
    {
        CoalesceKey = string.Join("\0", notice.Kind, notice.Identity ?? string.Empty);
        KindText = notice.Kind switch
        {
            AppServerNoticeKind.ThreadStatusChanged => "Thread status",
            AppServerNoticeKind.ConfigWarning => "Configuration warning",
            AppServerNoticeKind.ModelRerouted => "Model rerouted",
            AppServerNoticeKind.ModelVerification => "Model verification",
            AppServerNoticeKind.McpToolCallProgress => "MCP tool progress",
            _ => "App Server notice",
        };
        Text = Limit(markdown.ToSafeText(notice.Text).Trim(), 4096);
        IsInformationalOnly = notice.IsInformationalOnly
            || notice.Kind == AppServerNoticeKind.ModelVerification;
    }

    [DataMember]
    public string KindText { get; }

    [DataMember]
    public string Text { get; }

    [DataMember]
    public bool IsInformationalOnly { get; }

    internal string CoalesceKey { get; }

    private static string Limit(string value, int maximum)
        => value.Length <= maximum ? value : string.Concat(value.AsSpan(0, maximum - 1), "…");
}

[DataContract]
public sealed class ArtifactPartPresentationViewModel : ObservableObject
{
    private readonly Func<ArtifactPartPresentationViewModel, ArtifactActionKind, Task> performAction;
    private XamlFragment? previewFragment;
    private string actionStatusText = string.Empty;

    internal ArtifactPartPresentationViewModel(
        ArtifactPart part,
        SafeMarkdownService markdown,
        Func<ArtifactPartPresentationViewModel, ArtifactActionKind, Task> performAction)
    {
        ArgumentNullException.ThrowIfNull(part);
        ArgumentNullException.ThrowIfNull(markdown);
        this.performAction = performAction ?? throw new ArgumentNullException(nameof(performAction));

        KindText = part.Kind switch
        {
            ArtifactPartKind.Text => "Text",
            ArtifactPartKind.Image => "Image",
            ArtifactPartKind.File => "File",
            _ => "Fallback content",
        };
        Text = markdown.ToSafeText(part.Text ?? string.Empty).Trim();
        DisplayName = markdown.ToSafeText(part.DisplayName ?? string.Empty).Trim();
        MimeType = markdown.ToSafeText(part.MimeType ?? string.Empty).Trim();
        IsServerTruncated = part.IsServerTruncated;
        IsLocallyTruncated = part.IsLocallyTruncated;
        ActionId = part.ActionId;
        AllowedActions = new ObservableCollection<ArtifactActionKind>(part.AllowedActions);

        PreviewCommand = new AsyncCommand(
            () => this.performAction(this, ArtifactActionKind.Preview),
            () => CanPreview);
        OpenCommand = new AsyncCommand(
            () => this.performAction(this, ArtifactActionKind.Open),
            () => CanOpen);
        RevealCommand = new AsyncCommand(
            () => this.performAction(this, ArtifactActionKind.Reveal),
            () => CanReveal);
    }

    [DataMember]
    public string KindText { get; }

    [DataMember]
    public string Text { get; }

    [DataMember]
    public string DisplayName { get; }

    [DataMember]
    public bool HasDisplayName => !string.IsNullOrWhiteSpace(DisplayName);

    [DataMember]
    public string MimeType { get; }

    [DataMember]
    public bool HasMimeType => !string.IsNullOrWhiteSpace(MimeType);

    [DataMember]
    public bool IsServerTruncated { get; }

    [DataMember]
    public bool IsLocallyTruncated { get; }

    [DataMember]
    public bool CanPreview => HasAction(ArtifactActionKind.Preview);

    [DataMember]
    public bool CanOpen => HasAction(ArtifactActionKind.Open);

    [DataMember]
    public bool CanReveal => HasAction(ArtifactActionKind.Reveal);

    [DataMember]
    public ObservableCollection<ArtifactActionKind> AllowedActions { get; }

    [DataMember]
    public XamlFragment? PreviewFragment
    {
        get => previewFragment;
        internal set
        {
            if (SetProperty(ref previewFragment, value))
            {
                OnPropertyChanged(nameof(HasPreview));
            }
        }
    }

    [DataMember]
    public bool HasPreview => PreviewFragment is not null;

    [DataMember]
    public string ActionStatusText
    {
        get => actionStatusText;
        internal set
        {
            if (SetProperty(ref actionStatusText, value))
            {
                OnPropertyChanged(nameof(HasActionStatus));
            }
        }
    }

    [DataMember]
    public bool HasActionStatus => !string.IsNullOrWhiteSpace(ActionStatusText);

    [DataMember]
    public AsyncCommand PreviewCommand { get; }

    [DataMember]
    public AsyncCommand OpenCommand { get; }

    [DataMember]
    public AsyncCommand RevealCommand { get; }

    internal string? ActionId { get; }

    public void RaiseCanExecuteChanged()
    {
        PreviewCommand.RaiseCanExecuteChanged();
        OpenCommand.RaiseCanExecuteChanged();
        RevealCommand.RaiseCanExecuteChanged();
    }

    private bool HasAction(ArtifactActionKind action)
        => !string.IsNullOrWhiteSpace(ActionId) && AllowedActions.Contains(action);
}
