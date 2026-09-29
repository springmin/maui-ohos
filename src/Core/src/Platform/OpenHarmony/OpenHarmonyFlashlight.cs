// Flashlight (MAUI Essentials IFlashlight) for OpenHarmony through the ArkTS shell's Camera
// Kit torch bridge:
//
//   managed OpenHarmonyFlashlight -> P/Invoke ohos_host_flashlight_set(on) -> host_napi.cpp
//     direct call into the shell's host.registerFlashlightSink ->
//     camera.getCameraManager(context) (created on first use and cached) ->
//     isTorchSupported() -> setTorchMode(camera.TorchMode.ON / camera.TorchMode.OFF) ->
//     the sink's boolean answer -> ohos_host_flashlight_set returns 0 when it was true,
//     -1 otherwise.
//
// on is the opcode the host and the shell share: 0 = torch off, 1 = torch on, 2 = support
// probe (isTorchSupported only, no torch call). IsSupportedAsync runs the probe, so a device
// without a torch answers false without toggling the LED.
//
// Microsoft.Maui.Devices.IFlashlight in this MAUI band is asynchronous:
// Task<bool> IsSupportedAsync(), Task TurnOnAsync(), Task TurnOffAsync() - the exact members
// were read from the referenced Microsoft.Maui.Essentials assembly with reflection, not
// assumed.
//
// Degradation: every native call is guarded. Off-device (no libopenharmonyhost.so) or with a
// host library that has no flashlight export (DllNotFoundException / EntryPointNotFoundException)
// the bridge reports unavailable once and IsSupportedAsync answers false; TurnOnAsync and
// TurnOffAsync log a status line and complete as no-ops. A shell without the sink, a missing
// camera, a device without a torch or a kit error (BusinessError 7400102 operation not
// allowed / 7400201 camera service fatal) all answer false through the same path, so this API
// never throws - unlike the reference platform implementations, which throw
// FeatureNotSupportedException from TurnOn/TurnOffAsync. Unverifiable off-device: whether the
// camera service accepts setTorchMode from the host callback thread and whether the LED
// actually lights - a true result means the kit accepted the request.
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Microsoft.Maui.Devices;
using Microsoft.OpenHarmony.Hosting;

namespace Microsoft.Maui.Platform;

/// <summary>Torch control through the host's flashlight sink; never throws.</summary>
internal static partial class OpenHarmonyFlashlightBridge
{
    private const string HostLibrary = "libopenharmonyhost.so";

    /// <summary>Torch off opcode.</summary>
    internal const int OffOp = 0;

    /// <summary>Torch on opcode.</summary>
    internal const int OnOp = 1;

    /// <summary>Support probe opcode: the shell runs isTorchSupported only.</summary>
    internal const int ProbeOp = 2;

    [LibraryImport(HostLibrary, EntryPoint = "ohos_host_flashlight_set")]
    private static partial int FlashlightSet(int on);

    private static bool s_available = true;

    /// <summary>
    /// True when the shell's Camera Kit path reports a camera torch (probe op, no torch call).
    /// False without the host library or export and when the shell has no sink registered yet;
    /// only the missing library/export is remembered, so a sink that registers later is still
    /// seen.
    /// </summary>
    public static bool IsSupported() => Set(ProbeOp);

    /// <summary>Turns the torch on; false when the platform path is unavailable.</summary>
    public static bool TurnOn() => Set(OnOp);

    /// <summary>Turns the torch off; false when the platform path is unavailable.</summary>
    public static bool TurnOff() => Set(OffOp);

    private static bool Set(int on)
    {
        if (!s_available)
        {
            return false;
        }
        try
        {
            return FlashlightSet(on) == 0;
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

/// <summary>
/// MAUI Essentials flashlight backed by the OpenHarmony Camera Kit torch control (the host
/// asks the ArkTS shell, which owns getCameraManager/isTorchSupported/setTorchMode).
/// </summary>
public sealed class OpenHarmonyFlashlight : IFlashlight
{
    /// <summary>The singleton installed as <see cref="Flashlight.Default"/>.</summary>
    public static readonly OpenHarmonyFlashlight Instance = new();

    private OpenHarmonyFlashlight()
    {
    }

    /// <summary>True when the device has a camera torch; false when the platform is unavailable.</summary>
    public Task<bool> IsSupportedAsync() => Task.FromResult(OpenHarmonyFlashlightBridge.IsSupported());

    /// <summary>Turns the torch on; a logged no-op when the platform path is unavailable.</summary>
    public Task TurnOnAsync()
    {
        Set(true);
        return Task.CompletedTask;
    }

    /// <summary>Turns the torch off; a logged no-op when the platform path is unavailable.</summary>
    public Task TurnOffAsync()
    {
        Set(false);
        return Task.CompletedTask;
    }

    private static void Set(bool on)
    {
        bool succeeded = on
            ? OpenHarmonyFlashlightBridge.TurnOn()
            : OpenHarmonyFlashlightBridge.TurnOff();
        if (!succeeded)
        {
            OpenHarmonyBridge.WriteStatus(on
                ? "[maui] flashlight turn-on unavailable (no host/sink, no torch or kit error)"
                : "[maui] flashlight turn-off unavailable (no host/sink, no torch or kit error)");
        }
    }

    /// <summary>Installs this implementation as the MAUI Essentials Flashlight default.</summary>
    public static void InstallDefault()
    {
        try
        {
            const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
            // The entry point (Flashlight.Default) is get-only, so the backing field is the
            // settable surface (Flashlight.defaultImplementation), the same pattern as the
            // haptics/battery/TextToSpeech/sensors installers.
            foreach (FieldInfo field in typeof(Flashlight).GetFields(flags))
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
