using System.Text.Json;
using Codex.VisualStudio.Contracts;

namespace Codex.VisualStudio.Worker;

internal sealed class PermissionSelectionSet
{
    private readonly JsonElement requestedPermissions;
    private readonly IReadOnlyDictionary<string, PermissionGrant> grants;
    private readonly IReadOnlyList<JsonElement> denyEntries;

    internal PermissionSelectionSet(
        JsonElement requestedPermissions,
        IReadOnlyList<PermissionGrantOption> options,
        IReadOnlyDictionary<string, PermissionGrant> grants,
        IReadOnlyList<JsonElement> denyEntries)
    {
        this.requestedPermissions = requestedPermissions.Clone();
        RequestedPermissions = options;
        this.grants = grants;
        this.denyEntries = denyEntries;
    }

    public IReadOnlyList<PermissionGrantOption> RequestedPermissions { get; }

    public bool TryBuild(
        IReadOnlyList<string> selectedPermissionIds,
        PermissionScope scope,
        out JsonElement response,
        out string safeError)
    {
        response = default;
        safeError = string.Empty;
        if (selectedPermissionIds.Count > grants.Count
            || scope is not (PermissionScope.Turn or PermissionScope.Session))
        {
            safeError = "The permission selection is invalid.";
            return false;
        }

        bool includeNetwork = false;
        var entries = new List<JsonElement>();
        var legacyRead = new List<string>();
        var legacyWrite = new List<string>();
        var seenIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (string permissionId in selectedPermissionIds)
        {
            if (string.IsNullOrWhiteSpace(permissionId)
                || !seenIds.Add(permissionId)
                || !grants.TryGetValue(permissionId, out PermissionGrant? grant))
            {
                safeError = "The permission selection contains an unknown or duplicate option.";
                return false;
            }

            switch (grant.Kind)
            {
                case PermissionGrantKind.Network:
                    includeNetwork = true;
                    break;
                case PermissionGrantKind.Entry:
                    entries.Add(grant.Value.Clone());
                    break;
                case PermissionGrantKind.LegacyRead:
                    legacyRead.Add(grant.Value.GetString() ?? string.Empty);
                    break;
                case PermissionGrantKind.LegacyWrite:
                    legacyWrite.Add(grant.Value.GetString() ?? string.Empty);
                    break;
                default:
                    safeError = "The permission selection is invalid.";
                    return false;
            }
        }

        var profile = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (includeNetwork)
        {
            profile.Add("network", new { enabled = true });
        }

        if (entries.Count > 0 || legacyRead.Count > 0 || legacyWrite.Count > 0)
        {
            JsonElement requestedFileSystem = requestedPermissions.GetProperty("fileSystem");
            var fileSystem = new Dictionary<string, object?>(StringComparer.Ordinal);
            var allEntries = entries.Concat(denyEntries.Select(entry => entry.Clone())).ToArray();
            if (allEntries.Length > 0)
            {
                fileSystem.Add("entries", allEntries);
            }

            if (legacyRead.Count > 0)
            {
                fileSystem.Add("read", legacyRead);
            }

            if (legacyWrite.Count > 0)
            {
                fileSystem.Add("write", legacyWrite);
            }

            if (requestedFileSystem.TryGetProperty("globScanMaxDepth", out JsonElement scanDepth)
                && scanDepth.ValueKind == JsonValueKind.Number)
            {
                fileSystem.Add("globScanMaxDepth", scanDepth.GetUInt32());
            }

            profile.Add("fileSystem", fileSystem);
        }

        JsonElement granted = JsonSerializer.SerializeToElement(profile);
        if (!PermissionSubsetValidator.IsSubset(requestedPermissions, granted))
        {
            safeError = "The selected permissions exceed the request.";
            return false;
        }

        response = JsonSerializer.SerializeToElement(new
        {
            permissions = granted,
            scope = scope == PermissionScope.Session ? "session" : "turn",
        });
        if (System.Text.Encoding.UTF8.GetByteCount(response.GetRawText()) > McpElicitationFormParser.MaximumPayloadBytes)
        {
            response = default;
            safeError = "The permission selection exceeds the supported size limit.";
            return false;
        }

        return true;
    }
}

