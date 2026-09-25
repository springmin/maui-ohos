// HMS Kit platform extras (KIT-IMPL 2026-09-25): Share Kit multi-file dispatch and Scan Kit
// default-UI scanning.
//
// Both ride the established host/ArkTS request bridge:
//
//   Share: OpenHarmonyShareKitBridge.TryShare -> ohos_host_share_kit_share(uris, title) -> the
//     ArkTS shell's registerShareKitSink handler builds systemShare.SharedData/ShareController
//     and calls show(); the shell answers synchronously whether the panel was dispatched (the
//     ability-sink shape; MAUI's IShare contract completes at hand-off).
//   Scan: OpenHarmonyScan.ScanAsync -> ohos_host_scan_request(request_id) -> registerScanSink ->
//     scanBarcode.startScanForResult(context) -> host.notifyScanResult(requestId, code, value)
//     -> ohos_host_scan_result -> the registered managed callback completes the awaiting task.
//     OpenHarmonyScan.IsSupported asks ohos_host_scan_available() (never launches the scanner).
//
// The shell registers both sinks only when its runtime provides the kit (the KIT-IMPL
// variable-specifier import() probe in the shell templates), so the default OpenHarmony-shell
// build registers neither: every bridge call answers unavailable and these APIs degrade to
// false/null with a once-per-process status note. They never throw off-device (no host
// library).
//
// Scan result codes (host protocol, mirrored from the shell): 0 the value was scanned
// (Result = result.originalValue), -1 unavailable/failed (the caller flips IsSupported off),
// -2 the user cancelled the scan (Scan Kit error 1000500002; not a failure).
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Microsoft.OpenHarmony.Hosting;

namespace Microsoft.Maui.Platform;

/// <summary>
/// Multi-file share through the Share Kit (systemShare) host bridge. False whenever the shell's
/// Share Kit sink is not registered (the OpenHarmony SDK shell, or an HMS runtime without the
/// kit); <see cref="OpenHarmonyShare"/> then keeps its documented degradation.
/// </summary>
internal static class OpenHarmonyShareKitBridge
{
    private const string HostLibrary = "libopenharmonyhost.so";

    [DllImport(HostLibrary, EntryPoint = "ohos_host_share_kit_share", CharSet = CharSet.Ansi)]
    private static extern int ShareKitShare(string uris, string title);

    private static bool s_unavailable;

    /// <summary>
    /// Asks the shell to show the system share panel for <paramref name="fileUris"/> (file://
    /// URIs; the host joins them into the '\n'-separated protocol payload). Returns whether the
    /// panel was dispatched.
    /// </summary>
    public static bool TryShare(IReadOnlyList<string> fileUris, string? title)
    {
        if (s_unavailable || fileUris.Count == 0)
        {
            return false;
        }
        try
        {
            return ShareKitShare(string.Join('\n', fileUris), title ?? string.Empty) == 0;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            s_unavailable = true;
            return false;
        }
    }
}

/// <summary>
/// Default-UI barcode scan over the Scan Kit host bridge (platform extra; the MAUI Essentials
/// surface has no scanning API). <see cref="IsSupported"/> reports whether the shell registered
/// the sink; <see cref="ScanAsync"/> opens the system scan UI and completes with the decoded
/// value, null on cancel/unavailable. All members degrade without throwing off-device.
/// </summary>
public static class OpenHarmonyScan
{
    private const string HostLibrary = "libopenharmonyhost.so";

    /// <summary>Result codes shared with the host/shell protocol (see the file header).</summary>
    private const int ResultOk = 0;

    /// <summary>The user cancelled the scan (Scan Kit error 1000500002).</summary>
    private const int ResultCancelled = -2;

    /// <summary>Scan UI time budget; a cancelled/dismissed scan answers from the shell, so this
    /// only bounds a lost answer (backgrounded app, killed UI).</summary>
    private static readonly TimeSpan s_timeout = TimeSpan.FromMinutes(2);

