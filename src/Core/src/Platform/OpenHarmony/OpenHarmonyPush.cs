// HMS Push Kit platform extra (KIT-EXT2 2026-09-25): the Push Token bridge.
//
//   OpenHarmonyPush.GetTokenAsync -> ohos_host_push_request(id, 0) -> the ArkTS shell's
//     registerPushSink handler calls pushService.getToken() -> host.notifyPushResult(id, 0, rc,
//     token) -> ohos_host_push_result -> the registered managed callback completes the awaiting
//     task.
//   OpenHarmonyPush.DeleteTokenAsync -> ohos_host_push_request(id, 1) -> pushService.deleteToken()
//     -> host.notifyPushResult(id, 1, rc, '') -> the same completion path.
//   OpenHarmonyPush.IsSupported asks ohos_host_push_available() (never requests a token).
//
// The shell registers the sink only when its runtime provides @kit.PushKit (the KIT-EXT2
// variable-specifier import() probe in the shell templates), so the default OpenHarmony-shell
// build registers nothing: every call answers unavailable and these APIs degrade to
// Unavailable/null with a once-per-process status note. They never throw off-device (no host
// library).
//
// Status mapping: non-negative values are the Push Kit BusinessError codes passed through
// unchanged, so the documented ones (1000900010 AGC configuration/signature, 1000900012 the
// push entitlement is not enabled, ...) stay distinguishable from the local -1/-2/-3 outcomes.
// Only Unavailable flips the bridge off for the rest of the process: an AGC/entitlement error is
// a configuration problem the caller may retry after fixing it.
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Microsoft.OpenHarmony.Hosting;
using System.Runtime.CompilerServices;

namespace Microsoft.Maui.Platform;

/// <summary>
/// The outcome of an <see cref="OpenHarmonyPush"/> call. Non-negative values are the Push Kit
/// BusinessError codes, passed through unchanged so a caller can branch on the documented ones
/// (see <see cref="OpenHarmonyPushStatus.AppAuthFailed"/>, <see cref="OpenHarmonyPushStatus.ServiceNotEnabled"/>).
/// </summary>
public enum OpenHarmonyPushStatus
{
    /// <summary>The call completed; <see cref="OpenHarmonyPushToken.Token"/> carries the token.</summary>
    Success = 0,

    /// <summary>No host library, or the shell did not register the Push Kit sink.</summary>
    Unavailable = -1,

    /// <summary>The shell did not answer within the bridge's time budget.</summary>
    TimedOut = -2,

    /// <summary>The caller's cancellation token fired.</summary>
    Cancelled = -3,

    /// <summary>Push Kit internal error (1000900001); a retry may succeed.</summary>
    InternalError = 1000900001,

    /// <summary>Connecting to the Push service failed (1000900008); a retry may succeed.</summary>
    ServiceConnectionFailed = 1000900008,

    /// <summary>Push service internal error (1000900009); a retry may succeed.</summary>
    PushServiceError = 1000900009,

    /// <summary>
    /// APP identity verification failed (1000900010): the AGC push service is not enabled for
    /// the app, or the signing profile/certificate does not match the AGC registration.
    /// </summary>
    AppAuthFailed = 1000900010,

    /// <summary>The network is unavailable (1000900011).</summary>
    NetworkUnavailable = 1000900011,

    /// <summary>The push service entitlement is not enabled (1000900012): enable it in AppGallery Connect.</summary>
    ServiceNotEnabled = 1000900012,

    /// <summary>The token request is not allowed across regions (1000900013).</summary>
    RegionMismatch = 1000900013,

    /// <summary>The device does not support Push (1000900014).</summary>
    DeviceUnsupported = 1000900014,
}

/// <summary>The Push Token result: <see cref="Status"/> plus the token on success.</summary>
public readonly record struct OpenHarmonyPushToken
{
    internal OpenHarmonyPushToken(OpenHarmonyPushStatus status, string? token)
    {
        Status = status;
        Token = token;
    }

    /// <summary>The outcome; see <see cref="OpenHarmonyPushStatus"/> for the code map.</summary>
    public OpenHarmonyPushStatus Status { get; }

    /// <summary>The Push Token when <see cref="Success"/> is true; null otherwise.</summary>
    public string? Token { get; }

    /// <summary>True when <see cref="Status"/> is <see cref="OpenHarmonyPushStatus.Success"/>.</summary>
    public bool Success => Status == OpenHarmonyPushStatus.Success;
}

/// <summary>
/// Push Kit (pushService) token access over the host bridge. All members degrade without
/// throwing when the host library or the shell's Push Kit sink is absent.
/// </summary>
public static partial class OpenHarmonyPush
{
    private const string HostLibrary = "libopenharmonyhost.so";

    /// <summary>Shell op codes (see the host protocol in openharmony_host.h).</summary>
    private const int OpGetToken = 0;
    private const int OpDeleteToken = 1;

    /// <summary>Time budget for one AGC round trip; the AGC error codes answer well inside it.</summary>
    private static readonly TimeSpan s_timeout = TimeSpan.FromSeconds(60);

    private static readonly ConcurrentDictionary<int, TaskCompletionSource<(int Code, string Token)>> s_pending = new();
    private static unsafe IntPtr s_callback = (IntPtr)(delegate* unmanaged[Cdecl]<int, int, int, IntPtr, void>)&OnResultNative;
    private static int s_nextId;
    private static bool s_registered;
    private static bool s_unavailable;

