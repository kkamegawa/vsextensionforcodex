using System.IO;
using System.Security;
using System.Security.Cryptography;
using System.Text;
using Codex.VisualStudio.Contracts;
using Microsoft.VisualStudio.Extensibility;
using Microsoft.VisualStudio.Extensibility.Documents;
using Microsoft.VisualStudio.RpcContracts.Documents;
using Microsoft.VisualStudio.RpcContracts.OpenDocument;

namespace Codex.VisualStudio.Extension;

/// <summary>
/// Keeps conversation snapshots under the current user's temporary folder, separated by owner.
/// File names are generated, so a conversation title never reaches the file system.
/// </summary>
internal sealed class ConversationTextStore
{
    internal static readonly TimeSpan MaximumUnusedAge = TimeSpan.FromHours(24);

    private readonly object gate = new();
    private readonly HashSet<string> openFiles = new(StringComparer.OrdinalIgnoreCase);
    private readonly string root;

    public ConversationTextStore(string? root = null)
    {
        this.root = root ?? Path.Combine(Path.GetTempPath(), "Codex.VisualStudio", "ConversationText");
    }

    public string Write(ArtifactPreviewOwner owner, string text)
    {
        string directory = Path.Combine(root, GetOwnerKey(owner));
        EnsureNotRedirected(directory);
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, string.Concat(Guid.NewGuid().ToString("N"), ".txt"));
        using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
        using (var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true)))
        {
            writer.Write(text);
        }

        lock (gate)
        {
            openFiles.Add(path);
        }

        return path;
    }

    /// <summary>Deletes a snapshot this instance wrote. Any other path is ignored.</summary>
    public void Remove(string path)
    {
        lock (gate)
        {
            if (!openFiles.Remove(path))
            {
                return;
            }
        }

        TryDelete(path);
    }

    public void RemoveUnused(DateTimeOffset now)
    {
        if (!Directory.Exists(root) || IsRedirected(root))
        {
            return;
        }

        try
        {
            foreach (string directory in Directory.EnumerateDirectories(root))
            {
                if (IsRedirected(root) || IsRedirected(directory))
                {
                    continue;
                }

                foreach (string file in Directory.EnumerateFiles(directory, "*.txt"))
                {
                    if (IsRedirected(root) || IsRedirected(directory))
                    {
                        break;
                    }

                    bool open;
                    lock (gate)
                    {
                        open = openFiles.Contains(file);
                    }

                    if (!open && now - new DateTimeOffset(File.GetLastWriteTimeUtc(file)) > MaximumUnusedAge)
                    {
                        TryDelete(file);
                    }
                }

                if (!IsRedirected(root) && !IsRedirected(directory) && !Directory.EnumerateFileSystemEntries(directory).Any())
                {
                    Directory.Delete(directory);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
        {
            // Cleanup is best effort, and the next start tries again.
            ExtensionDiagnostics.Write($"Conversation text cleanup stopped ({ex.GetType().Name}).");
        }
    }

    private static string GetOwnerKey(ArtifactPreviewOwner owner)
    {
        string material = FormattableString.Invariant($"{owner.StatePartitionFingerprint}\n{owner.OwnerGeneration}\n{owner.ConnectionGeneration}");
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(material))).ToLowerInvariant();
    }

    private void EnsureNotRedirected(string directory)
    {
        for (string? current = directory; current is not null && current.Length >= root.Length; current = Path.GetDirectoryName(current))
        {
            if (Directory.Exists(current) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
            {
                throw new IOException("The conversation text folder is redirected.");
            }
        }
    }

    private static bool IsRedirected(string directory)
    {
        try
        {
            return (File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
        {
            return true;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
        {
            ExtensionDiagnostics.Write($"A conversation text file was not removed ({ex.GetType().Name}).");
        }
    }
}

internal interface IConversationTextDocuments
{
    Task OpenAsync(string path, string caption, Action<string> closed, CancellationToken cancellationToken);
}

/// <summary>Opens snapshots in the read-only Visual Studio editor, which provides copy and search.</summary>
internal sealed class ConversationTextDocuments : IConversationTextDocuments, IDisposable
{
    private readonly VisualStudioExtensibility? extensibility;
    private readonly ClosedDocumentListener listener = new();
    private readonly object gate = new();
    private Task<IDisposable>? subscription;

    public ConversationTextDocuments(VisualStudioExtensibility? extensibility)
    {
        this.extensibility = extensibility;
    }

    public async Task OpenAsync(string path, string caption, Action<string> closed, CancellationToken cancellationToken)
    {
        if (extensibility is null)
        {
            throw new InvalidOperationException("Visual Studio document services are unavailable.");
        }

        var documents = extensibility.Documents();
        Task<IDisposable> subscribing;
        lock (gate)
        {
            subscribing = subscription ??= documents.SubscribeAsync(listener, "ConversationText", CancellationToken.None);
        }

        try
        {
            await subscribing.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            lock (gate)
            {
                subscription = null;
            }

            throw;
        }

        listener.Add(path, closed);
        await documents.OpenTextDocumentAsync(
            new Uri(path, UriKind.Absolute),
            new OpenDocumentOptions(
                isPreview: false,
                documentWindowCaptionKind: DocumentWindowCaptionKind.Custom,
                customDocumentWindowCaption: caption,
                isUserReadonly: true),
            cancellationToken).ConfigureAwait(false);
    }

    public void Dispose()
    {
        Task<IDisposable>? subscribing;
        lock (gate)
        {
            subscribing = subscription;
            subscription = null;
        }

        _ = subscribing?.ContinueWith(
            static completed => completed.Result.Dispose(),
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnRanToCompletion,
            TaskScheduler.Default);
        listener.Dispose();
    }

    private sealed class ClosedDocumentListener : IDocumentEventsListener
    {
        private readonly object gate = new();
        private readonly Dictionary<string, (string Path, Action<string> Closed)> callbacks = new(StringComparer.OrdinalIgnoreCase);

        public void Add(string path, Action<string> closed)
        {
            lock (gate)
            {
                callbacks[Path.GetFileName(path)] = (path, closed);
            }
        }

        // Keyed by the generated file name: Visual Studio may report a long path for a short TEMP path.
        public Task ClosedAsync(DocumentEventArgs e, CancellationToken token)
        {
            bool found;
            (string Path, Action<string> Closed) entry;
            lock (gate)
            {
                found = callbacks.Remove(Path.GetFileName(e.Moniker.LocalPath), out entry);
            }

            if (found)
            {
                entry.Closed(entry.Path);
            }

            return Task.CompletedTask;
        }

        public Task HiddenAsync(DocumentEventArgs e, CancellationToken token) => Task.CompletedTask;

        public Task OpenedAsync(DocumentEventArgs e, CancellationToken token) => Task.CompletedTask;

        public Task RenamedAsync(RenamedDocumentEventArgs e, CancellationToken token) => Task.CompletedTask;

        public Task SavedAsync(DocumentEventArgs e, CancellationToken token) => Task.CompletedTask;

        public Task SavingAsync(DocumentEventArgs e, CancellationToken token) => Task.CompletedTask;

        public Task ShownAsync(DocumentEventArgs e, CancellationToken token) => Task.CompletedTask;

        public void Dispose()
        {
        }
    }
}

/// <summary>Builds the snapshot text from sanitized history items, newest page first.</summary>
internal static class ConversationTextFormatter
{
    public static string Format(string title, DateTimeOffset snapshotAt, IEnumerable<ThreadHistoryItem> oldestFirst, SafeMarkdownService markdown)
    {
        var text = new StringBuilder();
        text.Append(title).Append("\r\n");
        text.Append(FormattableString.Invariant($"Snapshot taken {snapshotAt.ToLocalTime():yyyy-MM-dd HH:mm:ss}. It does not update.")).Append("\r\n");
        foreach (ThreadHistoryItem item in oldestFirst)
        {
            text.Append("\r\n--- ").Append(ChatViewModel.GetHistoryRole(item));
            string timing = ChatItemViewModel.FormatTiming(item.StartedAtMs, item.CompletedAtMs);
            if (timing.Length > 0)
            {
                text.Append(" · ").Append(timing);
            }

            text.Append(" ---\r\n");
            // History text has already passed through ISecretRedactor in the worker. Keep it
            // as plain text here: Markdown parsing changes literal command and code characters.
            // The literal path still removes ANSI escape sequences and control characters.
            text.Append(SafeMarkdownService.ToSafeLiteralText(item.Text ?? string.Empty)).Append("\r\n");

            foreach (ArtifactPart part in item.Parts)
            {
                if (part.Kind == ArtifactPartKind.Text)
                {
                    // MCP history can expose content only through ordered text parts. When the
                    // item already has that same body, avoid writing its projected copy twice.
                    if (!string.IsNullOrEmpty(part.Text)
                        && !string.Equals(item.Text, part.Text, StringComparison.Ordinal))
                    {
                        text.Append(SafeMarkdownService.ToSafeLiteralText(part.Text)).Append("\r\n");
                    }

                    continue;
                }

                string name = markdown.ToSafeText(part.DisplayName ?? string.Empty).Trim();
                text.Append(FormattableString.Invariant($"[{part.Kind}{(name.Length > 0 ? ": " : string.Empty)}{name}]")).Append("\r\n");
                if (!string.IsNullOrEmpty(part.Text))
                {
                    // Artifact text is also redacted by the worker. It is explanatory plain
                    // text (including fallback reasons), so do not run it through Markdown.
                    text.Append(SafeMarkdownService.ToSafeLiteralText(part.Text)).Append("\r\n");
                }
            }
        }

        return text.ToString().ReplaceLineEndings("\r\n");
    }
}
