// HMS Account Kit platform extra (KIT-EXT2 2026-09-25): the Huawei ID authorization bridge.
//
//   OpenHarmonyAccount.GetQuickLoginAnonymousPhoneAsync -> ohos_host_account_request(id, 0, '')
//     -> the ArkTS shell's registerAccountSink handler creates an
//     AuthenticationController(HuaweiIDProvider().createAuthorizationWithHuaweiIDRequest()) with
//     scopes ['quickLoginAnonymousPhone'] and forceAuthorization=false -> the anonymous phone is
//     read from response.data.extraInfo.quickLoginAnonymousPhone and answered through
//     host.notifyAccountResult(id, 0, rc, phone) -> the pending task completes.
//   OpenHarmonyAccount.AuthorizeAsync(scopes) -> ohos_host_account_request(id, 1, scopes) -> the
//     shell authorizes the '\n'-separated scopes with permissions ['serviceauthcode'] and
//     forceAuthorization=true (the system authorization UI) -> the answer payload is
//     response.data.authorizationCode (exchange it server-side for the Access Token / phone
//     number; it is valid for 5 minutes and single-use).
//   OpenHarmonyAccount.IsSupported asks ohos_host_account_available() (never shows UI).
//
// The shell registers the sink only when its runtime provides @kit.AccountKit (the KIT-EXT2
// variable-specifier import() probe in the shell templates), so the default OpenHarmony-shell
// build registers nothing: every call answers unavailable and these APIs degrade to
// Unavailable/null with a once-per-process status note. They never throw off-device (no host
// library).
//
// Status mapping: non-negative values are the Account Kit BusinessError codes passed through
// unchanged. The documented ones the caller is expected to branch on:
//   1001502014 the quick-login scope is not approved in AGC (apply for the Huawei ID one-tap
//     login permission first),
//   1001500001 the signing fingerprint does not match the AGC registration,
//   1001502001 the Huawei ID is not signed in on the device,
//   1001502012 the user cancelled the authorization (treat as Cancelled, not a failure),
//   1001500003 the scope is unsupported (overseas account/device; offer another sign-in path),
//   1001502005 network error, 1001502009 internal error.
// Only Unavailable flips the bridge off for the rest of the process: AGC/scope problems are
// configuration issues the caller may retry after fixing them.
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Microsoft.OpenHarmony.Hosting;
using System.Runtime.CompilerServices;

namespace Microsoft.Maui.Platform;

/// <summary>
/// The outcome of an <see cref="OpenHarmonyAccount"/> call. Non-negative values are the Account
/// Kit BusinessError codes, passed through unchanged (see
/// <see cref="OpenHarmonyAccountStatus.ScopeNotApproved"/>, <see cref="OpenHarmonyAccountStatus.FingerprintMismatch"/>).
/// </summary>
public enum OpenHarmonyAccountStatus
{
    /// <summary>The call completed; <see cref="OpenHarmonyAccountResult.Payload"/> carries the answer.</summary>
    Success = 0,

    /// <summary>No host library, or the shell did not register the Account Kit sink.</summary>
    Unavailable = -1,

    /// <summary>The shell did not answer within the bridge's time budget.</summary>
    TimedOut = -2,

    /// <summary>The caller's cancellation token fired.</summary>
    Cancelled = -3,

    /// <summary>The signing fingerprint does not match the AGC registration (1001500001).</summary>
    FingerprintMismatch = 1001500001,

    /// <summary>A repeated request was refused; the caller should ignore it (1001500002).</summary>
    RepeatedRequest = 1001500002,

    /// <summary>The account/device does not support the requested scope, e.g. an overseas account (1001500003).</summary>
    ScopeUnsupported = 1001500003,

    /// <summary>No Huawei ID is signed in on the device (1001502001).</summary>
    NotLoggedIn = 1001502001,

    /// <summary>Invalid request parameter, e.g. a wrong clientId/profile (1001502003).</summary>
    InvalidParameter = 1001502003,

    /// <summary>Network error (1001502005).</summary>
    NetworkError = 1001502005,

    /// <summary>Account Kit internal error (1001502009).</summary>
    InternalError = 1001502009,

    /// <summary>The user cancelled the authorization UI (1001502012); not a failure.</summary>
    UserCancelled = 1001502012,