    [LibraryImport(HostLibrary, EntryPoint = "ohos_host_push_available", StringMarshalling = StringMarshalling.Utf8)]
    private static partial int PushAvailable();

    [LibraryImport(HostLibrary, EntryPoint = "ohos_host_push_request", StringMarshalling = StringMarshalling.Utf8)]
    private static partial int PushRequest(int requestId, int op);

    [LibraryImport(HostLibrary, EntryPoint = "ohos_host_push_register_result", StringMarshalling = StringMarshalling.Utf8)]
    private static partial void PushRegisterResult(IntPtr callback);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void PushResultCallback(int requestId, int op, int code, IntPtr tokenUtf8);

    /// <summary>
    /// True when the host library exports the push bridge and the shell registered the sink (an
    /// HMS runtime with Push Kit), and no call has established that the kit is unavailable. The
    /// probe never requests a token and answers false off-device instead of throwing.
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
                return PushAvailable() == 1;
            }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
            {
                s_unavailable = true;
                return false;
            }
        }
    }

    /// <summary>
    /// Requests the Push Token through <c>pushService.getToken()</c>. The token must be uploaded
    /// to the app's server (it identifies this app install to the Huawei Push service). Never
    /// throws when the platform path is unavailable: <see cref="OpenHarmonyPushStatus.Unavailable"/>
    /// (or a kit error code) is returned instead.
    /// </summary>
    public static async Task<OpenHarmonyPushToken> GetTokenAsync(CancellationToken cancellationToken = default)
    {
        (OpenHarmonyPushStatus status, string? token) = await RequestAsync(OpGetToken, cancellationToken).ConfigureAwait(false);
        return new OpenHarmonyPushToken(status, token);
    }

    /// <summary>
    /// Deletes the current Push Token through <c>pushService.deleteToken()</c> (for example on
    /// sign-out). Never throws when the platform path is unavailable.
    /// </summary>
    public static async Task<OpenHarmonyPushStatus> DeleteTokenAsync(CancellationToken cancellationToken = default)
    {
        (OpenHarmonyPushStatus status, _) = await RequestAsync(OpDeleteToken, cancellationToken).ConfigureAwait(false);
        return status;
    }

    private static async Task<(OpenHarmonyPushStatus Status, string? Token)> RequestAsync(int op, CancellationToken cancellationToken)
    {
        if (s_unavailable)
        {
            return (OpenHarmonyPushStatus.Unavailable, null);
        }
        int requestId = Interlocked.Increment(ref s_nextId);
        var source = new TaskCompletionSource<(int Code, string Token)>(TaskCreationOptions.RunContinuationsAsynchronously);
        s_pending[requestId] = source;
        try
        {
            EnsureRegistered();
            if (s_unavailable || PushRequest(requestId, op) != 0)
            {
                s_pending.TryRemove(requestId, out _);
                s_unavailable = true;
                OpenHarmonyBridge.WriteStatus("[maui] push sink is not available (no Push Kit on this shell/device)");
                return (OpenHarmonyPushStatus.Unavailable, null);
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            s_pending.TryRemove(requestId, out _);
            s_unavailable = true;
            OpenHarmonyBridge.WriteStatus("[maui] push bridge unavailable (no host library)");
            return (OpenHarmonyPushStatus.Unavailable, null);
        }
        using CancellationTokenRegistration registration = cancellationToken.CanBeCanceled
            ? cancellationToken.Register(() => source.TrySetCanceled(cancellationToken))
            : default;
        Task completed = await Task.WhenAny(source.Task, Task.Delay(s_timeout, CancellationToken.None)).ConfigureAwait(false);
        if (completed != source.Task)
        {
            s_pending.TryRemove(requestId, out _);
            OpenHarmonyBridge.WriteStatus("[maui] push request timed out");
            return (OpenHarmonyPushStatus.TimedOut, null);
        }
        try
        {
            (int code, string token) = await source.Task.ConfigureAwait(false);
            if (code == (int)OpenHarmonyPushStatus.Success)
            {
                return (OpenHarmonyPushStatus.Success, token);
            }
            if (code < 0)
            {
                // A local unavailability answer: stop retrying a missing platform path.
                s_unavailable = true;
                OpenHarmonyBridge.WriteStatus($"[maui] push request failed (code {code})");
                return (OpenHarmonyPushStatus.Unavailable, null);
            }
            // A Push Kit BusinessError code: pass it through so the caller can map it (AGC
            // configuration 1000900010, entitlement 1000900012, ...) and retry after fixing it.
            OpenHarmonyBridge.WriteStatus($"[maui] push request failed (kit code {code})");
            return ((OpenHarmonyPushStatus)code, null);
        }
        catch (OperationCanceledException)
        {
            return (OpenHarmonyPushStatus.Cancelled, null);
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
            PushRegisterResult(s_callback);
            s_registered = true;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            s_unavailable = true;
            OpenHarmonyBridge.WriteStatus("[maui] push bridge unavailable (no host library)");
        }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static void OnResultNative(int requestId, int op, int code, IntPtr tokenUtf8)
    {
        if (!s_pending.TryRemove(requestId, out TaskCompletionSource<(int Code, string Token)>? source))
        {
            return;
        }
        string token = tokenUtf8 != IntPtr.Zero
            ? Marshal.PtrToStringAnsi(tokenUtf8) ?? string.Empty
            : string.Empty;
        source.TrySetResult((code, token));
    }
}
