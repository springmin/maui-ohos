// HMS Live View Kit platform extra (R2-SHELL-EXT 2026-09-26): the live view (实况窗) bridge.
//
//   OpenHarmonyLiveView.StartAsync(update) -> ohos_host_liveview_request(id, 0, argsJson) ->
//     the ArkTS shell's registerLiveViewSink handler checks isLiveViewEnabled(), builds the
//     TIMER live view object (title/text/progress/timer) and calls
//     liveViewManager.startLiveView -> host.notifyLiveViewResult -> ohos_host_liveview_result ->
//     the registered managed callback completes the awaiting task.
//   UpdateAsync -> op 1 (updateLiveView, sequence incremented by the shell), StopAsync -> op 2
//     (stopLiveView, the shell keeps the last view's id as the target).
//   IsSupported asks ohos_host_liveview_available() and never calls the kit.
//
// The shell registers the sink only when canIUse('SystemCapability.LiveView.LiveViewService')
// passes and its runtime resolves @kit.LiveViewKit (the R2-SHELL-EXT variable-specifier probe in
// the shell templates), so the default OpenHarmony-shell build registers nothing: every call
// answers Unavailable/Disabled and these APIs degrade without throwing. On an HMS device the kit
// still gates on the AGC entitlement and the user's live view switch.
//
// Status mapping: non-negative values are the Live View Kit BusinessError codes passed through
// unchanged, so the documented ones (see OpenHarmonyLiveViewStatus) stay distinguishable from
// the local -1..-4 outcomes; only Unavailable/Disabled flip the bridge off for the rest of the
// process (both are configuration states a retry cannot fix in-process).
using System.Collections.Concurrent;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.OpenHarmony.Hosting;

namespace Microsoft.Maui.Platform;

/// <summary>
/// The outcome of an <see cref="OpenHarmonyLiveView"/> call. Non-negative values are the Live
/// View Kit BusinessError codes, passed through unchanged so a caller can branch on the
/// documented ones; the negative values are the local bridge outcomes.
/// </summary>
public enum OpenHarmonyLiveViewStatus
{
    /// <summary>The live view operation completed.</summary>
    Success = 0,

    /// <summary>No host library, or the shell did not register the Live View Kit sink.</summary>
    Unavailable = -1,

    /// <summary>The shell's kit call failed, or the arguments were malformed.</summary>
    CallFailed = -2,

    /// <summary>The user's live view switch is off (the shell checked <c>isLiveViewEnabled()</c>).</summary>
    Disabled = -3,

    /// <summary>An update/stop arrived before a successful create (no view the shell owns).</summary>
    NoActiveView = -4,

    /// <summary>The shell did not answer within the bridge's time budget.</summary>
    TimedOut = -100,

    /// <summary>The caller's cancellation token fired.</summary>
    Cancelled = -101,

    /// <summary>Live View Kit internal error (1003500001); a retry may succeed.</summary>
    SystemError = 1003500001,

    /// <summary>Serialization or deserialization failed (1003500002); a retry may succeed.</summary>
    SerializationFailed = 1003500002,

    /// <summary>Connecting to the Live View service failed (1003500003); a retry may succeed.</summary>
    ServiceConnectionFailed = 1003500003,

    /// <summary>The user's live view switch is off (1003500004, the kit's own answer).</summary>
    SwitchOff = 1003500004,

    /// <summary>The AGC live view entitlement is not approved for this event scene (1003500005).</summary>
    RightsNotEnabled = 1003500005,

    /// <summary>The live view id already exists (1003500006); use another id or stop it first.</summary>
    AlreadyExists = 1003500006,

    /// <summary>The service is unreachable (1003500007); check the network.</summary>
    NetworkUnreachable = 1003500007,

    /// <summary>Creation/update rate limit exceeded (1003500008).</summary>
    RateLimited = 1003500008,

    /// <summary>The live view does not exist or already finished (1003500009).</summary>
    NotFound = 1003500009,

    /// <summary>The live view finished and is inside its keep time (1003500010).</summary>
    AlreadyFinished = 1003500010,

    /// <summary>The sequence is not greater than the current one (1003500011).</summary>
    SequenceIncorrect = 1003500011,

    /// <summary>Subscription count exceeded (1003500012).</summary>
    SubscriptionLimitExceeded = 1003500012,

    /// <summary>Invalid subscription scene (1003500013); check the event value.</summary>
    InvalidEvent = 1003500013,

    /// <summary>The reminder time is more than 30 days away (1003500014).</summary>
    AlertTimeTooFar = 1003500014,

    /// <summary>The live view subscription failed (1003500015); a retry may succeed.</summary>
    SubscriptionFailed = 1003500015,

    /// <summary>Subscription request rate limit exceeded (1003500016).</summary>
    SubscriptionRateLimited = 1003500016,

    /// <summary>Invalid argument (401); the kit rejected a field of the request.</summary>
    InvalidArgument = 401,
}

