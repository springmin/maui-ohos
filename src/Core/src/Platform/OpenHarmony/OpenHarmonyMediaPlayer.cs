// Media playback for OpenHarmony - a platform extension, NOT a MAUI core interface (MAUI has no
// media-player abstraction; the CommunityToolkit.Maui MediaElement control is the consumer this
// bridge was built for). It rides the established host/ArkTS request-response bridge:
//
//   LoadAsync -> ohos_host_media_request(id, 0, "<kind>\t<location>") -> the ArkTS shell's
//     registerMediaSink handler lazily imports @kit.MediaKit, resolves the source (kind 0 http/
//     https/file URL through avPlayer.url, kind 1 rawfile through resourceManager.getRawFd into
//     avPlayer.fdSrc, kind 2 file path through fs.open + avPlayer.fdSrc) and answers once the
//     AVPlayer reached 'prepared'.
//   PlayAsync/PauseAsync/StopAsync -> op 1/2/3; SeekAsync -> op 4 with the millisecond target;
//     ReleaseAsync -> op 5 (stops, releases the AVPlayer and closes the shell's descriptor).
//   StatusAsync -> op 6; the shell answers "state\tpositionMs\tdurationMs" from the live player.
//
// Every AVPlayer state transition, time tick, duration report and error is pushed by the same
// sink through host.notifyMediaEvent(payload) and arrives on this side as StateChanged,
// PositionChanged, DurationChanged and Failed. The push lives on its own export
// (ohos_host_media_register_event), so an older host library that only serves the
// request/response half still works - the events then never fire.
//
// Result codes: 0 = the request completed (an empty success payload is a valid answer),
// -1 = the platform path is unavailable (no host library, no shell sink, no Media Kit), -2 = a
// transient kit failure (the SDK rejected the source or the player reported an error). -1 flips
// IsSupported false and keeps it there; -2 only fails that one call. A failed request carries a
// diagnostic message that is logged, never thrown. Off-device (no host library) every call
// answers Unavailable without throwing.
//
// Wire format. Requests carry tab-separated fields (the managed side rejects any field that
// contains a tab/LF/CR, so no escaping is needed in that direction). op and fields:
//   0 load    kind (0 url / 1 rawfile / 2 file path), location
//   1 play    (none)        2 pause (none)      3 stop (none)
//   4 seek    positionMs    5 release (none)    6 status (none)
// Success payloads: op 0/1/2/3/4/5 answer empty; op 6 answers "state\tpositionMs\tdurationMs".
//
// Event payloads (host.notifyMediaEvent), tab-separated:
//   state     state name, reason (empty when absent)
//   time      positionMs
//   duration  durationMs
//   error     code, message
// A malformed event payload is ignored. State names are the AVPlayer state strings the Media
// Kit documents ('idle'..'released'); the mapping table is <see cref="ParseState"/>.
using System.Collections.Concurrent;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Microsoft.OpenHarmony.Hosting;

namespace Microsoft.Maui.Platform;

/// <summary>Outcome class of one media request (see the file header for the code mapping).</summary>
public enum OpenHarmonyMediaStatus
{
    /// <summary>The request completed; an empty payload is a valid answer.</summary>
    Success = 0,

    /// <summary>
    /// The platform path is unavailable (no host library, no shell sink, no Media Kit, or a
    /// source the shell refused before reaching the player). <see cref="OpenHarmonyMediaPlayer.IsSupported"/>
    /// flips to false and stays there.
    /// </summary>
    Unavailable = -1,

    /// <summary>A transient kit failure (the SDK rejected the source, the player errored).</summary>
    Failed = -2,
}

/// <summary>
/// Result of one media request: a <see cref="OpenHarmonyMediaStatus"/> and an optional
/// diagnostic <see cref="Message"/> (the shell/kit error text, empty on success).
/// </summary>
public readonly record struct OpenHarmonyMediaResult(OpenHarmonyMediaStatus Status, string Message)
{
    /// <summary>True when <see cref="Status"/> is <see cref="OpenHarmonyMediaStatus.Success"/>.</summary>
    public bool IsSuccess => Status == OpenHarmonyMediaStatus.Success;
}