    private static readonly ConcurrentDictionary<int, TaskCompletionSource<(int Code, string Value)>> s_pending = new();
    private static ScanResultCallback? s_callback;
    private static int s_nextId;
    private static bool s_registered;
    private static bool s_unavailable;

    [DllImport(HostLibrary, EntryPoint = "ohos_host_scan_available", CharSet = CharSet.Ansi)]
    private static extern int ScanAvailable();

    [DllImport(HostLibrary, EntryPoint = "ohos_host_scan_request", CharSet = CharSet.Ansi)]
    private static extern int ScanRequest(int requestId);

    [DllImport(HostLibrary, EntryPoint = "ohos_host_scan_register_result", CharSet = CharSet.Ansi)]
    private static extern void ScanRegisterResult(IntPtr callback);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void ScanResultCallback(int requestId, int code, IntPtr valueUtf8);

    /// <summary>
    /// True when the host library exports the scan bridge and the shell registered the sink (an
    /// HMS runtime with Scan Kit), and no call has established that the kit is unavailable. The
    /// probe never launches the scanner and answers false off-device instead of throwing.
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
                return ScanAvailable() == 1;
            }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
            {
                s_unavailable = true;
                return false;
            }
        }
    }

    /// <summary>
    /// Opens the system default-UI scanner and completes with the decoded value
    /// (<c>result.originalValue</c>); null when the user cancelled, the Scan Kit is unavailable
    /// or the answer was lost (see the timeout). Never throws for an unavailable platform.
    /// </summary>
    public static async Task<string?> ScanAsync(CancellationToken cancellationToken = default)
    {
        if (s_unavailable)
        {
            return null;
        }
        int requestId = Interlocked.Increment(ref s_nextId);
        var source = new TaskCompletionSource<(int Code, string Value)>(TaskCreationOptions.RunContinuationsAsynchronously);
        s_pending[requestId] = source;
        try
        {
            EnsureRegistered();
            if (s_unavailable || ScanRequest(requestId) != 0)
            {
                s_pending.TryRemove(requestId, out _);
                s_unavailable = true;
                OpenHarmonyBridge.WriteStatus("[maui] scan sink is not available (no Scan Kit on this shell/device)");
                return null;
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            s_pending.TryRemove(requestId, out _);
            s_unavailable = true;
            OpenHarmonyBridge.WriteStatus("[maui] scan bridge unavailable (no host library)");
            return null;
        }
        using CancellationTokenRegistration registration = cancellationToken.CanBeCanceled
            ? cancellationToken.Register(() => source.TrySetCanceled(cancellationToken))
            : default;
        Task completed = await Task.WhenAny(source.Task, Task.Delay(s_timeout, CancellationToken.None)).ConfigureAwait(false);
        if (completed != source.Task)
        {
            s_pending.TryRemove(requestId, out _);
            OpenHarmonyBridge.WriteStatus("[maui] scan request timed out");
            return null;
        }
        try
        {
            (int code, string value) = await source.Task.ConfigureAwait(false);
            if (code == ResultOk)
            {
                return value;
            }
            if (code != ResultCancelled)
            {
                // A real failure (kit/service error): mirror the contacts convention and keep
                // answering null without retrying a broken platform path.
                s_unavailable = true;
                OpenHarmonyBridge.WriteStatus($"[maui] scan request failed (code {code})");
            }
            return null;
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
            s_callback = OnResultNative;
            ScanRegisterResult(Marshal.GetFunctionPointerForDelegate(s_callback));
            s_registered = true;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            s_unavailable = true;
            OpenHarmonyBridge.WriteStatus("[maui] scan bridge unavailable (no host library)");
        }
    }

    private static void OnResultNative(int requestId, int code, IntPtr valueUtf8)
    {
        if (!s_pending.TryRemove(requestId, out TaskCompletionSource<(int Code, string Value)>? source))
        {
            return;
        }
        string value = valueUtf8 != IntPtr.Zero
            ? Marshal.PtrToStringAnsi(valueUtf8) ?? string.Empty
            : string.Empty;
        source.TrySetResult((code, value));
    }
}