internal static class PermissionSelectionBuilder
{
    private const int MaximumOptions = 256;

    public static bool TryCreate(
        JsonElement requestedPermissions,
        Func<string, string> redact,
        Func<string> newOpaqueId,
        out PermissionSelectionSet? selectionSet)
    {
        selectionSet = null;
        if (requestedPermissions.ValueKind != JsonValueKind.Object
            || redact is null
            || newOpaqueId is null
            || !OnlyProperties(requestedPermissions, "fileSystem", "network"))
        {
            return false;
        }

        var options = new List<PermissionGrantOption>();
        var grants = new Dictionary<string, PermissionGrant>(StringComparer.Ordinal);
        var denyEntries = new List<JsonElement>();
        if (requestedPermissions.TryGetProperty("network", out JsonElement network)
            && network.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined))
        {
            if (network.ValueKind != JsonValueKind.Object
                || !OnlyProperties(network, "enabled")
                || !network.TryGetProperty("enabled", out JsonElement enabled)
                || enabled.ValueKind is not (JsonValueKind.True or JsonValueKind.False or JsonValueKind.Null))
            {
                return false;
            }

            if (enabled.ValueKind == JsonValueKind.True
                && !AddGrant(PermissionGrantKind.Network, default, "Enable network access", "Grant the requested network access."))
            {
                return false;
            }
        }

