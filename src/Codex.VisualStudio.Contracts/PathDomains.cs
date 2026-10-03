using System;
using System.Collections.Generic;
using System.Linq;

namespace Codex.VisualStudio.Contracts;

/// <summary>Identifies a normalized absolute path on the Visual Studio host.</summary>
public sealed class LocalPath : IEquatable<LocalPath>
{
    private readonly NormalizedPath path;

    private LocalPath(NormalizedPath path) => this.path = path;

    /// <summary>Gets the canonical absolute path. Windows paths use backslashes.</summary>
    public string Value => path.Value;

    public PathFamily Family => path.Flavor;

    /// <summary>Gets a stable identity form that folds Windows path case and preserves POSIX case.</summary>
    public string IdentityValue => path.IdentityValue;

    /// <summary>Parses and normalizes an absolute local filesystem path.</summary>
    public static bool TryCreate(string? value, out LocalPath path)
    {
        path = null!;
        if (!NormalizedPath.TryParse(value, allowPosix: true, out NormalizedPath? parsed))
        {
            return false;
        }

        path = new LocalPath(parsed!);
        return true;
    }

    public static LocalPath Create(string value)
        => TryCreate(value, out LocalPath path)
            ? path
            : throw new ArgumentException("An absolute local filesystem path is required.", nameof(value));

    public bool Equals(LocalPath? other) => other is not null && path.Equals(other.path);

    public override bool Equals(object? obj) => Equals(obj as LocalPath);

    public override int GetHashCode() => path.GetHashCode();

    public override string ToString() => Value;

    internal bool IsWithin(LocalPath root) => path.IsWithin(root.path);

    internal bool StaysWithin(LocalPath root) => path.StaysWithin(root.path);

    internal string[] GetRelativeSegments(LocalPath root) => path.GetRelativeSegments(root.path);

    internal LocalPath AppendRelative(string[] relativeSegments)
        => new LocalPath(path.AppendRelative(relativeSegments));
}

/// <summary>Identifies a normalized absolute path in the app-server filesystem.</summary>
public sealed class ServerPath : IEquatable<ServerPath>
{
    private readonly NormalizedPath path;

    private ServerPath(NormalizedPath path) => this.path = path;

    /// <summary>Gets the canonical absolute path using the path's native separator.</summary>
    public string Value => path.Value;

    public PathFamily Family => path.Flavor;

    /// <summary>Gets a stable identity form that folds Windows path case and preserves POSIX case.</summary>
    public string IdentityValue => path.IdentityValue;

    /// <summary>Parses and normalizes an absolute server filesystem path.</summary>
    public static bool TryCreate(string? value, out ServerPath path)
    {
        path = null!;
        if (!NormalizedPath.TryParse(value, allowPosix: true, out NormalizedPath? parsed))
        {
            return false;
        }

        path = new ServerPath(parsed!);
        return true;
    }

    public static ServerPath Create(string value)
        => TryCreate(value, out ServerPath path)
            ? path
            : throw new ArgumentException("An absolute server filesystem path is required.", nameof(value));

    public bool Equals(ServerPath? other) => other is not null && path.Equals(other.path);

    public override bool Equals(object? obj) => Equals(obj as ServerPath);

    public override int GetHashCode() => path.GetHashCode();

    public override string ToString() => Value;

    internal bool IsWithin(ServerPath root) => path.IsWithin(root.path);

    internal bool StaysWithin(ServerPath root) => path.StaysWithin(root.path);

    internal string[] GetRelativeSegments(ServerPath root) => path.GetRelativeSegments(root.path);

    internal ServerPath AppendRelative(string[] relativeSegments)
        => new ServerPath(path.AppendRelative(relativeSegments));
}

/// <summary>
/// Validates that a local path remains physically under its configured local root after resolving
/// existing symbolic links, junctions, and other filesystem reparse points. Implementations must
/// fail closed when the physical location cannot be determined.
/// </summary>
public interface ILocalPathBoundary
{
    bool IsWithinRoot(LocalPath root, LocalPath candidate);
}

public enum PathFamily
{
    Posix,
    WindowsDrive,
    WindowsUnc,
}