    /// <summary>
    /// The app has not applied for (or been approved for) the requested scope (1001502014), e.g.
    /// the Huawei ID one-tap login permission for quickLoginAnonymousPhone.
    /// </summary>
    ScopeNotApproved = 1001502014,
}

/// <summary>
/// The Account Kit authorization result: <see cref="Status"/> plus the answer payload on success
/// (the anonymous phone for the quick-login call, the authorization code for the authorization
/// call). The payload may be an empty string when the kit returned no value.
/// </summary>
public readonly record struct OpenHarmonyAccountResult
{
    internal OpenHarmonyAccountResult(OpenHarmonyAccountStatus status, string? payload)
    {
        Status = status;
        Payload = payload;
    }

    /// <summary>The outcome; see <see cref="OpenHarmonyAccountStatus"/> for the code map.</summary>
    public OpenHarmonyAccountStatus Status { get; }

    /// <summary>The answer payload when <see cref="Success"/> is true; null otherwise.</summary>
    public string? Payload { get; }

    /// <summary>True when <see cref="Status"/> is <see cref="OpenHarmonyAccountStatus.Success"/>.</summary>
    public bool Success => Status == OpenHarmonyAccountStatus.Success;
}

/// <summary>
/// Huawei ID authorization over the Account Kit host bridge (Huawei ID one-tap login: anonymous
/// phone pre-fetch plus the general authorization request). All members degrade without throwing
/// when the host library or the shell's Account Kit sink is absent.
/// </summary>
public static partial class OpenHarmonyAccount
{
    private const string HostLibrary = "libopenharmonyhost.so";

    /// <summary>Shell op codes (see the host protocol in openharmony_host.h).</summary>
    private const int OpQuickLoginAnonymousPhone = 0;
    private const int OpAuthorize = 1;

    /// <summary>Time budget for one authorization, including the system UI the user interacts with.</summary>
    private static readonly TimeSpan s_timeout = TimeSpan.FromMinutes(5);

    private static readonly ConcurrentDictionary<int, TaskCompletionSource<(int Code, string Payload)>> s_pending = new();
    private static unsafe IntPtr s_callback = (IntPtr)(delegate* unmanaged[Cdecl]<int, int, int, IntPtr, void>)&OnResultNative;
    private static int s_nextId;
    private static bool s_registered;
    private static bool s_unavailable;

    [LibraryImport(HostLibrary, EntryPoint = "ohos_host_account_available", StringMarshalling = StringMarshalling.Utf8)]
    private static partial int AccountAvailable();

    [LibraryImport(HostLibrary, EntryPoint = "ohos_host_account_request", StringMarshalling = StringMarshalling.Utf8)]
    private static partial int AccountRequest(int requestId, int op, string scopes);

    [LibraryImport(HostLibrary, EntryPoint = "ohos_host_account_register_result", StringMarshalling = StringMarshalling.Utf8)]
    private static partial void AccountRegisterResult(IntPtr callback);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void AccountResultCallback(int requestId, int op, int code, IntPtr payloadUtf8);