        if (requestedPermissions.TryGetProperty("fileSystem", out JsonElement fileSystem)
            && fileSystem.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined))
        {
            if (fileSystem.ValueKind != JsonValueKind.Object
                || !OnlyProperties(fileSystem, "entries", "read", "write", "globScanMaxDepth"))
            {
                return false;
            }

            if (fileSystem.TryGetProperty("globScanMaxDepth", out JsonElement scanDepth)
                && scanDepth.ValueKind is not (JsonValueKind.Null or JsonValueKind.Number))
            {
                return false;
            }

            if (scanDepth.ValueKind == JsonValueKind.Number
                && (!scanDepth.TryGetUInt32(out uint depth) || depth == 0))
            {
                return false;
            }

            if (fileSystem.TryGetProperty("entries", out JsonElement entries)
                && entries.ValueKind is not (JsonValueKind.Null or JsonValueKind.Array))
            {
                return false;
            }

            if (entries.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement entry in entries.EnumerateArray())
                {
                    if (!TryReadEntry(entry, out string access, out string display))
                    {
                        return false;
                    }

                    if (access == "deny")
                    {
                        denyEntries.Add(entry.Clone());
                    }
                    else if (!AddGrant(
                        PermissionGrantKind.Entry,
                        entry,
                        $"{access} access: {Bound(redact(display))}",
                        $"Grant only the requested {access} access for this entry."))
                    {
                        return false;
                    }
                }
            }

            if (!AddLegacy("read", PermissionGrantKind.LegacyRead)
                || !AddLegacy("write", PermissionGrantKind.LegacyWrite))
            {
                return false;
            }

        }

        selectionSet = new PermissionSelectionSet(requestedPermissions, options, grants, denyEntries);
        return true;

        bool AddLegacy(string name, PermissionGrantKind kind)
        {
            if (!fileSystem.TryGetProperty(name, out JsonElement legacy)
                || legacy.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            {
                return true;
            }

            if (legacy.ValueKind != JsonValueKind.Array)
            {
                return false;
            }

            foreach (JsonElement item in legacy.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.String)
                {
                    return false;
                }

                string path = item.GetString() ?? string.Empty;
                if (!AddGrant(kind, item, $"{name} access: {Bound(redact(path))}", $"Grant only the requested {name} access."))
                {
                    return false;
                }
            }

            return true;
        }

        bool AddGrant(PermissionGrantKind kind, JsonElement value, string label, string description)
        {
            if (options.Count >= MaximumOptions)
            {
                return false;
            }

            string id = newOpaqueId();
            if (string.IsNullOrWhiteSpace(id) || grants.ContainsKey(id))
            {
                return false;
            }

            options.Add(new PermissionGrantOption
            {
                PermissionId = id,
                Label = Bound(redact(label)),
                Description = Bound(redact(description)),
            });
            grants.Add(id, new PermissionGrant(kind, value.ValueKind is JsonValueKind.Undefined ? default : value.Clone()));
            return true;
        }
    }

    private static bool TryReadEntry(JsonElement entry, out string access, out string display)
    {
        access = string.Empty;
        display = string.Empty;
        if (entry.ValueKind != JsonValueKind.Object
            || !entry.TryGetProperty("access", out JsonElement accessElement)
            || accessElement.ValueKind != JsonValueKind.String
            || !entry.TryGetProperty("path", out JsonElement path))
        {
            return false;
        }

        access = accessElement.GetString() ?? string.Empty;
        if (access is not ("read" or "write" or "deny") || !TryReadFileSystemPath(path, out display))
        {
            return false;
        }

        return entry.EnumerateObject().Count() == 2;
    }

    internal static bool IsValidEntry(JsonElement entry)
        => TryReadEntry(entry, out _, out _);

    private static bool TryReadFileSystemPath(JsonElement path, out string display)
    {
        display = string.Empty;
        if (path.ValueKind != JsonValueKind.Object
            || !path.TryGetProperty("type", out JsonElement typeElement)
            || typeElement.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        string type = typeElement.GetString() ?? string.Empty;
        if (type == "path"
            && path.EnumerateObject().Count() == 2
            && path.TryGetProperty("path", out JsonElement pathValue)
            && pathValue.ValueKind == JsonValueKind.String)
        {
            display = pathValue.GetString() ?? string.Empty;
            return true;
        }

        if (type == "glob_pattern"
            && path.EnumerateObject().Count() == 2
            && path.TryGetProperty("pattern", out JsonElement pattern)
            && pattern.ValueKind == JsonValueKind.String)
        {
            display = pattern.GetString() ?? string.Empty;
            return true;
        }

        if (type != "special"
            || path.EnumerateObject().Count() != 2
            || !path.TryGetProperty("value", out JsonElement special)
            || special.ValueKind != JsonValueKind.Object
            || !special.TryGetProperty("kind", out JsonElement kindElement)
            || kindElement.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        string kind = kindElement.GetString() ?? string.Empty;
        if (kind is "root" or "minimal" or "tmpdir" or "slash_tmp")
        {
            if (special.EnumerateObject().Count() != 1)
            {
                return false;
            }

            display = kind;
            return true;
        }

        if (kind == "project_roots")
        {
            if (!OnlyProperties(special, "kind", "subpath"))
            {
                return false;
            }

            if (special.TryGetProperty("subpath", out JsonElement subpathValue)
                && subpathValue.ValueKind is not (JsonValueKind.Null or JsonValueKind.String))
            {
                return false;
            }

            display = special.TryGetProperty("subpath", out JsonElement subpath)
                && subpath.ValueKind == JsonValueKind.String
                ? $"project roots / {subpath.GetString()}"
                : "project roots";
            return true;
        }

        if (kind == "unknown"
            && special.TryGetProperty("path", out JsonElement unknownPath)
            && unknownPath.ValueKind == JsonValueKind.String
            && OnlyProperties(special, "kind", "path", "subpath"))
        {
            if (special.TryGetProperty("subpath", out JsonElement unknownSubpath)
                && unknownSubpath.ValueKind is not (JsonValueKind.Null or JsonValueKind.String))
            {
                return false;
            }

            display = unknownPath.GetString() ?? string.Empty;
            return true;
        }

        return false;
    }

    private static bool OnlyProperties(JsonElement element, params string[] allowed)
    {
        var set = new HashSet<string>(allowed, StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (JsonProperty property in element.EnumerateObject())
        {
            if (!set.Contains(property.Name) || !seen.Add(property.Name))
            {
                return false;
            }
        }

        return true;
    }

    private static string Bound(string? value)
    {
        value ??= string.Empty;
        return value.Length <= 4096 ? value : value[..4096];
    }
}

internal static class PermissionSubsetValidator
{
    public static bool IsSubset(JsonElement requestedPermissions, JsonElement selectedPermissions)
    {
        if (!TryReadProfile(requestedPermissions, out PermissionProfile requested)
            || !TryReadProfile(selectedPermissions, out PermissionProfile selected))
        {
            return false;
        }

        if (selected.NetworkEnabled && !requested.NetworkEnabled)
        {
            return false;
        }

        if (!IsSubset(selected.Entries, requested.Entries)
            || !IsSubset(selected.LegacyRead, requested.LegacyRead)
            || !IsSubset(selected.LegacyWrite, requested.LegacyWrite))
        {
            return false;
        }

        bool selectedHasFileGrant = selected.Entries.Any(entry => GetString(entry, "access") is "read" or "write")
            || selected.LegacyRead.Count > 0
            || selected.LegacyWrite.Count > 0;
        if (selected.HasFileSystem
            && (!requested.HasFileSystem
                || selected.ScanDepth != requested.ScanDepth
                || (selectedHasFileGrant && requested.Entries.Where(entry => GetString(entry, "access") == "deny")
                    .Any(deny => !selected.Entries.Any(candidate => JsonEqual(deny, candidate))))))
        {
            return false;
        }

        return true;
    }

    private static bool TryReadProfile(JsonElement profile, out PermissionProfile result)
    {
        result = new PermissionProfile();
        if (profile.ValueKind != JsonValueKind.Object || !OnlyProperties(profile, "fileSystem", "network"))
        {
            return false;
        }

        if (profile.TryGetProperty("network", out JsonElement network)
            && network.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined))
        {
            if (network.ValueKind != JsonValueKind.Object
                || !OnlyProperties(network, "enabled")
                || !network.TryGetProperty("enabled", out JsonElement enabled)
                || enabled.ValueKind is not (JsonValueKind.True or JsonValueKind.False or JsonValueKind.Null))
            {
                return false;
            }

            result.NetworkEnabled = enabled.ValueKind == JsonValueKind.True;
        }

        if (profile.TryGetProperty("fileSystem", out JsonElement fileSystem)
            && fileSystem.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined))
        {
            result.HasFileSystem = true;
            if (fileSystem.ValueKind != JsonValueKind.Object
                || !OnlyProperties(fileSystem, "entries", "read", "write", "globScanMaxDepth"))
            {
                return false;
            }

            if (fileSystem.TryGetProperty("globScanMaxDepth", out JsonElement scanDepth))
            {
                if (scanDepth.ValueKind is not (JsonValueKind.Null or JsonValueKind.Number)
                    || (scanDepth.ValueKind == JsonValueKind.Number && (!scanDepth.TryGetUInt32(out uint depth) || depth == 0)))
                {
                    return false;
                }

                if (scanDepth.ValueKind == JsonValueKind.Number)
                {
                    result.ScanDepth = scanDepth.GetUInt32();
                }
            }

            if (fileSystem.TryGetProperty("entries", out JsonElement entries)
                && entries.ValueKind is not (JsonValueKind.Null or JsonValueKind.Array))
            {
                return false;
            }

            if (entries.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement entry in entries.EnumerateArray())
                {
                    if (!PermissionSelectionBuilder.IsValidEntry(entry))
                    {
                        return false;
                    }

                    result.Entries.Add(entry.Clone());
                }
            }

            if (!ReadPaths(fileSystem, "read", result.LegacyRead)
                || !ReadPaths(fileSystem, "write", result.LegacyWrite))
            {
                return false;
            }
        }

        return true;
    }

    private static bool ReadPaths(JsonElement fileSystem, string name, List<string> output)
    {
        if (!fileSystem.TryGetProperty(name, out JsonElement paths)
            || paths.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return true;
        }

        if (paths.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        foreach (JsonElement path in paths.EnumerateArray())
        {
            if (path.ValueKind != JsonValueKind.String)
            {
                return false;
            }

            output.Add(path.GetString() ?? string.Empty);
        }

        return output.Count == output.Distinct(StringComparer.Ordinal).Count();
    }

    private static bool IsSubset(IEnumerable<JsonElement> candidates, IEnumerable<JsonElement> allowed)
    {
        JsonElement[] allowedEntries = allowed.ToArray();
        var used = new HashSet<int>();
        foreach (JsonElement candidate in candidates)
        {
            bool found = false;
            for (int index = 0; index < allowedEntries.Length; index++)
            {
                if (!used.Contains(index) && JsonEqual(candidate, allowedEntries[index]))
                {
                    used.Add(index);
                    found = true;
                    break;
                }
            }

            if (!found)
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsSubset(IEnumerable<string> candidates, IEnumerable<string> allowed)
        => candidates.All(candidate => allowed.Contains(candidate, StringComparer.Ordinal));

    private static bool JsonEqual(JsonElement left, JsonElement right)
    {
        if (left.ValueKind != right.ValueKind)
        {
            return false;
        }

        return left.ValueKind switch
        {
            JsonValueKind.Object => JsonObjectsEqual(left, right),
            JsonValueKind.Array => left.GetArrayLength() == right.GetArrayLength()
                && left.EnumerateArray().Zip(right.EnumerateArray()).All(pair => JsonEqual(pair.First, pair.Second)),
            JsonValueKind.String => left.GetString() == right.GetString(),
            JsonValueKind.Number => left.GetRawText() == right.GetRawText(),
            JsonValueKind.True or JsonValueKind.False => left.GetBoolean() == right.GetBoolean(),
            JsonValueKind.Null => true,
            _ => false,
        };
    }

    private static bool JsonObjectsEqual(JsonElement left, JsonElement right)
    {
        JsonProperty[] leftProperties = left.EnumerateObject().OrderBy(item => item.Name, StringComparer.Ordinal).ToArray();
        JsonProperty[] rightProperties = right.EnumerateObject().OrderBy(item => item.Name, StringComparer.Ordinal).ToArray();
        return leftProperties.Length == rightProperties.Length
            && leftProperties.Zip(rightProperties).All(pair => pair.First.Name == pair.Second.Name && JsonEqual(pair.First.Value, pair.Second.Value));
    }

    private static bool OnlyProperties(JsonElement element, params string[] allowed)
    {
        var set = new HashSet<string>(allowed, StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (JsonProperty property in element.EnumerateObject())
        {
            if (!set.Contains(property.Name) || !seen.Add(property.Name))
            {
                return false;
            }
        }

        return true;
    }

    private static string? GetString(JsonElement element, string name)
        => element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty(name, out JsonElement property)
            && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;

    private sealed class PermissionProfile
    {
        public bool NetworkEnabled { get; set; }

        public bool HasFileSystem { get; set; }

        public uint? ScanDepth { get; set; }

        public List<JsonElement> Entries { get; } = [];

        public List<string> LegacyRead { get; } = [];

        public List<string> LegacyWrite { get; } = [];
    }
}

internal enum PermissionGrantKind
{
    Network,
    Entry,
    LegacyRead,
    LegacyWrite,
}

internal sealed record PermissionGrant(PermissionGrantKind Kind, JsonElement Value);
