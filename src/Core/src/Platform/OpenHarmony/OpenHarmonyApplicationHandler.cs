// Application handler for OpenHarmony: gives IApplication a platform handler so MAUI's
// application-level APIs (Quit, OpenWindow, CloseWindow, ActivateWindow) run through the slice.
// OpenHarmonyMauiAppHost.Run attaches it before creating the first window, so Application.Handler
// is non-null and Application.Windows reflects the window the host actually shows.
//
// Single window: the ArkTS shell owns the process and exactly one XComponent surface, and the
// platform bridge has no window-creation export, so this host keeps one MAUI window live at a
// time. The commands are mapped honestly instead of failing silently:
//   * Quit ("Terminate"): the ability lifecycle belongs to the shell, which has no terminate
//     export; the request is reported once and the app keeps running.
//   * OpenWindow with a live window: the request cannot be satisfied on the single surface, so
//     it is declined once (LastOpenWindowResult=CurrentWindowKept) and the current window stays.
//     The requested MAUI window stays pending in the application and is not added to Windows.
//   * OpenWindow with no live window (after CloseWindow): the requested window is realized with
//     IApplication.CreateWindow(request.State) and adopted by the host on the same surface
//     (LastOpenWindowResult=OpenedWindow), so an app can open a window again.
//   * CloseWindow: raises IWindow.Destroying (removes the window from Application.Windows,
//     raises Destroying and disconnects its handler) and tells the host to drop it. The shell's
//     window stays open - there is no ohos_host_close_window export.
//   * ActivateWindow: the live window is already in front (no-op); a request for a window that
//     is not live is declined once; a live window that was never activated is activated.
//
// A real multi-window implementation would need:
//   1. a platform window bridge: an ohos_host_open_window(request_id) export the shell maps to
//      window.createWindow/openAbility + a second XComponent surface (and a matching
//      ohos_host_close_window(request_id)), or an ArkUI window-manager sink;
//   2. per-window host state: OpenHarmonyMauiAppHost holds one _window, one
//      OpenHarmonyWindowSurface and one OpenHarmonyWindowRenderer today, so surfaces, renderers
//      and input routing would have to be keyed by window id, with OpenWindowRequest.State
//      carrying the id the shell reports back;
//   3. per-window lifecycle: Created/Activated/Resumed/Stopped/Destroying would have to carry the
//      platform window id so each MAUI window tracks its own platform window.
using Microsoft.Maui.Handlers;
using Microsoft.OpenHarmony.Hosting;

namespace Microsoft.Maui.Platform;

/// <summary>The outcome of the last <see cref="IApplication.OpenWindow"/> request.</summary>
public enum OpenHarmonyOpenWindowResult
{
    /// <summary>No OpenWindow request was seen yet.</summary>
    None = 0,

    /// <summary>A live window already exists; the single-window host keeps it.</summary>
    CurrentWindowKept = 1,

    /// <summary>The requested window was created and adopted (no window was live).</summary>
    OpenedWindow = 2,

    /// <summary>The request could not be served (host not attached, malformed or refused).</summary>
    NotSupported = 3,
}

/// <summary>
/// The OpenHarmony platform application object: the managed handle the application handler maps
/// onto. The ArkTS shell owns the process and its single main window, so the object only carries
/// the app host; a real multi-window implementation would grow one platform window per entry.
/// </summary>
public sealed class OpenHarmonyPlatformApplication
{
    /// <summary>The host that owns the current window (set when the handler is attached).</summary>
    public OpenHarmonyMauiAppHost? Host { get; internal set; }
}

/// <summary>
/// Handler for <see cref="IApplication"/> on OpenHarmony: the application-level commands of
/// MAUI's single-window host (see the file header for the exact semantics and the multi-window
/// requirements).
/// </summary>
public sealed class OpenHarmonyApplicationHandler : ElementHandler<IApplication, OpenHarmonyPlatformApplication>
{
    /// <summary>The command <see cref="Microsoft.Maui.Controls.Application.Quit"/> invokes.</summary>
    internal const string TerminateCommandKey = "Terminate";