internal sealed class NormalizedPath : IEquatable<NormalizedPath>
{
    private static readonly char[] WindowsSeparators = { '/', '\\' };
    private static readonly char[] PosixSeparators = { '/' };
    private static readonly char[] WindowsInvalidCharacters = { '<', '>', ':', '"', '|', '?', '*', '/', '\\' };
    private static readonly string[] WindowsDeviceNames =
    {
        "CON", "PRN", "AUX", "NUL", "CONIN$", "CONOUT$", "CLOCK$", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "COM¹", "COM²", "COM³", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
        "LPT¹", "LPT²", "LPT³",
    };
    private readonly string root;
    private readonly string[] segments;
    private readonly string[] inputSegments;
    private readonly StringComparison comparison;

    private NormalizedPath(PathFamily flavor, string root, string[] segments, string[] inputSegments)
    {
        Flavor = flavor;
        this.root = root;
        this.segments = segments;
        this.inputSegments = inputSegments;
        comparison = flavor == PathFamily.Posix ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        Value = Format(flavor, root, segments);
    }

    public PathFamily Flavor { get; }

    public string Value { get; }

    public string IdentityValue => Flavor == PathFamily.Posix ? Value : Value.ToUpperInvariant();

    public static bool TryParse(string? input, bool allowPosix, out NormalizedPath? path)
    {
        path = null;
        if (string.IsNullOrWhiteSpace(input) || input.Any(char.IsControl))
        {
            return false;
        }

        string value = input!;
        if (value.StartsWith(@"\\.\", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (value.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase))
        {
            value = @"\\" + value.Substring(8);
        }
        else if (value.StartsWith(@"\\?\", StringComparison.OrdinalIgnoreCase))
        {
            value = value.Substring(4);
            if (!IsDriveRooted(value))
            {
                return false;
            }
        }

        PathFamily flavor;
        string root;
        if (value.StartsWith(@"\\", StringComparison.Ordinal))
        {
            string[] uncParts = value.Substring(2).Split(WindowsSeparators, StringSplitOptions.RemoveEmptyEntries);
            if (uncParts.Length < 2 || !IsValidWindowsSegment(uncParts[0]) || !IsValidWindowsSegment(uncParts[1]))
            {
                return false;
            }

            flavor = PathFamily.WindowsUnc;
            root = @"\\" + uncParts[0] + @"\" + uncParts[1];
            int segmentStart = FindAfterUncRoot(value);
            value = value.Substring(segmentStart);
        }
        else if (IsDriveRooted(value))
        {
            flavor = PathFamily.WindowsDrive;
            root = char.ToUpperInvariant(value[0]) + @":\";
            value = value.Substring(3);
        }
        else if (allowPosix && value[0] == '/')
        {
            flavor = PathFamily.Posix;
            root = "/";
        }
        else
        {
            return false;
        }

        char[] separators = flavor == PathFamily.Posix ? PosixSeparators : WindowsSeparators;
        string[] sourceSegments = value.Split(separators, StringSplitOptions.RemoveEmptyEntries);
        var normalizedSegments = new List<string>();
        foreach (string segment in sourceSegments)
        {
            if (segment == ".")
            {
                continue;
            }

            if (segment == "..")
            {
                if (normalizedSegments.Count == 0)
                {
                    return false;
                }

                normalizedSegments.RemoveAt(normalizedSegments.Count - 1);
                continue;
            }

            if (segment.IndexOf('\0') >= 0 || (flavor != PathFamily.Posix && !IsValidWindowsSegment(segment)))
            {
                return false;
            }

            normalizedSegments.Add(segment);
        }

        path = new NormalizedPath(flavor, root, normalizedSegments.ToArray(), sourceSegments);
        return true;
    }

    public bool IsWithin(NormalizedPath otherRoot)
    {
        if (Flavor != otherRoot.Flavor || !string.Equals(root, otherRoot.root, comparison))
        {
            return false;
        }

        if (segments.Length < otherRoot.segments.Length)
        {
            return false;
        }

        for (int index = 0; index < otherRoot.segments.Length; index++)
        {
            if (!string.Equals(segments[index], otherRoot.segments[index], comparison))
            {
                return false;
            }
        }

        return true;
    }

    public bool StaysWithin(NormalizedPath otherRoot)
    {
        if (!IsWithin(otherRoot))
        {
            return false;
        }

        var current = new List<string>();
        foreach (string segment in inputSegments)
        {
            if (segment == ".")
            {
                continue;
            }

            if (segment == "..")
            {
                if (IsAtRoot(current, otherRoot) || current.Count == 0)
                {
                    return false;
                }

                current.RemoveAt(current.Count - 1);
            }
            else
            {
                current.Add(segment);
            }
        }

        return true;
    }

    public string[] GetRelativeSegments(NormalizedPath otherRoot)
    {
        if (!IsWithin(otherRoot))
        {
            throw new InvalidOperationException("The path is outside the requested root.");
        }

        return segments.Skip(otherRoot.segments.Length).ToArray();
    }

    public NormalizedPath AppendRelative(string[] relativeSegments)
    {
        var combined = new List<string>(segments);
        foreach (string segment in relativeSegments)
        {
            if (segment.Length == 0 || segment == "." || segment == "..")
            {
                throw new ArgumentException("The relative path contains an invalid path segment.", nameof(relativeSegments));
            }

            if (segment.IndexOf('\0') >= 0 || (Flavor != PathFamily.Posix && !IsValidWindowsSegment(segment)))
            {
                throw new ArgumentException("The relative path contains an invalid path segment.", nameof(relativeSegments));
            }

            combined.Add(segment);
        }

        return new NormalizedPath(Flavor, root, combined.ToArray(), combined.ToArray());
    }

    public bool Equals(NormalizedPath? other)
    {
        if (other is null || Flavor != other.Flavor || segments.Length != other.segments.Length
            || !string.Equals(root, other.root, comparison))
        {
            return false;
        }

        for (int index = 0; index < segments.Length; index++)
        {
            if (!string.Equals(segments[index], other.segments[index], comparison))
            {
                return false;
            }
        }

        return true;
    }

    public override bool Equals(object? obj) => Equals(obj as NormalizedPath);

    public override int GetHashCode()
    {
        var comparer = Flavor == PathFamily.Posix ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase;
        unchecked
        {
            int hash = ((int)Flavor * 397) ^ comparer.GetHashCode(root);
            foreach (string segment in segments)
            {
                hash = (hash * 31) + comparer.GetHashCode(segment);
            }

            return hash;
        }
    }

    private static bool IsDriveRooted(string value)
        => value.Length >= 3 && IsAsciiLetter(value[0]) && value[1] == ':' && (value[2] == '\\' || value[2] == '/');

    private static bool IsAsciiLetter(char value)
        => (value >= 'A' && value <= 'Z') || (value >= 'a' && value <= 'z');

    private static bool IsValidWindowsSegment(string value)
    {
        if (value.Length == 0 || value[value.Length - 1] == '.' || value[value.Length - 1] == ' '
            || value.IndexOfAny(WindowsInvalidCharacters) >= 0)
        {
            return false;
        }

        int extensionSeparator = value.IndexOf('.');
        string deviceName = (extensionSeparator < 0 ? value : value.Substring(0, extensionSeparator)).TrimEnd('.', ' ');
        return !WindowsDeviceNames.Contains(deviceName, StringComparer.OrdinalIgnoreCase);
    }

    private static int FindAfterUncRoot(string value)
    {
        int index = 2;
        while (index < value.Length && IsWindowsSeparator(value[index]))
        {
            index++;
        }

        while (index < value.Length && !IsWindowsSeparator(value[index]))
        {
            index++;
        }

        while (index < value.Length && IsWindowsSeparator(value[index]))
        {
            index++;
        }

        while (index < value.Length && !IsWindowsSeparator(value[index]))
        {
            index++;
        }

        while (index < value.Length && IsWindowsSeparator(value[index]))
        {
            index++;
        }

        return index;
    }

    private static bool IsWindowsSeparator(char value) => value == '\\' || value == '/';

    private static string Format(PathFamily flavor, string root, string[] segments)
    {
        if (segments.Length == 0)
        {
            return root;
        }

        char separator = flavor == PathFamily.Posix ? '/' : '\\';
        string prefix = root.EndsWith(separator.ToString(), StringComparison.Ordinal) ? root : root + separator;
        return prefix + string.Join(separator.ToString(), segments);
    }

    private bool IsAtRoot(List<string> current, NormalizedPath otherRoot)
    {
        if (current.Count != otherRoot.segments.Length)
        {
            return false;
        }

        for (int index = 0; index < current.Count; index++)
        {
            if (!string.Equals(current[index], otherRoot.segments[index], comparison))
            {
                return false;
            }
        }

        return true;
    }
}
