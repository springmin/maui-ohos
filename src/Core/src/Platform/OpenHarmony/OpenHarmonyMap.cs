// HMS Map Kit platform extra (KIT-EXT2 2026-09-25, overlay R2-3 2026-09-26): Map Kit capability
// discovery plus the MapComponent overlay bridge.
//
// Map Kit's map view is the ArkUI MapComponent component, not a plain module API: rendering one
// needs the component declaration at compile time, which the OpenHarmony SDK does not ship
// (a literal import('@kit.MapKit') is a hard ArkTS compile error there, 10505001). The two
// documented options are therefore both landed:
//
//   (b) capability discovery: the shell probes @kit.MapKit at runtime (variable specifier + local
//       structural interface cast) and registers a capability sink; QueryCapabilitiesAsync asks
//       over the host bridge. IsSupported is false unless the runtime resolves the kit.
//   (a) MapComponent overlay (this revision): the shell template set carries a harmony-flavor
//       module (templates/ets/map/MapOverlay.ets) that renders MapComponent in the page Stack;
//       the shell imports it dynamically and reports bit 1 only when it resolved, so the overlay
//       is created/shown/hidden/destroyed by this class's ShowAsync/HideAsync/CloseAsync and its
//       region/marker calls. It needs the ARKTS_SDK_FLAVOR=harmony shell build (the component
//       declaration lives in the DevEco SDK's hms/ets) and the AGC map service AppKey.
//
//   OpenHarmonyMap.QueryCapabilitiesAsync -> ohos_host_map_command(id, 0, "") -> the shell's
//     registerMapSink handler answers host.notifyMapResult(id, 0, code, flags) ->
//     ohos_host_map_result -> the registered managed callback.
//   ShowAsync/HideAsync/CloseAsync/SetRegionAsync/AddMarkerAsync send ops 1..6 the same way
//     (create/destroy/show/hide/set region/add marker) and complete when the shell answers.
//   Overlay events arrive with request id 0 (1 ready, 2 marker click, 3 camera idle) and are
//     raised as Ready/MarkerClick/CameraIdle.
//   OpenHarmonyMap.IsSupported asks ohos_host_map_available() (never probes the kit);
//     IsOverlayAvailable additionally requires the last capability answer's overlay bit.
//
// The shell registers the sink only when its runtime provides @kit.MapKit, so the default
// OpenHarmony-shell build registers nothing: QueryCapabilitiesAsync answers null, IsSupported and
// IsOverlayAvailable are false, every overlay call returns false and the events never fire. Never
// throws off-device.
using System.Collections.Concurrent;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
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
    /// The shell can render a MapComponent overlay (its harmony-flavor overlay module resolved, so
    /// ShowAsync/HideAsync/SetRegionAsync/AddMarkerAsync are usable). 0 on a shell built against
    /// the OpenHarmony SDK.
    /// </summary>
    MapComponentOverlay = 2,
}

/// <summary>One map camera region: the center point and the zoom level (2-20).</summary>
/// <param name="Latitude">Center latitude in degrees (WGS84/GCJ02 as the kit expects).</param>
/// <param name="Longitude">Center longitude in degrees.</param>
/// <param name="Zoom">Zoom level, 2-20.</param>
public readonly record struct OpenHarmonyMapRegion(double Latitude, double Longitude, double Zoom);

/// <summary>One map marker: the app's id, its position and an optional info-window title.</summary>
/// <param name="Id">App-defined identifier echoed back by <see cref="OpenHarmonyMap.MarkerClick"/>.</param>
/// <param name="Latitude">Marker latitude in degrees.</param>
/// <param name="Longitude">Marker longitude in degrees.</param>
/// <param name="Title">Info-window title; an empty string shows a marker without text.</param>
public sealed record OpenHarmonyMapMarker(string Id, double Latitude, double Longitude, string Title);

/// <summary>Event data for <see cref="OpenHarmonyMap.MarkerClick"/>.</summary>
public sealed class OpenHarmonyMapMarkerClickEventArgs : EventArgs
{
    internal OpenHarmonyMapMarkerClickEventArgs(string markerId) => MarkerId = markerId;

    /// <summary>The <see cref="OpenHarmonyMapMarker.Id"/> of the tapped marker.</summary>
    public string MarkerId { get; }
}

/// <summary>Event data for <see cref="OpenHarmonyMap.CameraIdle"/>.</summary>
public sealed class OpenHarmonyMapRegionEventArgs : EventArgs
{
    internal OpenHarmonyMapRegionEventArgs(OpenHarmonyMapRegion region) => Region = region;

    /// <summary>The camera region reported after the map settled.</summary>
    public OpenHarmonyMapRegion Region { get; }
}