    /// <summary>
    /// True when the host library exports the account bridge and the shell registered the sink
    /// (an HMS runtime with Account Kit), and no call has established that the kit is
    /// unavailable. The probe never shows authorization UI and answers false off-device instead
    /// of throwing.
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
                return AccountAvailable() == 1;
            }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
            {
                s_unavailable = true;
                return false;
            }
        }
    }

    /// <summary>
    /// Runs the Huawei ID one-tap login pre-fetch (<c>quickLoginAnonymousPhone</c> scope,
    /// <c>forceAuthorization=false</c>) and completes with the anonymous phone on success. The
    /// app then shows the Huawei ID one-tap login page and exchanges the number server-side;
    /// <see cref="OpenHarmonyAccountStatus.ScopeNotApproved"/> (1001502014) means the AGC
    /// permission for the scope has not been applied for or approved. Never throws when the
    /// platform path is unavailable.
    /// </summary>
    public static Task<OpenHarmonyAccountResult> GetQuickLoginAnonymousPhoneAsync(CancellationToken cancellationToken = default)
        => RequestAsync(OpQuickLoginAnonymousPhone, string.Empty, cancellationToken);

    /// <summary>
    /// Shows the system authorization UI for <paramref name="scopes"/> and completes with the
    /// authorization code (<c>response.data.authorizationCode</c>; the shell additionally passes
    /// the <c>serviceauthcode</c> permission). Exchange the code server-side for the Access
    /// Token / phone number; it is valid for 5 minutes and single-use. An empty list falls back
    /// to the openid scope in the shell. Never throws when the platform path is unavailable.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="scopes"/> is null.</exception>
    public static Task<OpenHarmonyAccountResult> AuthorizeAsync(IReadOnlyList<string> scopes, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scopes);
        string joined = scopes.Count > 0 ? string.Join('\n', scopes) : string.Empty;
        return RequestAsync(OpAuthorize, joined, cancellationToken);
    }

    private static async Task<OpenHarmonyAccountResult> RequestAsync(int op, string scopes, CancellationToken cancellationToken)
    {
        if (s_unavailable)
        {
            return new OpenHarmonyAccountResult(OpenHarmonyAccountStatus.Unavailable, null);
        }
        int requestId = Interlocked.Increment(ref s_nextId);
        var source = new TaskCompletionSource<(int Code, string Payload)>(TaskCreationOptions.RunContinuationsAsynchronously);
        s_pending[requestId] = source;
        try
        {
            EnsureRegistered();
            if (s_unavailable || AccountRequest(requestId, op, scopes) != 0)
            {
                s_pending.TryRemove(requestId, out _);
                s_unavailable = true;
                OpenHarmonyBridge.WriteStatus("[maui] account sink is not available (no Account Kit on this shell/device)");
                return new OpenHarmonyAccountResult(OpenHarmonyAccountStatus.Unavailable, null);
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            s_pending.TryRemove(requestId, out _);
            s_unavailable = true;
            OpenHarmonyBridge.WriteStatus("[maui] account bridge unavailable (no host library)");
            return new OpenHarmonyAccountResult(OpenHarmonyAccountStatus.Unavailable, null);
        }
        using CancellationTokenRegistration registration = cancellationToken.CanBeCanceled
            ? cancellationToken.Register(() => source.TrySetCanceled(cancellationToken))
            : default;
        Task completed = await Task.WhenAny(source.Task, Task.Delay(s_timeout, CancellationToken.None)).ConfigureAwait(false);
        if (completed != source.Task)
        {
            s_pending.TryRemove(requestId, out _);
            OpenHarmonyBridge.WriteStatus("[maui] account request timed out");
            return new OpenHarmonyAccountResult(OpenHarmonyAccountStatus.TimedOut, null);
        }
        try
        {
            (int code, string payload) = await source.Task.ConfigureAwait(false);
            if (code == (int)OpenHarmonyAccountStatus.Success)
            {
                return new OpenHarmonyAccountResult(OpenHarmonyAccountStatus.Success, payload);
            }
            if (code < 0)
            {
                // A local unavailability answer (kit missing, or the response state did not
                // match the request): stop retrying a broken platform path.
                s_unavailable = true;
                OpenHarmonyBridge.WriteStatus($"[maui] account request failed (code {code})");
                return new OpenHarmonyAccountResult(OpenHarmonyAccountStatus.Unavailable, null);
            }
            // An Account Kit BusinessError code: pass it through so the caller can map it
            // (1001502014 scope not approved, 1001500001 fingerprint mismatch, ...).
            OpenHarmonyBridge.WriteStatus($"[maui] account request failed (kit code {code})");
            return new OpenHarmonyAccountResult((OpenHarmonyAccountStatus)code, null);
        }
        catch (OperationCanceledException)
        {
            return new OpenHarmonyAccountResult(OpenHarmonyAccountStatus.Cancelled, null);
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
            AccountRegisterResult(s_callback);
            s_registered = true;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            s_unavailable = true;
            OpenHarmonyBridge.WriteStatus("[maui] account bridge unavailable (no host library)");
        }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static void OnResultNative(int requestId, int op, int code, IntPtr payloadUtf8)
    {
        if (!s_pending.TryRemove(requestId, out TaskCompletionSource<(int Code, string Payload)>? source))
        {
            return;
        }
        string payload = payloadUtf8 != IntPtr.Zero
            ? Marshal.PtrToStringAnsi(payloadUtf8) ?? string.Empty
            : string.Empty;
        source.TrySetResult((code, payload));
    }
}