/// <summary>Where a media source comes from (the `kind` field of the load request).</summary>
public enum OpenHarmonyMediaSourceKind
{
    /// <summary>An http/https/file URL the Media Kit streams (AVPlayer.url).</summary>
    Url = 0,

    /// <summary>A HAP rawfile name (resources/rawfile/**, resolved to a descriptor by the shell).</summary>
    RawFile = 1,

    /// <summary>An absolute path in the app sandbox (the shell opens it and passes the descriptor).</summary>
    File = 2,
}

/// <summary>
/// One media source: a kind plus the location string the shell resolves. Constructed through the
/// factories so an invalid source fails before the bridge is called.
/// </summary>
public readonly record struct OpenHarmonyMediaSource
{
    private OpenHarmonyMediaSource(OpenHarmonyMediaSourceKind kind, string location)
    {
        Kind = kind;
        Location = location;
    }

    /// <summary>The source kind (URL, rawfile or sandbox file path).</summary>
    public OpenHarmonyMediaSourceKind Kind { get; }

    /// <summary>The URL / rawfile name / file path the shell resolves.</summary>
    public string Location { get; }

    /// <summary>An http/https/file URL source (the string is passed to the Media Kit unchanged).</summary>
    public static OpenHarmonyMediaSource FromUri(Uri uri) =>
        uri is null ? throw new ArgumentNullException(nameof(uri)) : new OpenHarmonyMediaSource(OpenHarmonyMediaSourceKind.Url, uri.ToString());

    /// <summary>An http/https/file URL source (the string is passed to the Media Kit unchanged).</summary>
    public static OpenHarmonyMediaSource FromUrl(string url) =>
        new(OpenHarmonyMediaSourceKind.Url, url ?? string.Empty);

    /// <summary>
    /// A HAP rawfile source, for example "media/intro.mp3" (relative to resources/rawfile).
    /// The shell resolves it with the ability context's resource manager.
    /// </summary>
    public static OpenHarmonyMediaSource FromRawFile(string name) =>
        new(OpenHarmonyMediaSourceKind.RawFile, name ?? string.Empty);

    /// <summary>An absolute path in the app sandbox the shell opens for the player.</summary>
    public static OpenHarmonyMediaSource FromFile(string path) =>
        new(OpenHarmonyMediaSourceKind.File, path ?? string.Empty);
}

/// <summary>Playback state as the Media Kit reports it (AVPlayer.state).</summary>
public enum OpenHarmonyMediaPlaybackState
{
    /// <summary>No state was reported yet (or the previous event was malformed).</summary>
    Unknown = 0,

    /// <summary>The player exists but has no source.</summary>
    Idle,

    /// <summary>The source is set; the player is not prepared.</summary>
    Initialized,

    /// <summary>The player is prepared and can play.</summary>
    Prepared,

    /// <summary>The player is playing.</summary>
    Playing,

    /// <summary>The player is paused.</summary>
    Paused,

    /// <summary>Playback reached the end of the stream.</summary>
    Completed,

    /// <summary>Playback was stopped (stop is a reset-shaped transition: the player can be prepared again).</summary>
    Stopped,

    /// <summary>The player was released; a new load creates a fresh one.</summary>
    Released,

    /// <summary>The player reported an error.</summary>
    Error,
}

/// <summary>Payload of <see cref="OpenHarmonyMediaPlayer.StateChanged"/>.</summary>
public sealed class OpenHarmonyMediaStateChangedEventArgs : EventArgs
{
    internal OpenHarmonyMediaStateChangedEventArgs(OpenHarmonyMediaPlaybackState state, string reason)
    {
        State = state;
        Reason = reason;
    }

    /// <summary>The new playback state.</summary>
    public OpenHarmonyMediaPlaybackState State { get; }