/// <summary>
/// Map Kit capability discovery and MapComponent overlay control over the host bridge. All
/// members degrade without throwing when the host library or the shell's Map Kit sink is absent.
/// Overlay methods first run the capability probe (unless <see cref="QueryCapabilitiesAsync"/>
/// already did) and are no-ops that answer <c>false</c> when the overlay bit is not set.
/// </summary>
public static partial class OpenHarmonyMap
{
    private const string HostLibrary = "libopenharmonyhost.so";

    // Map sink ops (see src/OpenHarmonyHost/openharmony_host.h; the shell answers through
    // host.notifyMapResult). Event ops arrive with request id 0.
    private const int OpProbe = 0;
    private const int OpCreate = 1;
    private const int OpDestroy = 2;
    private const int OpShow = 3;
    private const int OpHide = 4;
    private const int OpSetRegion = 5;
    private const int OpAddMarker = 6;
    private const int EventReady = 1;
    private const int EventMarkerClick = 2;
    private const int EventCameraIdle = 3;

    /// <summary>Time budget for one capability answer or overlay command; a local runtime call.</summary>
    private static readonly TimeSpan s_timeout = TimeSpan.FromSeconds(15);

    private static readonly ConcurrentDictionary<int, TaskCompletionSource<MapCommandResult>> s_pending = new();
    private static unsafe IntPtr s_callback = (IntPtr)(delegate* unmanaged[Cdecl]<int, int, int, IntPtr, void>)&OnResultNative;
    private static int s_nextId;
    private static bool s_registered;
    private static bool s_unavailable;
    // Last capability bits the shell reported; -1 = not probed yet (see EnsureProbedAsync).
    private static int s_capabilities = -1;

    [LibraryImport(HostLibrary, EntryPoint = "ohos_host_map_available", StringMarshalling = StringMarshalling.Utf8)]
    private static partial int MapAvailable();

    [LibraryImport(HostLibrary, EntryPoint = "ohos_host_map_command", StringMarshalling = StringMarshalling.Utf8)]
    private static partial int MapCommand(int requestId, int op, string args);

    [LibraryImport(HostLibrary, EntryPoint = "ohos_host_map_register_result", StringMarshalling = StringMarshalling.Utf8)]
    private static partial void MapRegisterResult(IntPtr callback);

    // Informative declaration of the native callback shape; the pointer is taken from
    // OnResultNative below (the delegate is never instantiated).
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void MapResultCallback(int requestId, int op, int code, [MarshalAs(UnmanagedType.LPUTF8Str)] string payload);

    private readonly record struct MapCommandResult(int Op, int Code, string Payload);

    /// <summary>
    /// Raised when the overlay's map view finished initializing (the MapComponent controller is
    /// available). Senders run on the callback thread; handlers must not throw.
    /// </summary>
    public static event EventHandler? Ready;

    /// <summary>Raised when a marker is tapped; the payload carries the app's marker id.</summary>
    public static event EventHandler<OpenHarmonyMapMarkerClickEventArgs>? MarkerClick;

    /// <summary>Raised after the camera settled, with the region the map now shows.</summary>
    public static event EventHandler<OpenHarmonyMapRegionEventArgs>? CameraIdle;

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
    /// True when the shell both registered the sink and reported the overlay bit (its
    /// harmony-flavor MapComponent module resolved). The answer comes from the last capability
    /// probe, which <see cref="QueryCapabilitiesAsync"/> and the overlay methods run before they
    /// act, so false also means "not probed yet"; off-device it is always false.
    /// </summary>
    public static bool IsOverlayAvailable =>
        IsSupported && (Volatile.Read(ref s_capabilities) & (int)OpenHarmonyMapCapability.MapComponentOverlay) != 0;

    /// <summary>
    /// Asks the shell which Map Kit capabilities it has. Returns null when the platform path is
    /// unavailable (no host library, or the shell did not register the sink because the runtime
    /// has no @kit.MapKit). The answer is cached for <see cref="IsOverlayAvailable"/>. Never
    /// throws for an unavailable platform.
    /// </summary>
    public static async Task<OpenHarmonyMapCapability?> QueryCapabilitiesAsync(CancellationToken cancellationToken = default)
    {
        if (s_unavailable)
        {
            return null;
        }
        (int code, string payload) = await SendAsync(OpProbe, string.Empty, cancellationToken).ConfigureAwait(false);
        if (code != 0 || !int.TryParse(payload, NumberStyles.Integer, CultureInfo.InvariantCulture, out int flags))
        {
            Volatile.Write(ref s_capabilities, 0);
            return null;
        }
        OpenHarmonyMapCapability capabilities = (OpenHarmonyMapCapability)flags;
        Volatile.Write(ref s_capabilities, flags);
        return capabilities;
    }

