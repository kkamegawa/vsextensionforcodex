using System.Text.Json;
using Codex.VisualStudio.Contracts;

namespace Codex.VisualStudio.Worker;

internal sealed class OfferedCommandChoiceSet
{
    private const int MaximumChoices = 64;
    private const int MaximumAmendmentEntries = 256;
    private readonly Dictionary<string, JsonElement> decisions;

    private OfferedCommandChoiceSet(List<ApprovalChoice> choices, Dictionary<string, JsonElement> decisions)
    {
        Choices = choices;
        this.decisions = decisions;
    }

    public IReadOnlyList<ApprovalChoice> Choices { get; }

    public bool TryResolve(string? choiceId, out JsonElement response)
    {
        if (string.IsNullOrWhiteSpace(choiceId) || !decisions.TryGetValue(choiceId, out JsonElement decision))
        {
            response = default;
            return false;
        }

        response = decision.Clone();
        return true;
    }

    public bool TryResolveDecision(string wireDecision, out JsonElement response)
    {
        foreach (JsonElement offeredResponse in decisions.Values)
        {
            JsonElement decision = offeredResponse.GetProperty("decision");
            if (decision.ValueKind == JsonValueKind.String
                && string.Equals(decision.GetString(), wireDecision, StringComparison.Ordinal))
            {
                response = offeredResponse.Clone();
                return true;
            }
        }

        response = default;
        return false;
    }

