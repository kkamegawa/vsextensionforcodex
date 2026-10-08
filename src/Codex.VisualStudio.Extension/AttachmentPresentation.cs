using System.IO;
using System.Runtime.Serialization;

namespace Codex.VisualStudio.Extension;

/// <summary>
/// Presents one pending file attachment to Remote UI without serializing its absolute path.
/// </summary>
[DataContract]
public sealed class AttachmentChipViewModel
{
    private readonly Func<AttachmentChipViewModel, Task> remove;
    private readonly Func<AttachmentChipViewModel, Task>? saveToThread;

    public AttachmentChipViewModel(
        string fullPath,
        SafeMarkdownService markdown,
        Func<AttachmentChipViewModel, Task> remove,
        Func<AttachmentChipViewModel, Task>? saveToThread = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fullPath);
        ArgumentNullException.ThrowIfNull(markdown);
        ArgumentNullException.ThrowIfNull(remove);

        FullPath = fullPath;
        this.remove = remove;
        this.saveToThread = saveToThread;

        string fileName = Path.GetFileName(fullPath);
        DisplayName = markdown.ToSafeText(
            string.IsNullOrWhiteSpace(fileName) ? fullPath : fileName).Trim();
        AutomationName = $"Remove attachment {DisplayName}";
        RemoveCommand = new AsyncCommand(RemoveAsync);
        SaveToThreadCommand = new AsyncCommand(SaveToThreadAsync, () => CanSaveToThread);
    }

    /// <summary>
    /// Gets the trusted path retained in the extension process for request construction.
    /// </summary>
    public string FullPath { get; }

    [DataMember]
    public string DisplayName { get; }

    [DataMember]
    public string AutomationName { get; }

    [DataMember]
    public AsyncCommand RemoveCommand { get; }

    [DataMember]
    public bool CanSaveToThread => saveToThread is not null
        && Path.GetExtension(FullPath).ToLowerInvariant() is ".txt" or ".md" or ".pdf" or ".png" or ".jpg" or ".jpeg";

    [DataMember]
    public AsyncCommand SaveToThreadCommand { get; }

    private Task RemoveAsync()
        => remove(this);

    private Task SaveToThreadAsync()
        => saveToThread?.Invoke(this) ?? Task.CompletedTask;
}