/// <summary>
/// One live view content update: the app-defined id, the primary title/text, the progress
/// (0-100) and the timer value in milliseconds. The shell renders it on the TIMER scene (progress
/// template) and increments the sequence for updates; the id must identify one view.
/// </summary>
/// <param name="Id">App-defined live view id (the TIMER scene uses one view per id).</param>
/// <param name="Title">Primary title; empty becomes "计时中" in the shell.</param>
/// <param name="Text">Primary content text; empty becomes the id-derived default.</param>
/// <param name="Progress">Progress percentage, 0-100 (values outside are clamped).</param>
/// <param name="TimeMilliseconds">Timer value in milliseconds (0 for a plain progress display).</param>
public readonly record struct OpenHarmonyLiveViewUpdate(
    int Id,
    string Title,
    string Text,
    double Progress = 0,
    long TimeMilliseconds = 0);

/// <summary>
/// Live View Kit (liveViewManager) bridge over the host sink. All members degrade without
/// throwing when the host library or the shell's Live View sink is absent.
/// </summary>
public static partial class OpenHarmonyLiveView
{
    private const string HostLibrary = "libopenharmonyhost.so";

    /// <summary>Shell op codes (see the host protocol in openharmony_host.h).</summary>
    private const int OpCreate = 0;
    private const int OpUpdate = 1;
    private const int OpStop = 2;

    /// <summary>Time budget for one kit round trip; the local kit calls answer well inside it.</summary>
    private static readonly TimeSpan s_timeout = TimeSpan.FromSeconds(30);

    private static readonly ConcurrentDictionary<int, TaskCompletionSource<(int Code, string Payload)>> s_pending = new();
    private static unsafe IntPtr s_callback = (IntPtr)(delegate* unmanaged[Cdecl]<int, int, int, IntPtr, void>)&OnResultNative;
    private static int s_nextId;
    private static bool s_registered;
    private static bool s_unavailable;

    [LibraryImport(HostLibrary, EntryPoint = "ohos_host_liveview_available", StringMarshalling = StringMarshalling.Utf8)]
    private static partial int LiveViewAvailable();

    [LibraryImport(HostLibrary, EntryPoint = "ohos_host_liveview_request", StringMarshalling = StringMarshalling.Utf8)]
    private static partial int LiveViewRequest(int requestId, int op, string args);

    [LibraryImport(HostLibrary, EntryPoint = "ohos_host_liveview_register_result", StringMarshalling = StringMarshalling.Utf8)]
    private static partial void LiveViewRegisterResult(IntPtr callback);

    // Informative declaration of the native callback shape; the pointer is taken from
    // OnResultNative below (the delegate is never instantiated).
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void LiveViewResultCallback(int requestId, int op, int code, IntPtr payloadUtf8);

