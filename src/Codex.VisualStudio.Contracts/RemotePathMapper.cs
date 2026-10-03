namespace Codex.VisualStudio.Contracts;

/// <summary>Describes why a path could not be translated between local and server roots.</summary>
public enum RemotePathMappingFailure
{
    None,
    InvalidPath,
    OutsideConfiguredRoot,
    LocalBoundaryViolation,
}

/// <summary>
/// Maps local and server paths by normalized path components. Methods without an
/// <see cref="ILocalPathBoundary"/> perform lexical mapping only; trust-boundary operations must
/// use the overload that validates the physical local location.
/// </summary>
public sealed class RemotePathMapper
{
    private readonly LocalPath localRoot;
    private readonly ServerPath serverRoot;

    public RemotePathMapper(LocalPath localRoot, ServerPath serverRoot)
    {
        this.localRoot = localRoot ?? throw new ArgumentNullException(nameof(localRoot));
        this.serverRoot = serverRoot ?? throw new ArgumentNullException(nameof(serverRoot));
    }

    public LocalPath LocalRoot => localRoot;

    public ServerPath ServerRoot => serverRoot;

    public bool TryMapLocalToServer(LocalPath localPath, out ServerPath serverPath)
        => TryMapLocalToServer(localPath, out serverPath, out _);

    public bool TryMapLocalToServer(
        LocalPath localPath,
        out ServerPath serverPath,
        out RemotePathMappingFailure failure)
    {
        serverPath = null!;
        if (localPath is null)
        {
            failure = RemotePathMappingFailure.InvalidPath;
            return false;
        }

        if (!localPath.StaysWithin(localRoot))
        {
            failure = RemotePathMappingFailure.OutsideConfiguredRoot;
            return false;
        }

        try
        {
            serverPath = serverRoot.AppendRelative(localPath.GetRelativeSegments(localRoot));
            failure = RemotePathMappingFailure.None;
            return true;
        }
        catch (ArgumentException)
        {
            failure = RemotePathMappingFailure.InvalidPath;
            return false;
        }
    }

    /// <summary>Maps a local path and verifies its resolved filesystem location remains in-root.</summary>
    public bool TryMapLocalToServer(
        LocalPath localPath,
        ILocalPathBoundary boundary,
        out ServerPath serverPath,
        out RemotePathMappingFailure failure)
    {
        if (!TryMapLocalToServer(localPath, out serverPath, out failure))
        {
            return false;
        }

        if (boundary is null || !boundary.IsWithinRoot(localRoot, localPath))
        {
            serverPath = null!;
            failure = RemotePathMappingFailure.LocalBoundaryViolation;
            return false;
        }

        return true;
    }

    public bool TryMapServerToLocal(ServerPath serverPath, out LocalPath localPath)
        => TryMapServerToLocal(serverPath, out localPath, out _);

    public bool TryMapServerToLocal(
        ServerPath serverPath,
        out LocalPath localPath,
        out RemotePathMappingFailure failure)
    {
        localPath = null!;
        if (serverPath is null)
        {
            failure = RemotePathMappingFailure.InvalidPath;
            return false;
        }

        if (!serverPath.StaysWithin(serverRoot))
        {
            failure = RemotePathMappingFailure.OutsideConfiguredRoot;
            return false;
        }

        try
        {
            localPath = localRoot.AppendRelative(serverPath.GetRelativeSegments(serverRoot));
            failure = RemotePathMappingFailure.None;
            return true;
        }
        catch (ArgumentException)
        {
            failure = RemotePathMappingFailure.InvalidPath;
            return false;
        }
    }

    /// <summary>Maps a server path and verifies the resulting local filesystem location remains in-root.</summary>
    public bool TryMapServerToLocal(
        ServerPath serverPath,
        ILocalPathBoundary boundary,
        out LocalPath localPath,
        out RemotePathMappingFailure failure)
    {
        if (!TryMapServerToLocal(serverPath, out localPath, out failure))
        {
            return false;
        }

        if (boundary is null || !boundary.IsWithinRoot(localRoot, localPath))
        {
            localPath = null!;
            failure = RemotePathMappingFailure.LocalBoundaryViolation;
            return false;
        }

        return true;
    }

}
