// Battery and DeviceDisplay (MAUI Essentials) for OpenHarmony over the established
// host/ArkTS push bridge:
//
//   ArkTS shell (pages/Index.ets) -> batteryInfo.batterySOC/chargingStatus/pluggedType/
//     isBatteryPresent + power.getPowerMode() -> host.notifyBattery("soc\tcharge\tplugged\
//     tpresent\tpowerMode") -> ohos_host_battery_set_listener callback -> the cached snapshot
//     behind Battery.State/ChargeLevel/PowerSource/EnergySaverStatus;
//     display.getDefaultDisplaySync() + display.on('change') -> host.notifyDisplay(
//     "width\theight\tdensityDPI\trotation\trefreshRate\torientation") ->
//     ohos_host_display_set_listener callback -> DeviceDisplay.MainDisplayInfo.
//
//   DeviceDisplay.KeepScreenOn -> P/Invoke ohos_host_keep_screen_on(on) -> the shell's
//     registerKeepScreenOnSink handler -> window.getLastWindow(context) ->
//     setWindowKeepScreenOn(on === 1); the managed getter caches the last value the host
//     accepted (the shell call is asynchronous and does not answer back).
//
// The properties are synchronous, so the shell pushes a snapshot (the host replays the last
// one when the managed listener registers) and later changes through the same notify. The
// shell subscribes to the "usual.event.BATTERY_CHANGED" / CHARGING / DISCHARGING /
// POWER_SAVE_MODE_CHANGED common events for battery changes and to display.on('change') for
// display changes.
//
// Both implementations install themselves as the Essentials defaults through the
// field-reflection InstallDefault pattern (Battery.defaultImplementation /
// DeviceDisplay.currentImplementation) from a [ModuleInitializer], and every native call is
// guarded: off-device (no libopenharmonyhost.so) the extra stays at its documented defaults
// (Unknown/0/empty display) and never throws.
using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Microsoft.Maui.Devices;
using Microsoft.OpenHarmony.Hosting;

namespace Microsoft.Maui.Platform;

/// <summary>
/// One battery snapshot as reported by the shell: the charge level (0..1), the charging state,
/// the power source and the energy-saver state, all mapped to the MAUI Essentials enums.
/// </summary>
public readonly record struct OpenHarmonyBatterySnapshot(
    double ChargeLevel,
    BatteryState State,
    BatteryPowerSource PowerSource,
    EnergySaverStatus EnergySaver);

/// <summary>
/// MAUI Essentials battery backed by the OpenHarmony Basic Services Kit
/// (batteryInfo + power, pushed by the ArkTS shell). Properties return the last snapshot
/// reported by the shell; off-device (no host library) they stay Unknown and never throw.
/// </summary>
public sealed partial class OpenHarmonyBattery : IBattery
{
    private const string HostLibrary = "libopenharmonyhost.so";

    // power.DevicePowerMode values (MODE_CUSTOM_POWER_SAVE = 650 is explicit in the SDK).
    private const int PowerModeNormal = 600;
    private const int PowerModePowerSave = 601;
    private const int PowerModePerformance = 602;
    private const int PowerModeExtremePowerSave = 603;
    private const int PowerModeCustomPowerSave = 650;

    /// <summary>The singleton installed as <see cref="Battery.Default"/>.</summary>
    public static OpenHarmonyBattery Instance { get; } = new();

    private static OpenHarmonyBatterySnapshot s_snapshot =
        new(0, BatteryState.Unknown, BatteryPowerSource.Unknown, EnergySaverStatus.Unknown);
    private static unsafe IntPtr s_batteryCallback = (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, void>)&OnBatteryNative;
    private static bool s_registered;
    private static bool s_unavailable;

    [LibraryImport(HostLibrary, EntryPoint = "ohos_host_battery_set_listener")]
    private static partial void BatterySetListener(IntPtr callback);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void BatteryListener(IntPtr payloadUtf8);

    private OpenHarmonyBattery()
    {
    }

    /// <summary>Battery charge level in the range 0.0 to 1.0 (0 until the shell reports one).</summary>
    public double ChargeLevel => s_snapshot.ChargeLevel;

