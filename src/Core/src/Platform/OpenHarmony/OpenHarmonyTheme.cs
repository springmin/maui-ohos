
// App theme following for OpenHarmony through the ArkUI colour-mode bridge.
//
// The ArkTS shell keeps AppStorage 'colorMode' in sync with the device light/dark setting
// (Environment.envProp('colorMode') plus @StorageProp/@Watch in pages/Index.ets) and forwards
// every change with host.notifyTheme(isDark). The native host hands that to the managed
// callback registered through ohos_host_theme_set_listener, and the handler sets
// Application.Current.UserAppTheme to Dark/Light so RequestedTheme and AppThemeBinding follow
// the platform - but only while the app has not chosen a theme itself: an app-set UserAppTheme
// is respected (and LastIsDark keeps tracking the system mode for IAppInfo.RequestedTheme).
//
// Every native call is guarded: without libopenharmonyhost.so (desktop builds) or without the
// export (an older host), Register is a no-op, the MAUI default stays in place and nothing throws.
// The device mode that arrives before the application instance exists is remembered and applied
// by Attach, which the app host calls once IApplication is available.
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Microsoft.Maui.Controls;

namespace Microsoft.Maui.Platform;

/// <summary>Follows the device light/dark mode by updating the managed application theme.</summary>
internal static partial class OpenHarmonyTheme
{
    private const string HostLibrary = "libopenharmonyhost.so";

    /// <summary>Registers the managed callback the host invokes for notifyTheme.</summary>
    [LibraryImport(HostLibrary, EntryPoint = "ohos_host_theme_set_listener")]
    private static partial void ThemeSetListener(IntPtr callback);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void ThemeCallback(int isDark);

    private static ThemeCallback? s_callback;
    private static bool s_registered;
    private static bool s_available = true;
    private static bool? s_lastTheme;

    /// <summary>The AppTheme this handler last wrote to UserAppTheme.</summary>
    private static AppTheme s_appliedTheme = AppTheme.Unspecified;

    /// <summary>The last mode reported by the shell (null until the shell reports one).</summary>
    internal static bool? LastIsDark => s_lastTheme;

    /// <summary>Registers the native theme callback; a guarded no-op off-device.</summary>
    public static void Register()
    {
        if (s_registered || !s_available)
        {
            return;
        }
        s_registered = true;
        try
        {
            s_callback = OnNativeTheme;
            ThemeSetListener(Marshal.GetFunctionPointerForDelegate(s_callback));
        }
        catch (DllNotFoundException)
        {
            s_available = false;
        }
        catch (EntryPointNotFoundException)
        {
            s_available = false;
        }
    }

    /// <summary>The host calls this for every notifyTheme(isDark) from the ArkTS shell.</summary>
    internal static void OnPlatformThemeChanged(bool isDark)
    {
        s_lastTheme = isDark;
        Apply(isDark);
    }

    /// <summary>Native-shaped thunk: the NAPI export delivers 0/1.</summary>
    private static void OnNativeTheme(int isDark)
    {
        // A reverse P/Invoke entry: the change runs the app's theme application path, so an
        // exception must not unwind into the native frame (MB-2).
        try
        {
            OnPlatformThemeChanged(isDark != 0);
        }
        catch (Exception ex)
        {
            OpenHarmonyStatus.NativeCallbackFailed("theme change", ex);
        }
    }

    /// <summary>
    /// Follows the platform mode by setting <see cref="Application.UserAppTheme"/>, but only
    /// while the app has not chosen a theme itself: an app-set value (anything other than
    /// Unspecified and other than the value written here) is left untouched while the platform
    /// mode is still tracked in <see cref="LastIsDark"/>. The one indistinguishable case is an
    /// app setting exactly the value this handler last wrote - MAUI exposes no hook that
    /// separates the two writes, so that value is treated as still following the platform.
    /// </summary>
    private static void Apply(bool isDark)
    {
        try
        {
            Application? application = Application.Current;
            if (application is null)
            {
                return;
            }
            AppTheme current = application.UserAppTheme;
            if (current != AppTheme.Unspecified && current != s_appliedTheme)
            {
                // The app explicitly requested its own theme: respect it.
                return;
            }
            AppTheme theme = isDark ? AppTheme.Dark : AppTheme.Light;
            if (current != theme)
            {
                application.UserAppTheme = theme;
            }
            s_appliedTheme = theme;
        }
        catch (Exception)
        {
            // Following the platform theme must never take the app down.
        }
    }

    /// <summary>Applies the remembered platform mode once the application instance exists.</summary>
    public static void Attach(Application? application)
    {
        if (application is null || s_lastTheme is not bool isDark)
        {
            return;
        }
        try
        {
            if (application.UserAppTheme != AppTheme.Unspecified)
            {
                // The app already picked its theme (typically in its constructor): keep it.
                return;
            }
            AppTheme theme = isDark ? AppTheme.Dark : AppTheme.Light;
            application.UserAppTheme = theme;
            s_appliedTheme = theme;
        }
        catch (Exception)
        {
            // The application theme is best-effort.
        }
    }

    [ModuleInitializer]
    internal static void Initialize() => Register();
}
