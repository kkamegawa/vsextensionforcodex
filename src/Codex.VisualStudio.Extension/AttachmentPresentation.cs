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

    public AttachmentChipViewModel(
        string fullPath,
        SafeMarkdownService markdown,
        Func<AttachmentChipViewModel, Task> remove)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fullPath);
        ArgumentNullException.ThrowIfNull(markdown);
        ArgumentNullException.ThrowIfNull(remove);

        FullPath = fullPath;
        this.remove = remove;

        string fileName = Path.GetFileName(fullPath);
        DisplayName = markdown.ToSafeText(
            string.IsNullOrWhiteSpace(fileName) ? fullPath : fileName).Trim();
        AutomationName = $"Remove attachment {DisplayName}";
        RemoveCommand = new AsyncCommand(RemoveAsync);
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

    private Task RemoveAsync()
        => remove(this);
}