    /// <summary>
    /// The Media Kit's StateChangeReason when the push carried one (for example 'background',
    /// 'user', 'app'), otherwise empty. This is diagnostic text, not a managed enum.
    /// </summary>
    public string Reason { get; }
}

/// <summary>Payload of <see cref="OpenHarmonyMediaPlayer.PositionChanged"/>.</summary>
public sealed class OpenHarmonyMediaPositionChangedEventArgs : EventArgs
{
    internal OpenHarmonyMediaPositionChangedEventArgs(TimeSpan position) => Position = position;

    /// <summary>The playback position the player reported.</summary>
    public TimeSpan Position { get; }
}

/// <summary>Payload of <see cref="OpenHarmonyMediaPlayer.DurationChanged"/>.</summary>
public sealed class OpenHarmonyMediaDurationChangedEventArgs : EventArgs
{
    internal OpenHarmonyMediaDurationChangedEventArgs(TimeSpan duration) => Duration = duration;

    /// <summary>The stream duration the player reported.</summary>
    public TimeSpan Duration { get; }
}

/// <summary>
/// Result of <see cref="OpenHarmonyMediaPlayer.StatusAsync"/>: the request outcome plus the
/// live player snapshot the shell answered with (state, position, duration).
/// </summary>
public readonly record struct OpenHarmonyMediaStatusResult(
    OpenHarmonyMediaStatus Status,
    OpenHarmonyMediaPlaybackState State,
    TimeSpan Position,
    TimeSpan Duration,
    string Message)
{
    /// <summary>True when <see cref="Status"/> is <see cref="OpenHarmonyMediaStatus.Success"/>.</summary>
    public bool IsSuccess => Status == OpenHarmonyMediaStatus.Success;
}

/// <summary>Payload of <see cref="OpenHarmonyMediaPlayer.Failed"/>.</summary>
public sealed class OpenHarmonyMediaFailedEventArgs : EventArgs
{
    internal OpenHarmonyMediaFailedEventArgs(int code, string message)
    {
        Code = code;
        Message = message;
    }

    /// <summary>The Media Kit error code (0 when the push did not carry one).</summary>
    public int Code { get; }

    /// <summary>The Media Kit error message (empty when absent).</summary>
    public string Message { get; }
}

/// <summary>
/// Playback transport for OpenHarmony's Media Kit (AVPlayer), the platform half a
/// MediaElement-shaped control maps onto. Platform extension, not a MAUI core interface: the
/// CommunityToolkit.Maui MediaElement handler is the intended consumer. All calls degrade:
/// off-device (no host library) they answer <see cref="OpenHarmonyMediaStatus.Unavailable"/>
/// and never throw; <see cref="IsSupported"/> reports whether the platform path is available.
/// One process-wide player is owned by the shell sink, so this surface is static: a later
/// multi-player extension would add a player id to the request payloads.
/// </summary>
public static partial class OpenHarmonyMediaPlayer
{
    private const string HostLibrary = "libopenharmonyhost.so";
    private const int UnavailableCode = -1;

    // Request opcodes: the shell's registerMediaSink handler switches on these.
    private const int OpLoad = 0;
    private const int OpPlay = 1;
    private const int OpPause = 2;
    private const int OpStop = 3;
    private const int OpSeek = 4;
    private const int OpRelease = 5;
    private const int OpStatus = 6;

    private const int MaxFieldLength = 4096;

    private static readonly TimeSpan s_timeout = TimeSpan.FromSeconds(60);
    private static readonly ConcurrentDictionary<int, TaskCompletionSource<(int Code, string Payload)>> s_pending = new();
    private static readonly char[] s_fieldSeparators = { '\t', '\n', '\r' };

    private static unsafe IntPtr s_callback = (IntPtr)(delegate* unmanaged[Cdecl]<int, int, IntPtr, void>)&OnResultNative;
    private static unsafe IntPtr s_eventCallback = (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, void>)&OnEventNative;
    private static int s_nextId;
    private static bool s_registered;
    private static bool s_eventRegistered;
    private static bool s_eventUnavailable;
    private static bool s_unavailable;
    private static long s_positionMs;
    private static long s_durationMs;
    private static int s_state;

