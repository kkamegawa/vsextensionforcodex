using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Codex.VisualStudio.Worker;

/// <summary>
/// Ensures this process has a console window that is hidden from the user.
/// </summary>
/// <remarks>
/// The worker is launched by <c>WorkerBridge</c> with a console (instead of
/// CREATE_NO_WINDOW) so that codex app-server - and the cmd.exe processes it
/// spawns to run shell commands - inherit a console and do not each get their
/// own visible console window. As a defensive measure in case the worker ends
/// up with a visible console for any reason (e.g. launched manually for
/// diagnostics), hide it immediately at startup.
/// </remarks>
[SupportedOSPlatform("windows")]
internal static partial class HiddenConsole
{
    private const int SwHide = 0;

    public static void Hide()
    {
        IntPtr consoleWindow = GetConsoleWindow();
        if (consoleWindow != IntPtr.Zero)
        {
            ShowWindow(consoleWindow, SwHide);
        }
    }

    [LibraryImport("kernel32.dll")]
    private static partial IntPtr GetConsoleWindow();

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ShowWindow(IntPtr hWnd, int nCmdShow);
}