    /// <summary>
    /// Creates the map overlay if needed and shows it. False when the shell has no overlay
    /// (no Map Kit, or a shell built against the OpenHarmony SDK) or the map view refused.
    /// </summary>
    public static async Task<bool> ShowAsync(CancellationToken cancellationToken = default)
    {
        (int createCode, _) = await SendAsync(OpCreate, string.Empty, cancellationToken).ConfigureAwait(false);
        if (createCode != 0)
        {
            return false;
        }
        (int showCode, _) = await SendAsync(OpShow, string.Empty, cancellationToken).ConfigureAwait(false);
        return showCode == 0;
    }

    /// <summary>Hides the map overlay (an overlay that was never shown answers true: nothing to do).</summary>
    public static async Task<bool> HideAsync(CancellationToken cancellationToken = default)
    {
        if (!await EnsureProbedAsync(cancellationToken).ConfigureAwait(false))
        {
            return false;
        }
        (int code, _) = await SendAsync(OpHide, string.Empty, cancellationToken).ConfigureAwait(false);
        // -2 means "no overlay exists": hiding nothing succeeded.
        return code == 0 || code == -2;
    }

    /// <summary>Destroys the map overlay and releases the MapComponent (idempotent).</summary>
    public static async Task<bool> CloseAsync(CancellationToken cancellationToken = default)
    {
        if (!await EnsureProbedAsync(cancellationToken).ConfigureAwait(false))
        {
            return false;
        }
        (int code, _) = await SendAsync(OpDestroy, string.Empty, cancellationToken).ConfigureAwait(false);
        return code == 0;
    }

    /// <summary>
    /// Moves the camera to <paramref name="region"/> (without animation). False when the overlay
    /// is unavailable, the region is not finite/decimal, or the shell refused the command.
    /// </summary>
    public static async Task<bool> SetRegionAsync(OpenHarmonyMapRegion region, CancellationToken cancellationToken = default)
    {
        if (!IsFiniteRegion(region))
        {
            return false;
        }
        (int code, _) = await SendAsync(OpSetRegion, RegionArgs(region), cancellationToken).ConfigureAwait(false);
        return code == 0;
    }

    /// <summary>
    /// Adds <paramref name="marker"/> to the overlay. False when the overlay is unavailable, the
    /// marker is malformed (empty id, non-finite position) or the shell refused the command; the
    /// kit's own asynchronous rejection only means no marker appears (no throw).
    /// </summary>
    public static async Task<bool> AddMarkerAsync(OpenHarmonyMapMarker marker, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(marker);
        if (marker.Id.Length == 0 || !IsFiniteCoordinate(marker.Latitude, marker.Longitude))
        {
            return false;
        }
        (int code, _) = await SendAsync(OpAddMarker, MarkerArgs(marker), cancellationToken).ConfigureAwait(false);
        return code == 0;
    }

    // Runs the capability probe once (the shell answers the overlay bit), so an overlay call
    // always checks availability first. False without a probe result.
    private static async Task<bool> EnsureProbedAsync(CancellationToken cancellationToken)
    {
        if (s_unavailable)
        {
            return false;
        }
        if (Volatile.Read(ref s_capabilities) < 0)
        {
            await QueryCapabilitiesAsync(cancellationToken).ConfigureAwait(false);
        }
        return Volatile.Read(ref s_capabilities) > 0;
    }