    /// <summary>
    /// True when the host library exports the live view bridge and the shell registered the sink
    /// (a runtime with the Live View syscap and @kit.LiveViewKit), and no call has established
    /// that the kit is unavailable. The probe never creates a view and answers false off-device
    /// instead of throwing.
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
                return LiveViewAvailable() == 1;
            }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
            {
                s_unavailable = true;
                return false;
            }
        }
    }

    /// <summary>
    /// Creates the live view (or replaces the content of the view with this id). Never throws
    /// when the platform path is unavailable: the local status (Unavailable/Disabled/CallFailed)
    /// or a kit BusinessError code is returned instead.
    /// </summary>
    public static async Task<OpenHarmonyLiveViewStatus> StartAsync(OpenHarmonyLiveViewUpdate update, CancellationToken cancellationToken = default)
    {
        (int code, _) = await SendAsync(OpCreate, Args(update), cancellationToken).ConfigureAwait(false);
        return MapStatus(code);
    }

    /// <summary>
    /// Updates the live view with this id (the shell increments the sequence). A missing view
    /// answers <see cref="OpenHarmonyLiveViewStatus.NoActiveView"/>.
    /// </summary>
    public static async Task<OpenHarmonyLiveViewStatus> UpdateAsync(OpenHarmonyLiveViewUpdate update, CancellationToken cancellationToken = default)
    {
        (int code, _) = await SendAsync(OpUpdate, Args(update), cancellationToken).ConfigureAwait(false);
        return MapStatus(code);
    }

    /// <summary>Stops (dismisses) the live view with <paramref name="id"/>.</summary>
    public static async Task<OpenHarmonyLiveViewStatus> StopAsync(int id, CancellationToken cancellationToken = default)
    {
        (int code, _) = await SendAsync(OpStop, StopArgs(id), cancellationToken).ConfigureAwait(false);
        return MapStatus(code);
    }

    private static OpenHarmonyLiveViewStatus MapStatus(int code) => code switch
    {
        0 => OpenHarmonyLiveViewStatus.Success,
        -1 => OpenHarmonyLiveViewStatus.Unavailable,
        -2 => OpenHarmonyLiveViewStatus.CallFailed,
        -3 => OpenHarmonyLiveViewStatus.Disabled,
        -4 => OpenHarmonyLiveViewStatus.NoActiveView,
        _ => (OpenHarmonyLiveViewStatus)code,
    };

    // Queues one live view operation and completes with the shell's answer. Every failure path
    // (unregistered sink, missing export, timeout, cancellation) answers a local status instead
    // of throwing.
    private static async Task<(int Code, string Payload)> SendAsync(int op, string args, CancellationToken cancellationToken)
    {
        if (s_unavailable)
        {
            return ((int)OpenHarmonyLiveViewStatus.Unavailable, string.Empty);
        }
        int requestId = Interlocked.Increment(ref s_nextId);
        var source = new TaskCompletionSource<(int Code, string Payload)>(TaskCreationOptions.RunContinuationsAsynchronously);
        s_pending[requestId] = source;
        try
        {
            EnsureRegistered();
            if (s_unavailable || LiveViewRequest(requestId, op, args) != 0)
            {
                s_pending.TryRemove(requestId, out _);
                s_unavailable = true;
                OpenHarmonyBridge.WriteStatus("[maui] live view sink is not available (no Live View Kit on this shell/device)");
                return ((int)OpenHarmonyLiveViewStatus.Unavailable, string.Empty);
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            s_pending.TryRemove(requestId, out _);
            s_unavailable = true;
            OpenHarmonyBridge.WriteStatus("[maui] live view bridge unavailable (no host library)");
            return ((int)OpenHarmonyLiveViewStatus.Unavailable, string.Empty);
        }
        using CancellationTokenRegistration registration = cancellationToken.CanBeCanceled
            ? cancellationToken.Register(() => source.TrySetCanceled(cancellationToken))
            : default;
        Task completed = await Task.WhenAny(source.Task, Task.Delay(s_timeout, CancellationToken.None)).ConfigureAwait(false);
        if (completed != source.Task)
        {
            s_pending.TryRemove(requestId, out _);
            OpenHarmonyBridge.WriteStatus("[maui] live view request timed out");
            return ((int)OpenHarmonyLiveViewStatus.TimedOut, string.Empty);
        }
        try
        {
            (int code, string payload) = await source.Task.ConfigureAwait(false);
            if (code == -1 || code == -3)
            {
                // A local config answer (no kit / switch off): stop retrying the platform path.
                s_unavailable = code == -1;
                OpenHarmonyBridge.WriteStatus($"[maui] live view request answered status {code}");
            }
            else if (code < 0 && code != -4)
            {
                OpenHarmonyBridge.WriteStatus($"[maui] live view request failed (status {code})");
            }
            else if (code > 0)
            {
                OpenHarmonyBridge.WriteStatus($"[maui] live view request failed (kit code {code})");
            }
            return (code, payload);
        }
        catch (OperationCanceledException)
        {
            return ((int)OpenHarmonyLiveViewStatus.Cancelled, string.Empty);
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
            LiveViewRegisterResult(s_callback);
            s_registered = true;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            s_unavailable = true;
            OpenHarmonyBridge.WriteStatus("[maui] live view bridge unavailable (no host library)");
        }
    }

    // Native callback. Must not throw: an exception crossing the native boundary is a fail-fast.
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static void OnResultNative(int requestId, int op, int code, IntPtr payloadUtf8)
    {
        if (!s_pending.TryRemove(requestId, out TaskCompletionSource<(int Code, string Payload)>? source))
        {
            return;
        }
        string payload = payloadUtf8 != IntPtr.Zero
            ? Marshal.PtrToStringUTF8(payloadUtf8) ?? string.Empty
            : string.Empty;
        source.TrySetResult((code, payload));
    }

    // op 0/1 argument JSON: {"id":1,"title":"...","text":"...","progress":50,"time":123456}.
    // Built by hand (invariant culture) so the shell's JSON.parse sees JSON numbers; the payload
    // stays trim/AOT friendly (no reflection serializer).
    private static string Args(OpenHarmonyLiveViewUpdate update)
    {
        StringBuilder builder = new(160);
        builder.Append("{\"id\":");
        builder.Append(update.Id.ToString(CultureInfo.InvariantCulture));
        builder.Append(",\"title\":\"");
        AppendEscaped(builder, update.Title ?? string.Empty);
        builder.Append("\",\"text\":\"");
        AppendEscaped(builder, update.Text ?? string.Empty);
        builder.Append("\",\"progress\":");
        builder.Append(ClampProgress(update.Progress).ToString("R", CultureInfo.InvariantCulture));
        builder.Append(",\"time\":");
        builder.Append(Math.Max(0, update.TimeMilliseconds).ToString(CultureInfo.InvariantCulture));
        builder.Append('}');
        return builder.ToString();
    }

    private static string StopArgs(int id)
    {
        StringBuilder builder = new(24);
        builder.Append("{\"id\":");
        builder.Append(id.ToString(CultureInfo.InvariantCulture));
        builder.Append('}');
        return builder.ToString();
    }

    // The kit's progress range is [0, 100]; a non-finite value answers 0 instead of poisoning
    // the JSON with NaN/Infinity.
    private static double ClampProgress(double progress) =>
        double.IsFinite(progress) ? Math.Clamp(progress, 0.0, 100.0) : 0.0;

    // Minimal JSON string escaping for the title/text fields ('"', '\', control characters).
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
