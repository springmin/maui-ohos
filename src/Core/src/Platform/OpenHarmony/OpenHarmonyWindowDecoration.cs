// Window decorations for the compositor window: the system minimize / maximize-restore / close
// buttons and the title-band drag region, handed to the managed Window.TitleBar row when the
// ArkTS shell runs the window with app-managed decorations (windowed 2in1/tablet windows; a
// fullscreen phone window keeps the system decorations and never registers the sink).
//
// Contract (host export `ohos_host_window_decor`, ArkTS sink `registerWindowDecorSink`):
//   op 0 minimize | 1 toggle maximize/restore | 2 close | 3 start window move (drag)
//   op 4 request app-managed decorations (the shell hides its own title-bar decor)
//   op 5 release app-managed decorations (the shell restores its decor)
//   op 6 availability probe: 1 when the shell registered the decor sink, else 0
// Ops 0-5 return 0 when the command was queued for the shell and -1 when there is no sink (the
// shell only registers it when the runtime can hide its decorations); the probe answers 0/1.
//
// The slice side of the mapping:
//   * OpenHarmonyWindowHandler.MapTitleBar calls RequestAppManaged when a Window.TitleBar row is
//     created and ReleaseAppManaged when it is cleared, so the shell's own decorations step aside
//     only when the app actually draws a title bar.
//   * OpenHarmonyTitleBarRow draws the three caption buttons at the trailing edge (physical left
//     in RTL, mirroring the platform's caption corner) once the shell confirms the sink, and the
//     renderer dispatches the button commands; a press in the band that no template child consumes
//     starts the window move through the shell's startMoving.
//   * Off-device (no host library, no shell sink) everything degrades: the probe answers false, no
//     buttons draw, and the request/command calls are no-ops that never throw.
using System.Runtime.InteropServices;
using Microsoft.OpenHarmony.Hosting;

namespace Microsoft.Maui.Platform;

/// <summary>One window-decoration command the managed title bar can send to the shell.</summary>
internal enum OpenHarmonyWindowDecorCommand
{
    /// <summary>Minimize the window (op 0).</summary>
    Minimize = 0,

    /// <summary>Toggle maximize/restore; the shell decides from the window's current state (op 1).</summary>
    ToggleMaximize = 1,

    /// <summary>Close the window/ability (op 2).</summary>
    Close = 2,

    /// <summary>Start moving the window with the pointer (op 3, the title-band drag).</summary>
    StartMoving = 3,
}

/// <summary>
/// The managed half of the window-decoration bridge: request/release app-managed decorations and
/// execute the caption/drag commands through the host export. Never throws; every failure degrades
/// to a no-op (or false for the probe).
/// </summary>
internal static partial class OpenHarmonyWindowDecoration
{
    private const string HostLibrary = "libopenharmonyhost.so";

    internal const int OpMinimize = 0;
    internal const int OpToggleMaximize = 1;
    internal const int OpClose = 2;
    internal const int OpStartMoving = 3;
    internal const int OpRequestAppManaged = 4;
    internal const int OpReleaseAppManaged = 5;
    internal const int OpProbe = 6;

    [LibraryImport(HostLibrary, EntryPoint = "ohos_host_window_decor")]
    private static partial int WindowDecor(int op);

    private static bool s_unavailable;
    private static bool s_available;
    private static bool s_appManagedRequested;

    /// <summary>
    /// Test seam: forces the availability answer (null = ask the host). The suite uses it to pin
    /// the caption buttons and drag mapping without a shell; device builds keep it null.
    /// </summary>
    internal static bool? AvailabilityOverride { get; set; }

    /// <summary>
    /// Test seam: receives the commands the row dispatches (null = send to the host). The suite
    /// pins the button/drag mapping through it; device builds keep it null.
    /// </summary>
    internal static Func<OpenHarmonyWindowDecorCommand, bool>? CommandOverride { get; set; }

    /// <summary>
    /// True when the shell registered the decor sink (it runs with app-managed decorations). The
    /// answer is cached once true; it stays false off-device and on a fullscreen phone window,
    /// where the row then draws no caption buttons.
    /// </summary>
    internal static bool Available
    {
        get
        {
            if (AvailabilityOverride is { } forced)
            {
                return forced;
            }
            if (s_available)
            {
                return true;
            }
            if (s_unavailable)
            {
                return false;
            }
            try
            {
                s_available = WindowDecor(OpProbe) == 1;
                return s_available;
            }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
            {
                s_unavailable = true;
                return false;
            }
        }
    }

    /// <summary>Asks the shell to hand the window's title-bar decorations to the app (op 4).</summary>
    internal static void RequestAppManaged() => Dispatch(OpRequestAppManaged);

    /// <summary>Gives the window's decorations back to the shell (op 5).</summary>
    internal static void ReleaseAppManaged() => Dispatch(OpReleaseAppManaged);

    /// <summary>
    /// Requests or releases app-managed decorations only when the requested state changed, so the
    /// render loop can call this every frame (the shell's own decor steps aside exactly while a
    /// visible Window.TitleBar row exists). Off-device the dispatch is a silent no-op.
    /// </summary>
    internal static void EnsureAppManaged(bool requested)
    {
        if (requested == s_appManagedRequested)
        {
            return;
        }
        s_appManagedRequested = requested;
        Dispatch(requested ? OpRequestAppManaged : OpReleaseAppManaged);
    }

    /// <summary>Executes one caption/drag command (the CommandOverride seam wins for tests).</summary>
    internal static bool Execute(OpenHarmonyWindowDecorCommand command)
    {
        if (CommandOverride is { } custom)
        {
            return custom(command);
        }
        return Dispatch((int)command) == 0;
    }

    private static int Dispatch(int op)
    {
        if (s_unavailable)
        {
            return -1;
        }
        try
        {
            return WindowDecor(op);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            s_unavailable = true;
            return -1;
        }
    }
}
