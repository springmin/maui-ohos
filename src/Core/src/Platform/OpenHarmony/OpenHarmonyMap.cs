// HMS Map Kit platform extra (KIT-EXT2 2026-09-25, reserved sink): map capability discovery.
//
// Map Kit's map view is the ArkUI MapComponent component, not a plain module API: rendering one
// needs the component declaration at compile time, which the OpenHarmony SDK does not ship
// (a literal import('@kit.MapKit') is a hard ArkTS compile error there, 10505001). This slice
// therefore lands the two documented options in stages:
//
//   (b) capability discovery (this file): the shell probes @kit.MapKit at runtime (variable
//       specifier + local structural interface cast) and registers a capability sink; the
//       managed side asks over the host bridge. This is what makes the runtime dependency
//       honest today: QueryCapabilitiesAsync returns MapKitImportable only on a runtime that
//       resolves the kit, and MapComponentOverlay only when a shell actually implements the
//       overlay (bit 1 is 0 in the current templates).
//   (a) shell-side overlay (documented follow-up): a MapComponent rendered inside the page
//       Stack, created/shown/hidden/destroyed by managed calls (the XComponent/ArkWeb pattern),
//       with markers/POI/camera ops forwarded to the MapComponentController. It can only exist
//       in the ARKTS_SDK_FLAVOR=harmony build (the HMS/DevEco SDK has the component
//       declaration) and additionally needs the AGC map service AppKey.
//
//   OpenHarmonyMap.QueryCapabilitiesAsync -> ohos_host_map_probe(id) -> the shell's
//     registerMapSink handler answers host.notifyMapResult(id, flags) -> ohos_host_map_result ->
//     the registered managed callback completes the task.
//   OpenHarmonyMap.IsSupported asks ohos_host_map_available() (never probes the kit).
//
// The shell registers the sink only when its runtime provides @kit.MapKit, so the default
// OpenHarmony-shell build registers nothing: QueryCapabilitiesAsync answers null and
// IsSupported false with a once-per-process status note. Never throws off-device.
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Microsoft.OpenHarmony.Hosting;
using System.Runtime.CompilerServices;

namespace Microsoft.Maui.Platform;

/// <summary>
/// The Map Kit capability bits the shell reports (see <see cref="OpenHarmonyMap.QueryCapabilitiesAsync"/>).
/// </summary>
[Flags]
public enum OpenHarmonyMapCapability
{
    /// <summary>Nothing is available.</summary>
    None = 0,

    /// <summary>The runtime resolved <c>@kit.MapKit</c> (map/mapCommon/MapComponent are present).</summary>
    MapKitImportable = 1,

    /// <summary>
    /// The shell can render a MapComponent overlay (needs the HarmonyOS flavor build; 0 in the
    /// current templates, so map rendering is documented follow-up work).
    /// </summary>
    MapComponentOverlay = 2,
}

/// <summary>
/// Map Kit capability discovery over the host bridge (the reserved sink of the Map slice; the
/// MapComponent overlay is documented follow-up work, see the file header). All members degrade
/// without throwing when the host library or the shell's Map Kit sink is absent.
/// </summary>
public static partial class OpenHarmonyMap
{
    private const string HostLibrary = "libopenharmonyhost.so";

    /// <summary>Time budget for the capability answer; it is a local runtime probe, not a network call.</summary>
    private static readonly TimeSpan s_timeout = TimeSpan.FromSeconds(15);

    private static readonly ConcurrentDictionary<int, TaskCompletionSource<int>> s_pending = new();
    private static unsafe IntPtr s_callback = (IntPtr)(delegate* unmanaged[Cdecl]<int, int, void>)&OnResultNative;
    private static int s_nextId;
    private static bool s_registered;
    private static bool s_unavailable;

    [LibraryImport(HostLibrary, EntryPoint = "ohos_host_map_available", StringMarshalling = StringMarshalling.Utf8)]
    private static partial int MapAvailable();

    [LibraryImport(HostLibrary, EntryPoint = "ohos_host_map_probe", StringMarshalling = StringMarshalling.Utf8)]
    private static partial int MapProbe(int requestId);

    [LibraryImport(HostLibrary, EntryPoint = "ohos_host_map_register_result", StringMarshalling = StringMarshalling.Utf8)]
    private static partial void MapRegisterResult(IntPtr callback);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void MapResultCallback(int requestId, int flags);

    /// <summary>
    /// True when the host library exports the map bridge and the shell registered the capability
    /// sink (a runtime whose @kit.MapKit import resolved), and no call has established that the
    /// kit is unavailable. The probe never creates a map and answers false off-device instead of
    /// throwing.
    /// </summary>
    public static bool IsSupported
    {
        get
        {
            if (s_unavailable)
            {
                return false;
            }
            try
            {
                return MapAvailable() == 1;
            }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
            {
                s_unavailable = true;
                return false;
            }
        }
    }

    /// <summary>
    /// Asks the shell which Map Kit capabilities it has. Returns null when the platform path is
    /// unavailable (no host library, or the shell did not register the sink because the runtime
    /// has no @kit.MapKit). Never throws for an unavailable platform.
    /// </summary>
    public static async Task<OpenHarmonyMapCapability?> QueryCapabilitiesAsync(CancellationToken cancellationToken = default)
    {
        if (s_unavailable)
        {
            return null;
        }
        int requestId = Interlocked.Increment(ref s_nextId);
        var source = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        s_pending[requestId] = source;
        try
        {
            EnsureRegistered();
            if (s_unavailable || MapProbe(requestId) != 0)
            {
                s_pending.TryRemove(requestId, out _);
                s_unavailable = true;
                OpenHarmonyBridge.WriteStatus("[maui] map sink is not available (no Map Kit on this shell/device)");
                return null;
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            s_pending.TryRemove(requestId, out _);
            s_unavailable = true;
            OpenHarmonyBridge.WriteStatus("[maui] map bridge unavailable (no host library)");
            return null;
        }
        using CancellationTokenRegistration registration = cancellationToken.CanBeCanceled
            ? cancellationToken.Register(() => source.TrySetCanceled(cancellationToken))
            : default;
        Task completed = await Task.WhenAny(source.Task, Task.Delay(s_timeout, CancellationToken.None)).ConfigureAwait(false);
        if (completed != source.Task)
        {
            s_pending.TryRemove(requestId, out _);
            OpenHarmonyBridge.WriteStatus("[maui] map capability probe timed out");
            return null;
        }
        try
        {
            int flags = await source.Task.ConfigureAwait(false);
            return (OpenHarmonyMapCapability)flags;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
    }

    private static void EnsureRegistered()
    {
        if (s_registered || s_unavailable)
        {
            return;
        }
        try
        {
            MapRegisterResult(s_callback);
            s_registered = true;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            s_unavailable = true;
            OpenHarmonyBridge.WriteStatus("[maui] map bridge unavailable (no host library)");
        }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static void OnResultNative(int requestId, int flags)
    {
        if (s_pending.TryRemove(requestId, out TaskCompletionSource<int>? source))
        {
            source.TrySetResult(flags);
        }
    }
}