    // Queues one Map sink command and completes with the shell's answer. Every failure path
    // (unregistered sink, missing export, timeout, cancellation) returns code -1 with an empty
    // payload instead of throwing.
    private static async Task<(int Code, string Payload)> SendAsync(int op, string args, CancellationToken cancellationToken)
    {
        if (s_unavailable)
        {
            return (-1, string.Empty);
        }
        if (op != OpProbe && !await EnsureProbedAsync(cancellationToken).ConfigureAwait(false))
        {
            return (-1, string.Empty);
        }
        int requestId = Interlocked.Increment(ref s_nextId);
        var source = new TaskCompletionSource<MapCommandResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        s_pending[requestId] = source;
        try
        {
            EnsureRegistered();
            if (s_unavailable || MapCommand(requestId, op, args) != 0)
            {
                s_pending.TryRemove(requestId, out _);
                if (op == OpProbe)
                {
                    s_unavailable = true;
                    OpenHarmonyBridge.WriteStatus("[maui] map sink is not available (no Map Kit on this shell/device)");
                }
                else
                {
                    OpenHarmonyBridge.WriteStatus($"[maui] map command {op} was not dispatched");
                }
                return (-1, string.Empty);
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            s_pending.TryRemove(requestId, out _);
            s_unavailable = true;
            OpenHarmonyBridge.WriteStatus("[maui] map bridge unavailable (no host library)");
            return (-1, string.Empty);
        }
        using CancellationTokenRegistration registration = cancellationToken.CanBeCanceled
            ? cancellationToken.Register(() => source.TrySetCanceled(cancellationToken))
            : default;
        Task completed = await Task.WhenAny(source.Task, Task.Delay(s_timeout, CancellationToken.None)).ConfigureAwait(false);
        if (completed != source.Task)
        {
            s_pending.TryRemove(requestId, out _);
            OpenHarmonyBridge.WriteStatus($"[maui] map command {op} timed out");
            return (-1, string.Empty);
        }
        try
        {
            MapCommandResult result = await source.Task.ConfigureAwait(false);
            return (result.Code, result.Payload);
        }
        catch (OperationCanceledException)
        {
            return (-1, string.Empty);
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

    // Native callback (request id 0 = an unsolicited overlay event). Must not throw: an exception
    // crossing the native boundary is a fail-fast, so event handlers are isolated.
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static void OnResultNative(int requestId, int op, int code, IntPtr payload)
    {
        string payloadText = payload == IntPtr.Zero ? string.Empty : Marshal.PtrToStringUTF8(payload) ?? string.Empty;
        if (requestId == 0)
        {
            RaiseEvent(op, payloadText);
            return;
        }
        if (s_pending.TryRemove(requestId, out TaskCompletionSource<MapCommandResult>? source))
        {
            source.TrySetResult(new MapCommandResult(op, code, payloadText));
        }
    }

    private static void RaiseEvent(int op, string payload)
    {
        try
        {
            if (op == EventReady)
            {
                Ready?.Invoke(null, EventArgs.Empty);
            }
            else if (op == EventMarkerClick)
            {
                MarkerClick?.Invoke(null, new OpenHarmonyMapMarkerClickEventArgs(payload));
            }
            else if (op == EventCameraIdle && TryParseRegion(payload, out OpenHarmonyMapRegion region))
            {
                CameraIdle?.Invoke(null, new OpenHarmonyMapRegionEventArgs(region));
            }
        }
        catch (Exception ex)
        {
            OpenHarmonyBridge.WriteStatus($"[maui] map event handler threw {ex.GetType().Name}");
        }
    }

    private static bool TryParseRegion(string payload, out OpenHarmonyMapRegion region)
    {
        region = default;
        string[] parts = payload.Split('\t');
        if (parts.Length != 3 ||
            !double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double latitude) ||
            !double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double longitude) ||
            !double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out double zoom))
        {
            return false;
        }
        region = new OpenHarmonyMapRegion(latitude, longitude, zoom);
        return true;
    }

    private static bool IsFiniteRegion(OpenHarmonyMapRegion region) =>
        IsFiniteCoordinate(region.Latitude, region.Longitude) && double.IsFinite(region.Zoom);

    private static bool IsFiniteCoordinate(double latitude, double longitude) =>
        double.IsFinite(latitude) && latitude >= -90.0 && latitude <= 90.0 &&
        double.IsFinite(longitude) && longitude >= -180.0 && longitude <= 180.0;

    private static string RegionArgs(OpenHarmonyMapRegion region)
    {
        StringBuilder builder = new(96);
        builder.Append("{\"latitude\":");
        AppendDouble(builder, region.Latitude);
        builder.Append(",\"longitude\":");
        AppendDouble(builder, region.Longitude);
        builder.Append(",\"zoom\":");
        AppendDouble(builder, region.Zoom);
        builder.Append('}');
        return builder.ToString();
    }

    private static string MarkerArgs(OpenHarmonyMapMarker marker)
    {
        StringBuilder builder = new(128);
        builder.Append("{\"id\":\"");
        AppendEscaped(builder, marker.Id);
        builder.Append("\",\"latitude\":");
        AppendDouble(builder, marker.Latitude);
        builder.Append(",\"longitude\":");
        AppendDouble(builder, marker.Longitude);
        builder.Append(",\"title\":\"");
        AppendEscaped(builder, marker.Title);
        builder.Append("\"}");
        return builder.ToString();
    }

    // Shortest round-trippable form, invariant culture: the ArkTS JSON.parse on the shell side
    // must see a JSON number, never a locale-formatted string.
    private static void AppendDouble(StringBuilder builder, double value) =>
        builder.Append(value.ToString("R", CultureInfo.InvariantCulture));

    // Minimal JSON string escaping for the id/title fields ('"', '\', control characters). The
    // payload is built by hand so the slice stays trim/AOT friendly (no reflection serializer).
    private static void AppendEscaped(StringBuilder builder, string value)
    {
        foreach (char c in value)
        {
            switch (c)
            {
                case '"':
                    builder.Append("\\\"");
                    break;
                case '\\':
                    builder.Append("\\\\");
                    break;
                case '\n':
                    builder.Append("\\n");
                    break;
                case '\r':
                    builder.Append("\\r");
                    break;
                case '\t':
                    builder.Append("\\t");
                    break;
                default:
                    if (c < ' ')
                    {
                        builder.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                    }
                    else
                    {
                        builder.Append(c);
                    }
                    break;
            }
        }
    }
}
