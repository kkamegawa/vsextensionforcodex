using System.Runtime.Serialization;
using Codex.VisualStudio.Contracts;

namespace Codex.VisualStudio.Extension;

[DataContract]
public sealed class PendingSkillViewModel : ObservableObject
{
    private readonly Func<Task> remove;
    private readonly Func<string, Task> useDefaultPrompt;

    internal PendingSkillViewModel(
        string displayName,
        string scopeLabel,
        string description,
        SkillInvocationInfo invocation,
        Func<Task> remove,
        string? defaultPrompt,
        Func<string, Task> useDefaultPrompt,
        SafeMarkdownService markdown)
    {
        this.remove = remove;
        this.useDefaultPrompt = useDefaultPrompt;
        DisplayName = markdown.ToSafeText(displayName).Trim();
        ScopeLabel = markdown.ToSafeText(scopeLabel).Trim();
        Description = markdown.ToSafeText(description).Trim();
        Invocation = invocation;
        RemoveCommand = new AsyncCommand(RemoveAsync);
        string safePrompt = markdown.ToSafeText(defaultPrompt ?? string.Empty).Trim();
        DefaultPrompt = safePrompt;
        DefaultPromptPreview = safePrompt.Length > 160 ? string.Concat(safePrompt.AsSpan(0, 160), "…") : safePrompt;
        UseDefaultPromptCommand = new AsyncCommand(UseDefaultPromptAsync, () => HasDefaultPrompt);
    }

    [DataMember]
    public string DisplayName { get; }

    [DataMember]
    public string ScopeLabel { get; }

    [DataMember]
    public string Description { get; }

    [DataMember]
    public AsyncCommand RemoveCommand { get; }

    [DataMember]
    public AsyncCommand UseDefaultPromptCommand { get; }

    [DataMember]
    public string DefaultPromptPreview { get; }

    [DataMember]
    public bool HasDefaultPrompt => DefaultPrompt.Length > 0;

    internal SkillInvocationInfo Invocation { get; }

    internal string DefaultPrompt { get; }

    private Task RemoveAsync() => remove();

    private Task UseDefaultPromptAsync() => useDefaultPrompt(DefaultPrompt);
}
