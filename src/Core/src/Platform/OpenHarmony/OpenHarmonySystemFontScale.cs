// System font scale reporting for the self-drawn compositor (T21 leftovers).
//
// The ArkTS shell reads the user's font size setting (Configuration.fontSizeScale) when the
// ability is created and on every configuration update, and forwards it with
// host.notifyFontScale(scale). The native host hands the value to the managed callback registered
// through ohos_host_font_scale_set (replaying the last value to a listener that registers after
// the report), and this handler:
//   * publishes the value to OpenHarmonyFontManager.SetSystemFontScale (the one boundary every
//     text path measures and draws through; non-finite/non-positive resets to 1, 0.5..3 clamps),
//     which bumps the font-scale generation so every metrics cache re-measures; and
//   * raises Changed so the app host re-arranges the tree and repaints - the value alone cannot
//     relayout, exactly like a platform configuration change.
//
// Every native call is guarded: without libopenharmonyhost.so (desktop builds) or with an older
// host Register is a no-op, the managed default of 1 stays in place and nothing throws.
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Microsoft.Maui.Platform;

/// <summary>Follows the device's system font-size scale reported by the shell.</summary>
internal static partial class OpenHarmonySystemFontScale
{
    private const string HostLibrary = "libopenharmonyhost.so";

    /// <summary>Registers the managed callback the host invokes for notifyFontScale.</summary>
    [LibraryImport(HostLibrary, EntryPoint = "ohos_host_font_scale_set")]
    private static partial void FontScaleSetListener(IntPtr callback);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void FontScaleCallback(float scale);

    private static unsafe IntPtr s_callback = (IntPtr)(delegate* unmanaged[Cdecl]<float, void>)&OnNativeFontScale;
    private static bool s_registered;
    private static bool s_available = true;

    /// <summary>Raised after a reported scale moved the effective value (the host re-arranges).</summary>
    internal static event Action? Changed;

    /// <summary>Reports that moved the effective scale (a repeated value is a no-op).</summary>
    internal static int AppliedChanges { get; private set; }

    /// <summary>Registers the native callback; a guarded no-op off-device.</summary>
    public static void Register()
    {
        if (s_registered || !s_available)
        {
            return;
        }
        s_registered = true;
        try
        {
            FontScaleSetListener(s_callback);
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

    /// <summary>
    /// The host calls this for every notifyFontScale(scale) from the shell: the value is
    /// published to the font manager (invalid values reset to 1, the range clamps) and a changed
    /// value raises <see cref="Changed"/> so the app host re-arranges and repaints.
    /// </summary>
    internal static void OnPlatformFontScaleChanged(float scale)
    {
        float before = OpenHarmonyFontManager.SystemFontScale;
        OpenHarmonyFontManager.SetSystemFontScale(scale);
        if (Math.Abs(before - OpenHarmonyFontManager.SystemFontScale) < 0.0001f)
        {
            // The same setting reported again (startup replay, a configuration event that did not
            // move the font size): no cache generation change, so no re-arrange is needed.
            return;
        }
        AppliedChanges++;
        Changed?.Invoke();
    }

    /// <summary>Native-shaped thunk: the NAPI export delivers the scale factor.</summary>
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static void OnNativeFontScale(float scale)
    {
        // A reverse P/Invoke entry: an exception must not unwind into the native frame (MB-2).
        try
        {
            OnPlatformFontScaleChanged(scale);
        }
        catch (Exception ex)
        {
            OpenHarmonyStatus.NativeCallbackFailed("font scale change", ex);
        }
    }

    [ModuleInitializer]
    internal static void Initialize() => Register();
}
