// Window handler for OpenHarmony: gives IWindow a platform handler so MAUI's window lifecycle
// (Created/Activated/Resumed/Stopped/Destroying) can run.
//
// Window chrome: Title and the window frame (X/Y/Width/Height) are forwarded to the host exports
//   int ohos_host_set_window_title(const char* utf8)
//   int ohos_host_set_window_rect(int x, int y, int w, int h)
// which the ArkTS shell applies to the main window (window.setWindowTitle through the
// SessionManager syscap, API 15+; window.moveWindowTo + window.resize, API 11+). Both exports are
// probed once with NativeLibrary.TryGetExport (the screen reader's announce probe pattern); a host
// library that predates them keeps the recorded-field behaviour - Title/X/Y/Width/Height below stay
// observable off-device - and the handler degrades silently with one status line. The host clamps
// the rectangle it queues (width/height into (0, 16384], x/y into [-32768, 32768]) and rejects a
// non-positive size, so a rectangle is only published once a usable size is known.
using System.Runtime.InteropServices;
using Microsoft.Maui.Handlers;
using Microsoft.OpenHarmony.Hosting;

namespace Microsoft.Maui.Platform;

public sealed class OpenHarmonyWindowHandler : ElementHandler<IWindow, OpenHarmonyView>
{
    private const string HostLibrary = "libopenharmonyhost.so";
    private const string TitleEntryPoint = "ohos_host_set_window_title";
    private const string RectEntryPoint = "ohos_host_set_window_rect";

    public static readonly IPropertyMapper<IWindow, OpenHarmonyWindowHandler> Mapper =
        new PropertyMapper<IWindow, OpenHarmonyWindowHandler>(ElementMapper)
        {
            [nameof(IWindow.Content)] = MapContent,
            [nameof(IWindow.Title)] = MapTitle,
            // The ArkUI window belongs to the shell; the frame MAUI knows (mirrored from the
            // reported surface size by the app host, or set by the app) is pushed back through
            // ohos_host_set_window_rect.
            [nameof(IWindow.X)] = MapX,
            [nameof(IWindow.Y)] = MapY,
            [nameof(IWindow.Width)] = MapWidth,
            [nameof(IWindow.Height)] = MapHeight,
        };

    public OpenHarmonyWindowHandler() : base(Mapper) { }

    [DllImport(HostLibrary, EntryPoint = TitleEntryPoint, CharSet = CharSet.Ansi)]
    private static extern int SetWindowTitleNative([MarshalAs(UnmanagedType.LPUTF8Str)] string title);

    [DllImport(HostLibrary, EntryPoint = RectEntryPoint)]
    private static extern int SetWindowRectNative(int x, int y, int w, int h);

    // 0 unknown, 1 exported, -1 missing (cached probes; see the header).
    private static int s_titleExport;
    private static int s_rectExport;
    private static bool s_chromeMissingLogged;
    private static bool s_chromeFailureLogged;

    protected override OpenHarmonyView CreatePlatformElement() => new();

    public static void MapContent(OpenHarmonyWindowHandler handler, IWindow window)
    {
        // The app host renders window.Content; nothing to do here yet.
    }

    /// <summary>
    /// Records the title requested through <see cref="IWindow.Title"/> and forwards it to the
    /// shell's main window when the host exports the chrome bridge.
    /// </summary>
    public static void MapTitle(OpenHarmonyWindowHandler handler, IWindow window)
    {
        handler.Title = window.Title;
        PublishTitle(window.Title);
    }

    public static void MapX(OpenHarmonyWindowHandler handler, IWindow window)
    {
        handler.X = window.X;
        PublishRect(handler);
    }

    public static void MapY(OpenHarmonyWindowHandler handler, IWindow window)
    {
        handler.Y = window.Y;
        PublishRect(handler);
    }

    public static void MapWidth(OpenHarmonyWindowHandler handler, IWindow window)
    {
        handler.Width = window.Width;
        PublishRect(handler);
    }

    public static void MapHeight(OpenHarmonyWindowHandler handler, IWindow window)
    {
        handler.Height = window.Height;
        PublishRect(handler);
    }