    public static readonly IPropertyMapper<IApplication, OpenHarmonyApplicationHandler> Mapper =
        new PropertyMapper<IApplication, OpenHarmonyApplicationHandler>(ElementMapper);

    public static readonly CommandMapper<IApplication, OpenHarmonyApplicationHandler> CommandMapper =
        new(ElementCommandMapper)
        {
            [TerminateCommandKey] = MapTerminate,
            [nameof(IApplication.OpenWindow)] = MapOpenWindow,
            [nameof(IApplication.CloseWindow)] = MapCloseWindow,
            [nameof(IApplication.ActivateWindow)] = MapActivateWindow,
        };

    // One status line per unsupported condition (a recurring request must not spam the status log,
    // but the first one must never be silent).
    private static bool s_openWindowKeptLogged;
    private static bool s_openWindowUnsupportedLogged;
    private static bool s_activateWindowLogged;
    private static bool s_terminateLogged;

    public OpenHarmonyApplicationHandler() : base(Mapper, CommandMapper)
    {
    }

    /// <summary>Outcome of the last <see cref="IApplication.OpenWindow"/> request.</summary>
    public OpenHarmonyOpenWindowResult LastOpenWindowResult { get; private set; }

    /// <summary>The platform application object (null until the handler is connected).</summary>
    public OpenHarmonyPlatformApplication? PlatformApplication => base.PlatformView as OpenHarmonyPlatformApplication;

    /// <summary>The host that owns the single live window (attached by the app host).</summary>
    internal OpenHarmonyMauiAppHost? Host
    {
        get => PlatformApplication?.Host;
        set
        {
            if (PlatformApplication is { } platform)
            {
                platform.Host = value;
            }
        }
    }

    protected override OpenHarmonyPlatformApplication CreatePlatformElement() => new();

    /// <summary>Maps <see cref="Microsoft.Maui.Controls.Application.Quit"/>; the shell owns the process.</summary>
    public static void MapTerminate(OpenHarmonyApplicationHandler handler, IApplication application, object? args)
    {
        WriteOnce(ref s_terminateLogged,
            "[maui] Quit is not supported on OpenHarmony: the shell owns the ability lifecycle");
    }

    /// <summary>
    /// Opens a window on the single surface: declined once while a window is live, otherwise the
    /// requested window is realized through the application and adopted by the host.
    /// </summary>
    public static void MapOpenWindow(OpenHarmonyApplicationHandler handler, IApplication application, object? args)
    {
        if (args is not OpenWindowRequest request)
        {
            handler.LastOpenWindowResult = OpenHarmonyOpenWindowResult.NotSupported;
            WriteOnce(ref s_openWindowUnsupportedLogged,
                "[maui] OpenWindow ignored: the command carried no OpenWindowRequest");
            return;
        }

        OpenHarmonyMauiAppHost? host = handler.Host;
        if (host is null)
        {
            handler.LastOpenWindowResult = OpenHarmonyOpenWindowResult.NotSupported;
            WriteOnce(ref s_openWindowUnsupportedLogged,
                "[maui] OpenWindow not supported before the app host is attached");
            return;
        }

        if (host.Window is not null)
        {
            // The ArkTS shell has one XComponent surface, so a second window cannot be shown.
            // Keep the current one and say so: the request is never silently dropped.
            handler.LastOpenWindowResult = OpenHarmonyOpenWindowResult.CurrentWindowKept;
            WriteOnce(ref s_openWindowKeptLogged,
                "[maui] OpenWindow not supported on the single-window OpenHarmony host; the current window stays");
            return;
        }

        IWindow? window = RealizeWindow(handler, application, request, out string? failure);
        if (window is null)
        {
            handler.LastOpenWindowResult = OpenHarmonyOpenWindowResult.NotSupported;
            WriteOnce(ref s_openWindowUnsupportedLogged, failure ?? "[maui] OpenWindow could not create the window");
            return;
        }

        if (!host.TryAdoptWindow(window))
        {
            handler.LastOpenWindowResult = OpenHarmonyOpenWindowResult.NotSupported;
            WriteOnce(ref s_openWindowUnsupportedLogged,
                "[maui] OpenWindow could not adopt the created window on the single-window host");
            // A realized window that was not adopted would leave Application.Windows ahead of
            // what the platform shows, so close it again to keep the list honest.
            CloseRealizedWindow(window);
            return;
        }

        handler.LastOpenWindowResult = OpenHarmonyOpenWindowResult.OpenedWindow;
        WriteStatus("[maui] OpenWindow adopted the requested window on the single-window host");
    }