    /// <summary>Charging state from batteryInfo.chargingStatus (Unknown off-device).</summary>
    public BatteryState State => s_snapshot.State;

    /// <summary>Power source from batteryInfo.pluggedType (Unknown off-device).</summary>
    public BatteryPowerSource PowerSource => s_snapshot.PowerSource;

    /// <summary>Energy-saver state from power.getPowerMode() (Unknown off-device).</summary>
    public EnergySaverStatus EnergySaverStatus => s_snapshot.EnergySaver;

    public event EventHandler<BatteryInfoChangedEventArgs>? BatteryInfoChanged;
    public event EventHandler<EnergySaverStatusChangedEventArgs>? EnergySaverStatusChanged;

    /// <summary>
    /// Parses the shell payload "soc\tchargeState\tpluggedType\tpresent\tpowerMode" into the
    /// MAUI enums; null when the payload is missing or malformed. soc is 0..100.
    /// </summary>
    public static OpenHarmonyBatterySnapshot? ParseState(string? payload)
    {
        if (string.IsNullOrEmpty(payload))
        {
            return null;
        }
        string[] fields = payload.Split('\t');
        if (fields.Length < 5 ||
            !int.TryParse(fields[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int soc) ||
            !int.TryParse(fields[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int chargeState) ||
            !int.TryParse(fields[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out int pluggedType) ||
            !int.TryParse(fields[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out int present) ||
            !int.TryParse(fields[4], NumberStyles.Integer, CultureInfo.InvariantCulture, out int powerMode))
        {
            return null;
        }
        double chargeLevel = Math.Clamp(soc / 100.0, 0.0, 1.0);
        BatteryState state = present == 0
            ? BatteryState.NotPresent
            : chargeState switch
            {
                1 => BatteryState.Charging,     // batteryInfo.BatteryChargeState.ENABLE
                2 => BatteryState.Discharging,  // batteryInfo.BatteryChargeState.DISABLE
                3 => BatteryState.Full,         // batteryInfo.BatteryChargeState.FULL
                _ => BatteryState.Unknown,
            };
        BatteryPowerSource source = pluggedType switch
        {
            0 => BatteryPowerSource.Battery,   // batteryInfo.BatteryPluggedType.NONE
            1 => BatteryPowerSource.AC,        // batteryInfo.BatteryPluggedType.AC
            2 => BatteryPowerSource.Usb,       // batteryInfo.BatteryPluggedType.USB
            3 => BatteryPowerSource.Wireless,  // batteryInfo.BatteryPluggedType.WIRELESS
            _ => BatteryPowerSource.Unknown,
        };
        EnergySaverStatus saver = powerMode switch
        {
            PowerModePowerSave or PowerModeExtremePowerSave or PowerModeCustomPowerSave => EnergySaverStatus.On,
            PowerModeNormal or PowerModePerformance => EnergySaverStatus.Off,
            _ => EnergySaverStatus.Unknown,
        };
        return new OpenHarmonyBatterySnapshot(chargeLevel, state, source, saver);
    }

    /// <summary>
    /// Native-shaped entry point for the host's battery notify (harness-testable): applies the
    /// snapshot and raises the changed events. A malformed payload is ignored.
    /// </summary>
    internal static void OnBatteryPayload(string? payload)
    {
        if (ParseState(payload) is not { } parsed)
        {
            return;
        }
        OpenHarmonyBatterySnapshot previous = s_snapshot;
        s_snapshot = parsed;
        if (previous.ChargeLevel != parsed.ChargeLevel ||
            previous.State != parsed.State ||
            previous.PowerSource != parsed.PowerSource)
        {
            Instance.BatteryInfoChanged?.Invoke(
                null, new BatteryInfoChangedEventArgs(parsed.ChargeLevel, parsed.State, parsed.PowerSource));
        }
        if (previous.EnergySaver != parsed.EnergySaver)
        {
            Instance.EnergySaverStatusChanged?.Invoke(
                null, new EnergySaverStatusChangedEventArgs(parsed.EnergySaver));
        }
    }

    // A reverse P/Invoke entry: applying the snapshot raises BatteryInfoChanged /
    // EnergySaverStatusChanged, so an exception must not unwind into the native frame (MB-2).
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static void OnBatteryNative(IntPtr payloadUtf8)
    {
        try
        {
            string payload = payloadUtf8 == IntPtr.Zero
                ? string.Empty
                : Marshal.PtrToStringUTF8(payloadUtf8) ?? string.Empty;
            OnBatteryPayload(payload);
        }
        catch (Exception ex)
        {
            OpenHarmonyStatus.NativeCallbackFailed("battery", ex);
        }
    }

    /// <summary>Registers the managed battery callback with the host; guarded no-op off-device.</summary>
    public static void Register()
    {
        if (s_registered || s_unavailable)
        {
            return;
        }
        try
        {
            BatterySetListener(s_batteryCallback);
            s_registered = true;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            s_unavailable = true;
            OpenHarmonyBridge.WriteStatus("[maui] battery bridge unavailable (no host library)");
        }
    }

    /// <summary>Installs this implementation as the MAUI Essentials <see cref="Battery"/> default.</summary>
    public static void InstallDefault()
    {
        try
        {
            const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
            foreach (FieldInfo field in typeof(Battery).GetFields(flags))
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
    internal static void Initialize()
    {
        InstallDefault();
        Register();
    }
}

/// <summary>
/// MAUI Essentials device display backed by @ohos.display, pushed by the ArkTS shell
/// (display.getDefaultDisplaySync() at startup and display.on('change') afterwards).
/// KeepScreenOn asks the shell for window.setWindowKeepScreenOn; off-device (no host library)
/// <see cref="MainDisplayInfo"/> stays empty, KeepScreenOn stays false and nothing throws.
/// </summary>
public sealed partial class OpenHarmonyDeviceDisplay : IDeviceDisplay
{
    private const string HostLibrary = "libopenharmonyhost.so";

    /// <summary>The singleton installed as <see cref="DeviceDisplay.Current"/>.</summary>
    public static OpenHarmonyDeviceDisplay Instance { get; } = new();

    private static DisplayInfo s_info =
        new(0, 0, 1, DisplayOrientation.Unknown, DisplayRotation.Unknown, 0);
    private static unsafe IntPtr s_displayCallback = (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, void>)&OnDisplayNative;
    private static bool s_registered;
    private static bool s_unavailable;
    // KeepScreenOn: the last value the host accepted (queued through the shell sink). Off-device
    // there is no host library and the value stays false.
    private static bool s_keepScreenOn;
    private static bool s_keepScreenOnUnavailable;

    [LibraryImport(HostLibrary, EntryPoint = "ohos_host_display_set_listener")]
    private static partial void DisplaySetListener(IntPtr callback);

    [LibraryImport(HostLibrary, EntryPoint = "ohos_host_keep_screen_on")]
    private static partial int KeepScreenOnSet(int on);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void DisplayListener(IntPtr payloadUtf8);

    private OpenHarmonyDeviceDisplay()
    {
    }

    /// <summary>
    /// The last display snapshot reported by the shell (0x0/Unknown until one arrives). Width and
    /// height are in pixels and <see cref="DisplayInfo.Density"/> is densityDPI / 160, so
    /// Width/Density is the size in logical units.
    /// </summary>
    public DisplayInfo MainDisplayInfo => s_info;

    /// <summary>
    /// Whether the screen should stay on. The setter asks the ArkTS shell through
    /// ohos_host_keep_screen_on (the shell applies window.setWindowKeepScreenOn to the last
    /// window) and the getter reflects the last value the host accepted. Off-device (no host
    /// library) the setter is a silent no-op, so the value stays false.
    /// </summary>
    public bool KeepScreenOn
    {
        get => s_keepScreenOn;
        set
        {
            if (s_keepScreenOnUnavailable)
            {
                return;
            }
            try
            {
                if (KeepScreenOnSet(value ? 1 : 0) == 0)
                {
                    s_keepScreenOn = value;
                }
            }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
            {
                s_keepScreenOnUnavailable = true;
                OpenHarmonyBridge.WriteStatus("[maui] keep-screen-on bridge unavailable (no host library)");
            }
        }
    }

    public event EventHandler<DisplayInfoChangedEventArgs>? MainDisplayInfoChanged;

    /// <summary>
    /// Parses the shell payload "width\theight\tdensityDPI\trotation\trefreshRate\torientation"
    /// (width/height in px, rotation/display.Orientation 0..3); null when missing or malformed.
    /// </summary>
    public static DisplayInfo? ParseDisplayInfo(string? payload)
    {
        if (string.IsNullOrEmpty(payload))
        {
            return null;
        }
        string[] fields = payload.Split('\t');
        if (fields.Length < 5 ||
            !double.TryParse(fields[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double width) ||
            !double.TryParse(fields[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double height) ||
            !double.TryParse(fields[2], NumberStyles.Float, CultureInfo.InvariantCulture, out double densityDpi) ||
            !int.TryParse(fields[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out int rotation) ||
            !float.TryParse(fields[4], NumberStyles.Float, CultureInfo.InvariantCulture, out float refreshRate))
        {
            return null;
        }
        int orientation = -1;
        if (fields.Length >= 6)
        {
            int.TryParse(fields[5], NumberStyles.Integer, CultureInfo.InvariantCulture, out orientation);
        }
        double density = densityDpi > 0 ? densityDpi / 160.0 : 1.0;
        DisplayRotation displayRotation = rotation switch
        {
            0 => DisplayRotation.Rotation0,      // display.Orientation.PORTRAIT
            1 => DisplayRotation.Rotation90,     // display.Orientation.LANDSCAPE
            2 => DisplayRotation.Rotation180,    // display.Orientation.PORTRAIT_INVERTED
            3 => DisplayRotation.Rotation270,    // display.Orientation.LANDSCAPE_INVERTED
            _ => DisplayRotation.Unknown,
        };
        DisplayOrientation displayOrientation = orientation switch
        {
            0 or 2 => DisplayOrientation.Portrait,
            1 or 3 => DisplayOrientation.Landscape,
            _ => width >= height ? DisplayOrientation.Landscape : DisplayOrientation.Portrait,
        };
        if (width <= 0 || height <= 0)
        {
            return null;
        }
        return new DisplayInfo(width, height, density, displayOrientation, displayRotation, refreshRate);
    }

    /// <summary>
    /// Native-shaped entry point for the host's display notify (harness-testable): applies the
    /// snapshot and raises <see cref="MainDisplayInfoChanged"/> when it differs from the last one.
    /// A malformed payload is ignored.
    /// </summary>
    internal static void OnDisplayPayload(string? payload)
    {
        if (ParseDisplayInfo(payload) is not { } info || info.Equals(s_info))
        {
            return;
        }
        s_info = info;
        Instance.MainDisplayInfoChanged?.Invoke(null, new DisplayInfoChangedEventArgs(info));
    }

    // A reverse P/Invoke entry: applying the snapshot raises MainDisplayInfoChanged, so an
    // exception must not unwind into the native frame (MB-2).
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static void OnDisplayNative(IntPtr payloadUtf8)
    {
        try
        {
            string payload = payloadUtf8 == IntPtr.Zero
                ? string.Empty
                : Marshal.PtrToStringUTF8(payloadUtf8) ?? string.Empty;
            OnDisplayPayload(payload);
        }
        catch (Exception ex)
        {
            OpenHarmonyStatus.NativeCallbackFailed("display", ex);
        }
    }

    /// <summary>Registers the managed display callback with the host; guarded no-op off-device.</summary>
    public static void Register()
    {
        if (s_registered || s_unavailable)
        {
            return;
        }
        try
        {
            DisplaySetListener(s_displayCallback);
            s_registered = true;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            s_unavailable = true;
            OpenHarmonyBridge.WriteStatus("[maui] display bridge unavailable (no host library)");
        }
    }

    /// <summary>Installs this implementation as the MAUI Essentials <see cref="DeviceDisplay"/> current.</summary>
    public static void InstallDefault()
    {
        try
        {
            const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
            foreach (FieldInfo field in typeof(DeviceDisplay).GetFields(flags))
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
    internal static void Initialize()
    {
        InstallDefault();
        Register();
    }
}
