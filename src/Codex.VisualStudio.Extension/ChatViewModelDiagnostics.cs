using System.Globalization;
using System.IO;
using Codex.VisualStudio.Contracts;
using Microsoft.VisualStudio.Extensibility;
using Microsoft.VisualStudio.Extensibility.Shell;

namespace Codex.VisualStudio.Extension;

// "Save diagnostic ZIP" through feedback/save (0.164 tier). The server writes the file on its own
// host and never overwrites one, so the save is sent once and never resent automatically.
public sealed partial class ChatViewModel
{
    internal enum DiagnosticsSaveChoice
    {
        Cancel,
        WithoutLogs,
        WithLogs,
    }

    private const string DiagnosticsTierRequiredText =
        "Saving a diagnostic ZIP requires Codex CLI 0.164.0 or later.";

    // Tests replace the Visual Studio prompt; production uses the shell prompt.
    internal Func<string, Task<DiagnosticsSaveChoice>>? DiagnosticsConfirmationOverride { get; set; }

    private async Task<bool> ExecuteDiagnosticsAsync(string arguments, string? threadId)
    {
        OwnerSnapshot owner = CaptureOwnerSnapshot();
        if (Status.FeatureTier < CodexFeatureTier.Tier164)
        {
            await ShowSlashFailureAsync(DiagnosticsTierRequiredText).ConfigureAwait(false);
            return false;
        }

        bool remote = IsRemoteTarget;
        string? savePath;
        if (remote)
        {
            savePath = arguments.Trim();
            if (savePath.Length == 0 || !IsAbsoluteServerPath(savePath))
            {
                await ShowSlashFailureAsync(
                    "Enter an absolute path on the app-server host for the new ZIP: /diagnostics <server path>.").ConfigureAwait(false);
                return false;
            }
        }
        else
        {
            string suggested = string.Create(
                CultureInfo.InvariantCulture,
                $"codex-diagnostics-{DateTime.Now:yyyyMMdd-HHmmss}.zip");
            savePath = await filePickerService.PickSaveFileAsync(suggested, lifetime.Token).ConfigureAwait(false);
            if (savePath is null)
            {
                await ShowSlashStatusAsync("Saving the diagnostic ZIP was canceled.").ConfigureAwait(false);
                return false;
            }

            // The server never overwrites a file, so an existing path is rejected before the request.
            if (File.Exists(savePath) || Directory.Exists(savePath))
            {
                await ShowSlashFailureAsync(
                    $"{savePath} already exists. Choose a new file name; the diagnostic ZIP never replaces a file.").ConfigureAwait(false);
                return false;
            }
        }

        string host = remote
            ? $"the remote app-server host ({Status.Target?.DisplayName ?? "remote"})"
            : "this computer";
        DiagnosticsSaveChoice choice = await ConfirmDiagnosticsSaveAsync(
            $"Save a diagnostic ZIP on {host}?\r\n\r\nPath: {savePath}\r\n\r\n"
            + "Without logs, the ZIP contains the feedback report and diagnostics text only. "
            + "With logs, it also contains the conversation rollout and Codex log files.").ConfigureAwait(false);
        if (choice == DiagnosticsSaveChoice.Cancel)
        {
            await ShowSlashStatusAsync("Saving the diagnostic ZIP was canceled.").ConfigureAwait(false);
            return false;
        }

        if (!IsCurrentOwner(owner))
        {
            return false;
        }

        SaveFeedbackResult result = await bridge.SaveFeedbackAsync(
            StampOwner(new SaveFeedbackRequest
            {
                Classification = "visual-studio",
                IncludeLogs = choice == DiagnosticsSaveChoice.WithLogs,
                ThreadId = threadId,
                SavePath = savePath,
            }, owner),
            lifetime.Token).ConfigureAwait(false);
        if (!IsCurrentOwner(owner))
        {
            return false;
        }

        if (!result.IsSupported)
        {
            await ShowSlashFailureAsync(result.UnavailableReason ?? DiagnosticsTierRequiredText).ConfigureAwait(false);
            return false;
        }

        switch (result.Outcome)
        {
            case SaveFeedbackOutcome.Saved:
                string saved = result.SavedPath ?? savePath;
                // A remote result is a server path and is never opened as a local file.
                await ShowSlashStatusAsync(remote
                    ? $"The diagnostic ZIP was saved on the app-server host. Server path: {saved}"
                    : $"The diagnostic ZIP was saved: {saved}").ConfigureAwait(false);
                return true;
            case SaveFeedbackOutcome.Unconfirmed:
                await ShowSlashFailureAsync(result.Message
                    ?? "The connection ended before the save was confirmed. Check the path before saving again.").ConfigureAwait(false);
                return false;
            default:
                await ShowSlashFailureAsync(result.Message ?? "The app-server did not save the diagnostic ZIP.").ConfigureAwait(false);
                return false;
        }
    }

    private async Task<DiagnosticsSaveChoice> ConfirmDiagnosticsSaveAsync(string message)
    {
        if (DiagnosticsConfirmationOverride is { } confirm)
        {
            return await confirm(message).ConfigureAwait(false);
        }

        if (extensibility is null)
        {
            return DiagnosticsSaveChoice.Cancel;
        }

        return await extensibility.Shell().ShowPromptAsync(
            message,
            new PromptOptions<DiagnosticsSaveChoice>
            {
                Choices =
                {
                    { "Save without logs", DiagnosticsSaveChoice.WithoutLogs },
                    { "Save with logs", DiagnosticsSaveChoice.WithLogs },
                    { "Cancel", DiagnosticsSaveChoice.Cancel },
                },
                DefaultChoiceIndex = 0,
                DismissedReturns = DiagnosticsSaveChoice.Cancel,
                Title = "Save Diagnostic ZIP",
            },
            lifetime.Token).ConfigureAwait(false);
    }

    // A POSIX absolute path or a Windows drive or UNC path; the Worker validates it again.
    private static bool IsAbsoluteServerPath(string path)
        => path.StartsWith('/')
            || path.StartsWith(@"\\", StringComparison.Ordinal)
            || (path.Length >= 3 && char.IsAsciiLetter(path[0]) && path[1] == ':' && path[2] is '\\' or '/');
}
