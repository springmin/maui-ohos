// System "reduce animations" (accessibility) for the self-drawn compositor.
//
// The ArkTS shell reads accessibility.isAnimationReduceEnabledSync() at page start and follows
// the setting with accessibility.onAnimationReduceStateChange; every change is forwarded with
// host.notifyAnimationReduce(isReduce). The native host hands it to the managed callback
// registered through ohos_host_animation_reduce_set, and the handler flips ReduceMotion:
//   * OpenHarmonyTicker.SystemEnabled reads the flag, so MAUI's AnimationManager stops accepting
//     animations and force-finishes the running ones on the ticker's next fire (the manager
//     re-reads ITicker.SystemEnabled on every OnFire);
//   * the page and shared-element transitions bail out of their enter pass (navigation commits
//     instantly);
//   * the control-state progress animations (press/switch/check) snap to their target.
//
// Every native call is guarded: without libopenharmonyhost.so (desktop builds) or with an older
// host the Register is a no-op, ReduceMotion stays false and nothing throws.
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Microsoft.OpenHarmony.Hosting;

namespace Microsoft.Maui.Platform;

/// <summary>Follows the device's "reduce animations" accessibility setting.</summary>
internal static partial class OpenHarmonyMotion
{
    private const string HostLibrary = "libopenharmonyhost.so";

    /// <summary>Registers the managed callback the host invokes for notifyAnimationReduce.</summary>
    [LibraryImport(HostLibrary, EntryPoint = "ohos_host_animation_reduce_set")]
    private static partial void AnimationReduceSetListener(IntPtr callback);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void AnimationReduceCallback(int isReduce);

    private static unsafe IntPtr s_callback = (IntPtr)(delegate* unmanaged[Cdecl]<int, void>)&OnNativeAnimationReduce;
    private static bool s_registered;
    private static bool s_available = true;
    private static volatile bool s_reduceMotion;

    /// <summary>True while the device asks apps to reduce animations (default false).</summary>
    internal static bool ReduceMotion => s_reduceMotion;

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
            AnimationReduceSetListener(s_callback);
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

    /// <summary>The host calls this for every notifyAnimationReduce(isReduce) from the shell.</summary>
    internal static void OnPlatformReduceMotionChanged(bool reduceMotion)
    {
        s_reduceMotion = reduceMotion;
        // A transition already in flight sees the flag on its next frame and commits itself; the
        // redraw makes sure that frame is painted even when the ticker was the only producer.
        try
        {
            OpenHarmonyBridge.RequestRedraw();
        }
        catch (Exception)
        {
            // No host: the transition still commits on its next step.
        }
    }

    /// <summary>Native-shaped thunk: the NAPI export delivers 0/1.</summary>
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static void OnNativeAnimationReduce(int isReduce)
    {
        // A reverse P/Invoke entry: an exception must not unwind into the native frame (MB-2).
        try
        {
            OnPlatformReduceMotionChanged(isReduce != 0);
        }
        catch (Exception ex)
        {
            OpenHarmonyStatus.NativeCallbackFailed("animation reduce change", ex);
        }
    }

    [ModuleInitializer]
    internal static void Initialize() => Register();
}