    [LibraryImport(HostLibrary, EntryPoint = "ohos_host_media_request", StringMarshalling = StringMarshalling.Utf8)]
    private static partial int MediaRequest(int requestId, int op, string payload);

    [LibraryImport(HostLibrary, EntryPoint = "ohos_host_media_register_result")]
    private static partial void MediaRegisterResult(IntPtr callback);

    [LibraryImport(HostLibrary, EntryPoint = "ohos_host_media_register_event")]
    private static partial void MediaRegisterEvent(IntPtr callback);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void MediaResultCallback(int requestId, int code, IntPtr payloadUtf8);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void MediaEventCallback(IntPtr payloadUtf8);

    /// <summary>
    /// Raised for every AVPlayer state transition the platform pushes (load/prepare, play,
    /// pause, stop, completion and error). A malformed push is ignored.
    /// </summary>
    public static event EventHandler<OpenHarmonyMediaStateChangedEventArgs>? StateChanged;

    /// <summary>
    /// Raised for every playback-position tick the platform pushes (and after a seek). The
    /// value is also readable through <see cref="Position"/>.
    /// </summary>
    public static event EventHandler<OpenHarmonyMediaPositionChangedEventArgs>? PositionChanged;

    /// <summary>
    /// Raised when the platform reports the stream duration (after preparation and on a live
    /// stream when it becomes known). The value is also readable through <see cref="Duration"/>.
    /// </summary>
    public static event EventHandler<OpenHarmonyMediaDurationChangedEventArgs>? DurationChanged;

    /// <summary>
    /// Raised when the platform reports a playback error (AVPlayer 'error'); the state also
    /// flips to <see cref="OpenHarmonyMediaPlaybackState.Error"/>.
    /// </summary>
    public static event EventHandler<OpenHarmonyMediaFailedEventArgs>? Failed;

    /// <summary>
    /// True when the host library is present and no call has established that the shell sink or
    /// the Media Kit is unavailable. The probe performs no request, so off-device it is false
    /// immediately.
    /// </summary>
    public static bool IsSupported => !s_unavailable;

    /// <summary>The last playback state the platform pushed (Unknown until the first push).</summary>
    public static OpenHarmonyMediaPlaybackState State => (OpenHarmonyMediaPlaybackState)s_state;

    /// <summary>The last playback position the platform pushed.</summary>
    public static TimeSpan Position => TimeSpan.FromMilliseconds(s_positionMs);

    /// <summary>The last stream duration the platform pushed.</summary>
    public static TimeSpan Duration => TimeSpan.FromMilliseconds(s_durationMs);

    /// <summary>
    /// Loads <paramref name="source"/> into the platform player and prepares it (the call
    /// completes once the player reports 'prepared'). Loading a new source releases the previous
    /// one. An invalid source (empty location, a field that could forge the wire format) fails
    /// the call instead of throwing. Never throws.
    /// </summary>
    public static Task<OpenHarmonyMediaResult> LoadAsync(OpenHarmonyMediaSource source, CancellationToken cancellationToken = default)
    {
        if (!IsValidField(source.Location))
        {
            return Task.FromResult(Failure("load", "the source location is empty or too long"));
        }
        return RequestAsync(
            OpLoad,
            new[] { ((int)source.Kind).ToString(CultureInfo.InvariantCulture), source.Location },
            cancellationToken);
    }

    /// <summary>
    /// Starts playback of the loaded source (the player must be prepared). Completes when the
    /// platform accepted the call; the state push arrives through
    /// <see cref="StateChanged"/>. Never throws.
    /// </summary>
    public static Task<OpenHarmonyMediaResult> PlayAsync(CancellationToken cancellationToken = default) =>
        RequestAsync(OpPlay, Array.Empty<string>(), cancellationToken);