    public static bool TryCreate(
        JsonElement parameters,
        Func<string, string> redact,
        Func<string> newOpaqueId,
        out OfferedCommandChoiceSet? choiceSet)
    {
        choiceSet = null;
        if (parameters.ValueKind != JsonValueKind.Object
            || redact is null
            || newOpaqueId is null
            || EncodingSize(parameters) > McpElicitationFormParser.MaximumPayloadBytes)
        {
            return false;
        }

        string permissionSummary = BuildPermissionSummary(parameters, redact);
        var choices = new List<ApprovalChoice>();
        var decisions = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        if (parameters.TryGetProperty("availableDecisions", out JsonElement availableDecisions)
            && availableDecisions.ValueKind is not JsonValueKind.Null)
        {
            if (availableDecisions.ValueKind != JsonValueKind.Array
                || availableDecisions.GetArrayLength() is < 1 or > MaximumChoices)
            {
                return false;
            }

            foreach (JsonElement offered in availableDecisions.EnumerateArray())
            {
                if (!TryDescribeDecision(offered, redact, out string label, out string description)
                    || !TryAdd(label, AddPermissionContext(description), offered))
                {
                    return false;
                }
            }
        }
        else
        {
            if (!TryAdd("Approve this command", AddPermissionContext("Run the command once."), JsonSerializer.SerializeToElement("accept"))
                || !TryAdd("Approve for this session", AddPermissionContext("Run this command and allow session-scoped approval reuse."), JsonSerializer.SerializeToElement("acceptForSession"))
                || !TryAdd("Decline", "Decline the command and let the turn continue.", JsonSerializer.SerializeToElement("decline"))
                || !TryAdd("Cancel turn", "Decline the command and interrupt the turn.", JsonSerializer.SerializeToElement("cancel")))
            {
                return false;
            }

            if (parameters.TryGetProperty("proposedExecpolicyAmendment", out JsonElement execAmendment)
                && execAmendment.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined))
            {
                if (execAmendment.ValueKind != JsonValueKind.Array
                    || execAmendment.GetArrayLength() > MaximumAmendmentEntries
                    || execAmendment.EnumerateArray().Any(item => item.ValueKind != JsonValueKind.String))
                {
                    return false;
                }

                if (execAmendment.GetArrayLength() > 0)
                {
                    string rules = string.Join("; ", execAmendment.EnumerateArray()
                        .Select(item => Bound(redact(item.GetString() ?? string.Empty))));
                    JsonElement decision = JsonSerializer.SerializeToElement(new
                    {
                        acceptWithExecpolicyAmendment = new { execpolicy_amendment = execAmendment },
                    });
                    if (!TryAdd(
                        "Approve and add execution rule",
                        AddPermissionContext($"Add these matching-command rules for future approvals: {rules}"),
                        decision))
                    {
                        return false;
                    }
                }
            }

            if (parameters.TryGetProperty("proposedNetworkPolicyAmendments", out JsonElement networkAmendments)
                && networkAmendments.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined))
            {
                if (networkAmendments.ValueKind != JsonValueKind.Array
                    || networkAmendments.GetArrayLength() > MaximumAmendmentEntries
                    || networkAmendments.GetArrayLength() > MaximumChoices - choices.Count)
                {
                    return false;
                }

                foreach (JsonElement amendment in networkAmendments.EnumerateArray())
                {
                    if (!TryReadNetworkAmendment(amendment, out string action, out string host))
                    {
                        return false;
                    }

                    JsonElement decision = JsonSerializer.SerializeToElement(new
                    {
                        applyNetworkPolicyAmendment = new { network_policy_amendment = amendment },
                    });
                    if (!TryAdd(
                        $"Approve {action} network rule for {Bound(redact(host))}",
                        AddPermissionContext($"Add a persistent {action} rule for network host {Bound(redact(host))}."),
                        decision))
                    {
                        return false;
                    }
                }
            }
        }

        choiceSet = new OfferedCommandChoiceSet(choices, decisions);
        return true;

        string AddPermissionContext(string description)
            => string.IsNullOrEmpty(permissionSummary) ? description : $"{description} Requested additional permissions: {permissionSummary}";

        bool TryAdd(string label, string description, JsonElement decision)
        {
            if (choices.Count >= MaximumChoices
                || EncodingSize(decision) > McpElicitationFormParser.MaximumPayloadBytes)
            {
                return false;
            }

            string choiceId = newOpaqueId();
            if (string.IsNullOrWhiteSpace(choiceId)
                || !decisions.TryAdd(choiceId, JsonSerializer.SerializeToElement(new { decision }).Clone()))
            {
                return false;
            }

            choices.Add(new ApprovalChoice
            {
                ChoiceId = choiceId,
                Label = Bound(redact(label) ?? string.Empty),
                Description = Bound(redact(description) ?? string.Empty),
            });
            return true;
        }
    }

    private static bool TryDescribeDecision(JsonElement decision, Func<string, string> redact, out string label, out string description)
    {
        label = string.Empty;
        description = string.Empty;
        if (decision.ValueKind == JsonValueKind.String)
        {
            switch (decision.GetString())
            {
                case "accept":
                    label = "Approve this command";
                    description = "Run the command once.";
                    return true;
                case "acceptForSession":
                    label = "Approve for this session";
                    description = "Run this command and allow session-scoped approval reuse.";
                    return true;
                case "decline":
                    label = "Decline";
                    description = "Decline the command and let the turn continue.";
                    return true;
                case "cancel":
                    label = "Cancel turn";
                    description = "Decline the command and interrupt the turn.";
                    return true;
                default:
                    return false;
            }
        }

        if (decision.ValueKind != JsonValueKind.Object || decision.EnumerateObject().Count() != 1)
        {
            return false;
        }

        JsonProperty property = decision.EnumerateObject().Single();
        if (property.NameEquals("acceptWithExecpolicyAmendment")
            && property.Value.ValueKind == JsonValueKind.Object
            && property.Value.TryGetProperty("execpolicy_amendment", out JsonElement rules)
            && rules.ValueKind == JsonValueKind.Array
            && rules.GetArrayLength() is > 0 and <= MaximumAmendmentEntries
            && rules.EnumerateArray().All(rule => rule.ValueKind == JsonValueKind.String))
        {
            label = "Approve and add execution rule";
            description = "Add these matching-command rules for future approvals: "
                + string.Join("; ", rules.EnumerateArray().Select(rule => Bound(redact(rule.GetString() ?? string.Empty))));
            return true;
        }

        if (property.NameEquals("applyNetworkPolicyAmendment")
            && property.Value.ValueKind == JsonValueKind.Object
            && property.Value.TryGetProperty("network_policy_amendment", out JsonElement amendment)
            && TryReadNetworkAmendment(amendment, out string action, out string host))
        {
            string safeHost = Bound(redact(host));
            label = $"Approve {action} network rule for {safeHost}";
            description = $"Add a persistent {action} rule for network host {safeHost}.";
            return true;
        }

        return false;
    }

    private static string BuildPermissionSummary(JsonElement parameters, Func<string, string> redact)
    {
        if (!parameters.TryGetProperty("additionalPermissions", out JsonElement profile)
            || profile.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined
            || profile.ValueKind != JsonValueKind.Object)
        {
            return string.Empty;
        }

        var summaries = new List<string>();
        if (profile.TryGetProperty("network", out JsonElement network)
            && network.ValueKind == JsonValueKind.Object
            && network.TryGetProperty("enabled", out JsonElement enabled)
            && enabled.ValueKind == JsonValueKind.True)
        {
            summaries.Add("network access");
        }

        if (profile.TryGetProperty("fileSystem", out JsonElement fileSystem)
            && fileSystem.ValueKind == JsonValueKind.Object)
        {
            if (fileSystem.TryGetProperty("entries", out JsonElement entries) && entries.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement entry in entries.EnumerateArray())
                {
                    if (entry.ValueKind != JsonValueKind.Object
                        || !entry.TryGetProperty("access", out JsonElement access)
                        || access.ValueKind != JsonValueKind.String
                        || !entry.TryGetProperty("path", out JsonElement path))
                    {
                        continue;
                    }

                    summaries.Add($"{access.GetString()} {Bound(redact(DescribePath(path)))}");
                }
            }

            AddLegacy("read", "read");
            AddLegacy("write", "write");
            if (fileSystem.TryGetProperty("globScanMaxDepth", out JsonElement depth) && depth.TryGetUInt32(out uint value))
            {
                summaries.Add($"glob scan depth {value}");
            }

            void AddLegacy(string propertyName, string accessName)
            {
                if (fileSystem.TryGetProperty(propertyName, out JsonElement paths) && paths.ValueKind == JsonValueKind.Array)
                {
                    foreach (JsonElement item in paths.EnumerateArray())
                    {
                        if (item.ValueKind == JsonValueKind.String)
                        {
                            summaries.Add($"{accessName} {Bound(redact(item.GetString() ?? string.Empty))}");
                        }
                    }
                }
            }
        }

        return summaries.Count == 0 ? string.Empty : string.Join(", ", summaries);
    }

    private static string DescribePath(JsonElement path)
    {
        if (path.ValueKind != JsonValueKind.Object || !path.TryGetProperty("type", out JsonElement type))
        {
            return "requested file path";
        }

        return type.GetString() switch
        {
            "path" when path.TryGetProperty("path", out JsonElement item) && item.ValueKind == JsonValueKind.String => item.GetString() ?? "path",
            "glob_pattern" when path.TryGetProperty("pattern", out JsonElement pattern) && pattern.ValueKind == JsonValueKind.String => pattern.GetString() ?? "pattern",
            "special" when path.TryGetProperty("value", out JsonElement value) => value.TryGetProperty("kind", out JsonElement kind) && kind.ValueKind == JsonValueKind.String ? kind.GetString() ?? "special path" : "special path",
            _ => "requested file path",
        };
    }

    private static bool TryReadNetworkAmendment(JsonElement amendment, out string action, out string host)
    {
        action = string.Empty;
        host = string.Empty;
        if (amendment.ValueKind != JsonValueKind.Object
            || amendment.EnumerateObject().Count() != 2
            || !amendment.TryGetProperty("action", out JsonElement actionElement)
            || actionElement.ValueKind != JsonValueKind.String
            || !amendment.TryGetProperty("host", out JsonElement hostElement)
            || hostElement.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        action = actionElement.GetString() ?? string.Empty;
        host = hostElement.GetString() ?? string.Empty;
        return (action is "allow" or "deny") && host.Length is > 0 and <= 2048;
    }

    private static int EncodingSize(JsonElement value)
        => System.Text.Encoding.UTF8.GetByteCount(value.GetRawText());

    private static string Bound(string? value)
    {
        value ??= string.Empty;
        return value.Length <= 4096 ? value : value[..4096];
    }
}
