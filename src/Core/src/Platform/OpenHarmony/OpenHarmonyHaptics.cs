
// Haptic feedback for OpenHarmony through the host's existing NDK vibration export
// (ohos_host_vibrate -> OH_Vibrator_PlayVibration), so no new host entry point is needed and
// the whole implementation stays managed.
//
// The export takes a duration only (the SDK's Vibrator_Attribute carries a usage, not an
// amplitude), so the two MAUI haptic types are mapped to durations:
//   Click     -> 30 ms short tick (default branch, also used for any future haptic type),
//   LongPress -> 300 ms longer vibration.
// Every native call is guarded: without libopenharmonyhost.so (desktop builds) IsSupported
// reports false and Perform degrades to a no-op - haptics never throw.
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Microsoft.Maui.Devices;
using Microsoft.OpenHarmony.Hosting;

namespace Microsoft.Maui.Platform;

/// <summary>Plays a vibration through the OpenHarmony NDK export and never throws.</summary>
internal static class OpenHarmonyHaptics
{
    private const string HostLibrary = "libopenharmonyhost.so";

    /// <summary>Direct NDK call: OH_Vibrator_PlayVibration(durationMs, default attribute).</summary>
    [DllImport(HostLibrary, EntryPoint = "ohos_host_vibrate")]
    private static extern int VibrateNative(int durationMs);

    private static bool s_available = true;

    /// <summary>
    /// True when the host library is usable and the VIBRATE permission was granted. Without the
    /// native library the permission probe answers false (it is guarded), which is what makes
    /// desktop builds report no haptic support.
    /// </summary>
    public static bool IsSupported
        => s_available && OpenHarmonyBridge.CheckSelfPermission("ohos.permission.VIBRATE");

    /// <summary>Plays a vibration; false when the host library, export or permission is missing.</summary>
    public static bool Play(int durationMs)
    {
        if (!s_available)
        {
            return false;
        }
        try
        {
            return VibrateNative(durationMs) == 0;
        }
        catch (DllNotFoundException)
        {
            s_available = false;
            return false;
        }
        catch (EntryPointNotFoundException)
        {
            s_available = false;
            return false;
        }
    }
}

/// <summary>MAUI Essentials haptic feedback backed by the OpenHarmony vibrator NDK export.</summary>
public sealed class OpenHarmonyHapticFeedback : IHapticFeedback
{
    public static readonly OpenHarmonyHapticFeedback Instance = new();

    /// <summary>Click: a short 30 ms tick (short/small feedback).</summary>
    internal const int ClickDurationMs = 30;

    /// <summary>LongPress: a longer 300 ms vibration.</summary>
    internal const int LongPressDurationMs = 300;

    public bool IsSupported => OpenHarmonyHaptics.IsSupported;

    /// <summary>
    /// Plays the platform vibration for <paramref name="type"/>: Click is a 30 ms tick,
    /// LongPress a 300 ms vibration (the export has no amplitude parameter).
    /// </summary>
    public void Perform(HapticFeedbackType type)
    {
        int duration = type == HapticFeedbackType.LongPress ? LongPressDurationMs : ClickDurationMs;
        OpenHarmonyHaptics.Play(duration);
    }

    /// <summary>Installs this implementation as the MAUI Essentials HapticFeedback default.</summary>
    public static void InstallDefault()
    {
        try
        {
            const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
            // The entry point is get-only, so the backing field is the settable surface
            // (HapticFeedback.defaultImplementation), the same pattern as the sensors,
            // TextToSpeech and launcher installers.
            foreach (FieldInfo field in typeof(HapticFeedback).GetFields(flags))
            {
                if (field.FieldType.IsInstanceOfType(Instance))
                {
                    field.SetValue(null, Instance);
                }
            }
        }
        catch (Exception)
        {
            // The default stays in place when the entry point cannot be replaced.
        }
    }

    [ModuleInitializer]
    internal static void Initialize() => InstallDefault();
}