    /// <summary>
    /// Pauses playback. Completes when the platform accepted the call; the state push arrives
    /// through <see cref="StateChanged"/>. Never throws.
    /// </summary>
    public static Task<OpenHarmonyMediaResult> PauseAsync(CancellationToken cancellationToken = default) =>
        RequestAsync(OpPause, Array.Empty<string>(), cancellationToken);

    /// <summary>
    /// Stops playback and releases the current source; the player can be loaded again
    /// afterwards. Never throws.
    /// </summary>
    public static Task<OpenHarmonyMediaResult> StopAsync(CancellationToken cancellationToken = default) =>
        RequestAsync(OpStop, Array.Empty<string>(), cancellationToken);

    /// <summary>
    /// Seeks to <paramref name="position"/> (clamped by the platform to the stream bounds). The
    /// completed call carries the position the platform acknowledged; position ticks then
    /// arrive through <see cref="PositionChanged"/>. Never throws.
    /// </summary>
    public static Task<OpenHarmonyMediaResult> SeekAsync(TimeSpan position, CancellationToken cancellationToken = default)
    {
        if (position < TimeSpan.Zero)
        {
            return Task.FromResult(Failure("seek", "the position must not be negative"));
        }
        return RequestAsync(
            OpSeek,
            new[] { ((long)position.TotalMilliseconds).ToString(CultureInfo.InvariantCulture) },
            cancellationToken);
    }

    /// <summary>
    /// Releases the platform player and its descriptor (the shell closes what it opened). A
    /// later <see cref="LoadAsync"/> creates a fresh player. Never throws.
    /// </summary>
    public static Task<OpenHarmonyMediaResult> ReleaseAsync(CancellationToken cancellationToken = default) =>
        RequestAsync(OpRelease, Array.Empty<string>(), cancellationToken);

    /// <summary>
    /// Asks the shell for the live player status and answers the request outcome plus the
    /// (state, position, duration) snapshot the reply carried; the values are also mirrored
    /// into <see cref="State"/>/<see cref="Position"/>/<see cref="Duration"/>. Unavailable
    /// off-device. Never throws.
    /// </summary>
    public static async Task<OpenHarmonyMediaStatusResult> StatusAsync(CancellationToken cancellationToken = default)
    {
        (int Code, string Payload)? answer = await SendAsync(OpStatus, Array.Empty<string>(), cancellationToken).ConfigureAwait(false);
        if (answer is not { } result)
        {
            return new OpenHarmonyMediaStatusResult(
                OpenHarmonyMediaStatus.Unavailable, State, Position, Duration, "the platform bridge is unavailable");
        }
        if (result.Code != 0)
        {
            return new OpenHarmonyMediaStatusResult(
                result.Code == UnavailableCode ? OpenHarmonyMediaStatus.Unavailable : OpenHarmonyMediaStatus.Failed,
                State,
                Position,
                Duration,
                result.Payload.Length > 0 ? result.Payload : $"the platform answered {result.Code}");
        }
        ApplyStatusPayload(result.Payload);
        return new OpenHarmonyMediaStatusResult(OpenHarmonyMediaStatus.Success, State, Position, Duration, string.Empty);
    }

    /// <summary>Maps one AVPlayer state string onto the enum; an unknown name answers Unknown.</summary>
    public static OpenHarmonyMediaPlaybackState ParseState(string? state) => state switch
    {
        "idle" => OpenHarmonyMediaPlaybackState.Idle,
        "initialized" => OpenHarmonyMediaPlaybackState.Initialized,
        "prepared" => OpenHarmonyMediaPlaybackState.Prepared,
        "playing" => OpenHarmonyMediaPlaybackState.Playing,
        "paused" => OpenHarmonyMediaPlaybackState.Paused,
        "completed" => OpenHarmonyMediaPlaybackState.Completed,
        "stopped" => OpenHarmonyMediaPlaybackState.Stopped,
        "released" => OpenHarmonyMediaPlaybackState.Released,
        "error" => OpenHarmonyMediaPlaybackState.Error,
        _ => OpenHarmonyMediaPlaybackState.Unknown,
    };

