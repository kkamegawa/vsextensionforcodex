using Codex.VisualStudio.Contracts;

namespace Codex.VisualStudio.Extension;

// Routing policy that decides whether a ConversationEvent carries user-facing Codex content
// (rendered in the chat panel transcript) or operational/protocol detail (written to the
// "Codex Diagnostics" Output channel). Keeping this a pure, public function makes the
// panel/Output split unit-testable from the UI test project, the same way
// ChatItemViewModel.ParsePlanSteps is exercised directly.
public static class ConversationEventPresentation
{
    // Returns true when the event kind represents user-facing Codex output that belongs in the
    // chat panel. Everything else (item/turn lifecycle, errors, unknown protocol notifications)
    // is diagnostic and must be routed to the Output channel instead. Note that raw codex
    // app-server output is treated as untrusted: the panel renders only ConversationEvent.Text,
    // never PayloadJson (PlanUpdated is the sole exception and parses PayloadJson into structured
    // steps).
    public static bool IsPanelContent(ConversationEventKind kind) => kind switch
    {
        ConversationEventKind.AgentMessageDelta => true,
        ConversationEventKind.ReasoningSummaryDelta => true,
        ConversationEventKind.CommandOutputDelta => true,
        ConversationEventKind.DiffUpdated => true,
        ConversationEventKind.PlanUpdated => true,
        _ => false,
    };

    // Builds a concise, single-line description of a non-user-facing event for the Output
    // channel. Secret redaction is applied downstream by ExtensionDiagnostics.WriteOutputAsync,
    // so this method only shapes the text.
    public static string FormatDiagnostic(ConversationEvent value)
    {
        string? detail = value.Text ?? value.PayloadJson;
        if (value.Kind == ConversationEventKind.Error)
        {
            return $"[codex-error] {Compact(detail)}";
        }

        string suffix = string.IsNullOrEmpty(detail) ? string.Empty : " " + Compact(detail);
        return $"[event] {value.Kind} thread={value.ThreadId ?? "-"} turn={value.TurnId ?? "-"} item={value.ItemId ?? "-"}{suffix}";
    }

    // Collapses newlines so each diagnostic occupies a single Output line.
    private static string Compact(string? text)
        => string.IsNullOrEmpty(text)
            ? string.Empty
            : text.Replace("\r", " ").Replace("\n", " ").Trim();
}