    /// <summary>
    /// Closes the MAUI window: <see cref="IWindow.Destroying"/> removes it from
    /// Application.Windows, raises Destroying and disconnects its handler; the host then drops it.
    /// </summary>
    public static void MapCloseWindow(OpenHarmonyApplicationHandler handler, IApplication application, object? args)
    {
        if (args is not IWindow window)
        {
            return;
        }
        // A window that already went through Destroying has no handler left, and a window the
        // host never adopted is not in Application.Windows; both are no-ops (idempotent close).
        if (window.Handler is null && !application.Windows.Contains(window))
        {
            return;
        }
        try
        {
            window.Destroying();
            handler.Host?.NotifyWindowClosed(window);
        }
        catch (Exception ex)
        {
            WriteStatus($"[maui] CloseWindow failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// Brings the live window to the front. A live window that is already activated is a no-op;
    /// one that never saw the platform activation event is activated here. A window that is not
    /// the live one cannot be brought to front on the single surface and is declined once.
    /// </summary>
    public static void MapActivateWindow(OpenHarmonyApplicationHandler handler, IApplication application, object? args)
    {
        if (args is not IWindow window)
        {
            return;
        }
        if (!ReferenceEquals(handler.Host?.Window, window))
        {
            WriteOnce(ref s_activateWindowLogged,
                "[maui] ActivateWindow ignored: the window is not the live OpenHarmony window");
            return;
        }
        if (window is Microsoft.Maui.Controls.Window { IsActivated: true })
        {
            return;
        }
        window.Activated();
    }

    /// <summary>
    /// Realizes the window an <see cref="IApplication.OpenWindow"/> request refers to. The request
    /// state carries the pending window id the Controls layer registered, so
    /// <see cref="IApplication.CreateWindow"/> returns exactly the window the app asked to open.
    /// </summary>
    private static IWindow? RealizeWindow(
        OpenHarmonyApplicationHandler handler,
        IApplication application,
        OpenWindowRequest request,
        out string? failure)
    {
        failure = null;
        if (handler.MauiContext is not IMauiContext context)
        {
            failure = "[maui] OpenWindow ignored: the handler has no MauiContext";
            return null;
        }
        try
        {
            IActivationState activationState = new ActivationState(context, request.State ?? new PersistedState());
            return application.CreateWindow(activationState);
        }
        catch (Exception ex)
        {
            failure = $"[maui] OpenWindow could not create the window: {ex.GetType().Name}: {ex.Message}";
            return null;
        }
    }

    /// <summary>
    /// Destroys a window the host refused to adopt so it does not linger in Application.Windows
    /// as a window the platform never shows (Destroying removes it from the list again).
    /// </summary>
    private static void CloseRealizedWindow(IWindow window)
    {
        try
        {
            window.Destroying();
        }
        catch (Exception ex)
        {
            WriteStatus($"[maui] OpenWindow cleanup failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>Writes a status line the first time a condition occurs (never spams).</summary>
    private static void WriteOnce(ref bool logged, string message)
    {
        if (logged)
        {
            return;
        }
        logged = true;
        WriteStatus(message);
    }

    private static void WriteStatus(string message)
    {
        try
        {
            OpenHarmonyBridge.WriteStatus(message);
        }
        catch (Exception)
        {
            // Status logging is diagnostics; it must never surface into the handler.
        }
    }
}