    /// <summary>
    /// Parses the op 6 status payload ("state\tpositionMs\tdurationMs") into the managed
    /// mirrors; a malformed payload leaves them unchanged. Also used by the harness to pin the
    /// parser without a device.
    /// </summary>
    public static void ApplyStatusPayload(string? payload)
    {
        if (string.IsNullOrEmpty(payload))
        {
            return;
        }
        string[] fields = payload.Split('\t');
        if (fields.Length < 3)
        {
            return;
        }
        OpenHarmonyMediaPlaybackState state = ParseState(fields[0]);
        if (state == OpenHarmonyMediaPlaybackState.Unknown && fields[0] != "unknown")
        {
            return;
        }
        if (!long.TryParse(fields[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out long positionMs) ||
            !long.TryParse(fields[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out long durationMs))
        {
            return;
        }
        s_state = (int)state;
        s_positionMs = Math.Max(0, positionMs);
        s_durationMs = Math.Max(0, durationMs);
    }

    /// <summary>
    /// Native-shaped entry point for the host's event notify (harness-testable): one
    /// "kind\tfields..." payload, ignored when malformed. State events raise
    /// <see cref="StateChanged"/>, time events <see cref="PositionChanged"/>, duration events
    /// <see cref="DurationChanged"/> and error events <see cref="Failed"/> (plus a state
    /// transition to Error).
    /// </summary>
    internal static void OnEventPayload(string? payload)
    {
        if (string.IsNullOrEmpty(payload))
        {
            return;
        }
        string[] fields = payload.Split('\t');
        if (fields[0] == "time" && fields.Length == 2)
        {
            if (long.TryParse(fields[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out long positionMs))
            {
                s_positionMs = Math.Max(0, positionMs);
                PositionChanged?.Invoke(null, new OpenHarmonyMediaPositionChangedEventArgs(Position));
            }
        }
        else if (fields[0] == "duration" && fields.Length == 2)
        {
            if (long.TryParse(fields[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out long durationMs))
            {
                s_durationMs = Math.Max(0, durationMs);
                DurationChanged?.Invoke(null, new OpenHarmonyMediaDurationChangedEventArgs(Duration));
            }
        }
        else if (fields[0] == "state" && (fields.Length == 2 || fields.Length == 3))
        {
            OpenHarmonyMediaPlaybackState state = ParseState(fields[1]);
            if (state == OpenHarmonyMediaPlaybackState.Unknown && fields[1] != "unknown")
            {
                return;
            }
            s_state = (int)state;
            string reason = fields.Length == 3 ? fields[2] : string.Empty;
            StateChanged?.Invoke(null, new OpenHarmonyMediaStateChangedEventArgs(state, reason));
        }
        else if (fields[0] == "error" && fields.Length == 3)
        {
            int.TryParse(fields[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int code);
            s_state = (int)OpenHarmonyMediaPlaybackState.Error;
            Failed?.Invoke(null, new OpenHarmonyMediaFailedEventArgs(code, fields[2]));
        }
    }

    /// <summary>A field is valid when it is non-empty, short and cannot forge a record separator.</summary>
    private static bool IsValidField(string? value) =>
        !string.IsNullOrWhiteSpace(value) &&
        value.Length <= MaxFieldLength &&
        value.IndexOfAny(s_fieldSeparators) < 0;

    private static OpenHarmonyMediaResult Failure(string operation, string why) =>
        new(OpenHarmonyMediaStatus.Failed, $"{operation}: {why}");

    private static async Task<OpenHarmonyMediaResult> RequestAsync(
        int op,
        string[] fields,
        CancellationToken cancellationToken)
    {
        (int Code, string Payload)? answer = await SendAsync(op, fields, cancellationToken).ConfigureAwait(false);
        if (answer is not { } result)
        {
            return new OpenHarmonyMediaResult(OpenHarmonyMediaStatus.Unavailable, "the platform bridge is unavailable");
        }
        if (result.Code == 0)
        {
            return new OpenHarmonyMediaResult(OpenHarmonyMediaStatus.Success, string.Empty);
        }
        return new OpenHarmonyMediaResult(
            result.Code == UnavailableCode ? OpenHarmonyMediaStatus.Unavailable : OpenHarmonyMediaStatus.Failed,
            result.Payload.Length > 0 ? result.Payload : $"the platform answered {result.Code}");
    }

    private static async Task<(int Code, string Payload)?> SendAsync(
        int op,
        string[] fields,
        CancellationToken cancellationToken)
    {
        if (s_unavailable)
        {
            return null;
        }
        int requestId = Interlocked.Increment(ref s_nextId);
        var source = new TaskCompletionSource<(int Code, string Payload)>(TaskCreationOptions.RunContinuationsAsynchronously);
        s_pending[requestId] = source;
        try
        {
            EnsureRegistered();
            if (s_unavailable || MediaRequest(requestId, op, string.Join('\t', fields)) != 0)
            {
                s_pending.TryRemove(requestId, out _);
                s_unavailable = true;
                OpenHarmonyBridge.WriteStatus("[maui] media sink is not available");
                return null;
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            s_pending.TryRemove(requestId, out _);
            s_unavailable = true;
            OpenHarmonyBridge.WriteStatus("[maui] media bridge unavailable (no host library)");
            return null;
        }
        using CancellationTokenRegistration registration = cancellationToken.CanBeCanceled
            ? cancellationToken.Register(() => source.TrySetCanceled(cancellationToken))
            : default;
        Task completed = await Task.WhenAny(source.Task, Task.Delay(s_timeout, CancellationToken.None)).ConfigureAwait(false);
        if (completed != source.Task)
        {
            s_pending.TryRemove(requestId, out _);
            OpenHarmonyBridge.WriteStatus("[maui] media request timed out");
            return null;
        }
        try
        {
            (int Code, string Payload) answer = await source.Task.ConfigureAwait(false);
            if (answer.Code == UnavailableCode)
            {
                s_unavailable = true;
            }
            return answer;
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
            MediaRegisterResult(s_callback);
            s_registered = true;
            EnsureEventRegistered();
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            s_unavailable = true;
            OpenHarmonyBridge.WriteStatus("[maui] media bridge unavailable (no host library)");
        }
    }

    // The event notify is a separate export: a host without it still serves the request/response
    // half, so a missing export disables the pushed events only (not the whole extra).
    private static void EnsureEventRegistered()
    {
        if (s_eventRegistered || s_eventUnavailable)
        {
            return;
        }
        try
        {
            MediaRegisterEvent(s_eventCallback);
            s_eventRegistered = true;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            s_eventUnavailable = true;
            OpenHarmonyBridge.WriteStatus("[maui] media event bridge unavailable (older host)");
        }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static void OnResultNative(int requestId, int code, IntPtr payloadUtf8)
    {
        string payload = payloadUtf8 == IntPtr.Zero
            ? string.Empty
            : Marshal.PtrToStringUTF8(payloadUtf8) ?? string.Empty;
        if (s_pending.TryRemove(requestId, out TaskCompletionSource<(int Code, string Payload)>? source))
        {
            source.TrySetResult((code, payload));
        }
    }

    // A reverse P/Invoke entry: the pushed events run application handlers (StateChanged /
    // PositionChanged / DurationChanged / Failed), so an exception must not unwind into the
    // native frame (MB-2).
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static void OnEventNative(IntPtr payloadUtf8)
    {
        try
        {
            string payload = payloadUtf8 == IntPtr.Zero
                ? string.Empty
                : Marshal.PtrToStringUTF8(payloadUtf8) ?? string.Empty;
            OnEventPayload(payload);
        }
        catch (Exception ex)
        {
            OpenHarmonyStatus.NativeCallbackFailed("media event", ex);
        }
    }
}