    /// <summary>Last title mapped from the window (the fallback when the chrome export is absent).</summary>
    public string? Title { get; private set; }

    /// <summary>Last X mapped from <see cref="IWindow.X"/>.</summary>
    public double X { get; private set; }

    /// <summary>Last Y mapped from <see cref="IWindow.Y"/>.</summary>
    public double Y { get; private set; }

    /// <summary>Last width mapped from <see cref="IWindow.Width"/>.</summary>
    public double Width { get; private set; }

    /// <summary>Last height mapped from <see cref="IWindow.Height"/>.</summary>
    public double Height { get; private set; }

    private static void PublishTitle(string? title)
    {
        // The host rejects an empty title (the shell then keeps its own); the value is still
        // recorded above.
        if (string.IsNullOrEmpty(title))
        {
            return;
        }
        if (!ExportAvailable(TitleEntryPoint, ref s_titleExport))
        {
            LogMissingOnce(TitleEntryPoint);
            return;
        }
        try
        {
            int rc = SetWindowTitleNative(title);
            if (rc != 0)
            {
                LogFailureOnce($"title not applied (host rc={rc})");
            }
        }
        catch (Exception ex)
        {
            OnNativeFailure(ex, TitleEntryPoint, ref s_titleExport, "title");
        }
    }

    /// <summary>
    /// Publishes X/Y/Width/Height as the shell window's rectangle. Skipped while the size is not
    /// usable (the host rejects a non-positive width/height, and a window that was never framed
    /// has none yet) or while a value cannot be converted to the ints the export takes.
    /// </summary>
    private static void PublishRect(OpenHarmonyWindowHandler handler)
    {
        if (!(handler.Width > 0) || !(handler.Height > 0))
        {
            return;
        }
        if (!TryToDevice(handler.X, out int x) || !TryToDevice(handler.Y, out int y) ||
            !TryToDevice(handler.Width, out int w) || !TryToDevice(handler.Height, out int h))
        {
            return;
        }
        if (!ExportAvailable(RectEntryPoint, ref s_rectExport))
        {
            LogMissingOnce(RectEntryPoint);
            return;
        }
        try
        {
            int rc = SetWindowRectNative(x, y, w, h);
            if (rc != 0)
            {
                LogFailureOnce($"rect not applied (host rc={rc})");
            }
        }
        catch (Exception ex)
        {
            OnNativeFailure(ex, RectEntryPoint, ref s_rectExport, "rect");
        }
    }

    /// <summary>Rounds a window coordinate/size to the int the host export takes.</summary>
    private static bool TryToDevice(double value, out int result)
    {
        if (double.IsNaN(value) || double.IsInfinity(value) || value < int.MinValue || value > int.MaxValue)
        {
            result = 0;
            return false;
        }
        result = (int)Math.Round(value, MidpointRounding.AwayFromZero);
        return true;
    }

    private static void OnNativeFailure(Exception ex, string entryPoint, ref int cache, string what)
    {
        if (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            Volatile.Write(ref cache, -1);
            LogMissingOnce(entryPoint);
            return;
        }
        LogFailureOnce($"{what} failed: {ex.GetType().Name}");
    }

    /// <summary>True when the loaded host library exports <paramref name="entryPoint"/>.</summary>
    private static bool ExportAvailable(string entryPoint, ref int cache)
    {
        int known = Volatile.Read(ref cache);
        if (known != 0)
        {
            return known > 0;
        }
        bool available;
        try
        {
            available = NativeLibrary.TryLoad(HostLibrary, out IntPtr handle) &&
                NativeLibrary.TryGetExport(handle, entryPoint, out _);
        }
        catch (Exception)
        {
            available = false;
        }
        Volatile.Write(ref cache, available ? 1 : -1);
        return available;
    }

    private static void LogMissingOnce(string entryPoint)
    {
        if (s_chromeMissingLogged)
        {
            return;
        }
        s_chromeMissingLogged = true;
        WriteStatus($"[maui] window chrome kept managed-only (no {entryPoint} export)");
    }

    private static void LogFailureOnce(string what)
    {
        if (s_chromeFailureLogged)
        {
            return;
        }
        s_chromeFailureLogged = true;
        WriteStatus($"[maui] window chrome {what}");
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
