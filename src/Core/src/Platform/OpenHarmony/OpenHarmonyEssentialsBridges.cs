// Request/result and push bridges for the Essentials surfaces that talk to real platform kits:
// runtime permissions, the system pasteboard and network access. Every bridge follows the same
// shape as the other host bridges in this slice: a P/Invoke request or listener registration, a
// managed thunk kept alive for the process lifetime, and guarded calls so a desktop build
// without libopenharmonyhost.so (or an older host library without the export) degrades to the
// documented "unavailable" answer instead of throwing.
//
//   IPermissions.RequestAsync -> ohos_host_request_permission(permission, id) -> the shell's
//     registerPermissionSink handler (abilityAccessCtrl.requestPermissionsFromUser) ->
//     host.permissionResult(id, granted) -> ohos_host_permission_result callback -> TCS (deny
//     when the shell does not answer inside RequestTimeout).
//   IClipboard -> ohos_host_clipboard_request(id, op, text) -> the shell's
//     registerClipboardSink handler (@ohos.pasteboard) -> host.clipboardResult(id, rc, text) ->
//     TCS; the pasteboard 'update' observer pushes host.notifyClipboardChanged() into the
//     registered ohos_host_clipboard_changed callback.
//   IConnectivity.NetworkAccess -> ohos_host_network_access (0 unknown, 1 none, 2 local,
//     3 internet, the NDK path); the shell follows the NetworkKit connection events and pushes
//     host.notifyNetworkAccess() -> ohos_host_network_access_notify -> the registered
//     ohos_host_network_access callback (the level is re-read in the host).
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Microsoft.Maui.ApplicationModel;

namespace Microsoft.Maui.Platform;

/// <summary>
/// One runtime-permission request over the host/ArkTS bridge. The request id is allocated here;
/// the callback registered with the host completes the matching <see cref="TaskCompletionSource{TResult}"/>.
/// </summary>
internal static partial class OpenHarmonyPermissionBridge
{
    private const string HostLibrary = "libopenharmonyhost.so";

    /// <summary>
    /// How long a permission prompt may take before the request is answered with "denied".
    /// The prompt is user-driven, so the timeout is generous; it only bounds a lost answer.
    /// </summary>
    internal static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(30);

    [LibraryImport(HostLibrary, EntryPoint = "ohos_host_request_permission", StringMarshalling = StringMarshalling.Utf8)]
    private static partial void RequestPermissionNative(string permission, int requestId);

    [LibraryImport(HostLibrary, EntryPoint = "ohos_host_register_permission_result")]
    private static partial void RegisterPermissionResultNative(IntPtr callback);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void PermissionResultCallback(int requestId, int granted);

    private static readonly object s_sync = new();
    private static readonly Dictionary<int, TaskCompletionSource<bool>> s_pending = new();
    private static PermissionResultCallback? s_callback;
    private static bool s_registered;
    private static bool s_unavailable;
    private static int s_nextRequestId;

    [ModuleInitializer]
    internal static void Initialize() => Register();

    /// <summary>Registers the native result callback; a guarded no-op off-device.</summary>
    internal static void Register()
    {
        if (s_registered || s_unavailable)
        {
            return;
        }
        try
        {
            s_callback = OnNativePermissionResult;
            RegisterPermissionResultNative(Marshal.GetFunctionPointerForDelegate(s_callback));
            s_registered = true;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            s_unavailable = true;
        }
    }

    /// <summary>
    /// Asks the shell for <paramref name="permission"/>. Returns the granted flag, or null when
    /// the host library/sink is unavailable, the request could not be dispatched or the shell
    /// did not answer inside <paramref name="timeout"/> (the caller treats null as denied).
    /// </summary>
    internal static async Task<bool?> RequestAsync(string permission, TimeSpan timeout)
    {
        if (!s_registered)
        {
            Register();
            if (s_unavailable)
            {
                return null;
            }
        }
        int requestId = Interlocked.Increment(ref s_nextRequestId);
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (s_sync)
        {
            s_pending[requestId] = completion;
        }
        bool dispatched = true;
        try
        {
            RequestPermissionNative(permission, requestId);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            dispatched = false;
        }
        if (!dispatched)
        {
            lock (s_sync)
            {
                s_pending.Remove(requestId);
            }
            return null;
        }
        Task finished = await Task.WhenAny(completion.Task, Task.Delay(timeout)).ConfigureAwait(false);
        if (finished != completion.Task)
        {
            lock (s_sync)
            {
                s_pending.Remove(requestId);
            }
            return null;
        }
        return await completion.Task.ConfigureAwait(false);
    }

    private static void OnNativePermissionResult(int requestId, int granted)
    {
        TaskCompletionSource<bool>? completion;
        lock (s_sync)
        {
            if (!s_pending.Remove(requestId, out completion))
            {
                return;
            }
        }
        completion?.TrySetResult(granted != 0);
    }
}

/// <summary>
/// One clipboard operation over the host/ArkTS bridge, plus the pasteboard change push. op:
/// 0 has text, 1 get text, 2 set text; the shell answers (rc 0 success / -1 unavailable) with
/// the value in <c>text</c>.
/// </summary>
internal static partial class OpenHarmonyClipboardBridge
{
    private const string HostLibrary = "libopenharmonyhost.so";

    internal const int HasOp = 0;
    internal const int GetOp = 1;
    internal const int SetOp = 2;

    /// <summary>Bounds a lost pasteboard answer; the shell calls are local and fast.</summary>
    internal static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(5);

    [LibraryImport(HostLibrary, EntryPoint = "ohos_host_clipboard_request", StringMarshalling = StringMarshalling.Utf8)]
    private static partial void RequestNative(int requestId, int op, string text);

    [LibraryImport(HostLibrary, EntryPoint = "ohos_host_clipboard_register_result")]
    private static partial void RegisterResultNative(IntPtr callback);

    [LibraryImport(HostLibrary, EntryPoint = "ohos_host_clipboard_register_changed")]
    private static partial void RegisterChangedNative(IntPtr callback);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void ClipboardResultCallback(int requestId, int rc, IntPtr textUtf8);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void ClipboardChangedCallback();

    private static readonly object s_sync = new();
    private static readonly Dictionary<int, TaskCompletionSource<(int Rc, string Text)>> s_pending = new();
    private static ClipboardResultCallback? s_resultCallback;
    private static ClipboardChangedCallback? s_changedCallback;
    private static bool s_registered;
    private static bool s_unavailable;
    private static int s_nextRequestId;

    /// <summary>Raised for every pasteboard 'update' the shell reports.</summary>
    internal static event Action? Changed;

    [ModuleInitializer]
    internal static void Initialize() => Register();

    /// <summary>Registers the native result and change callbacks; a guarded no-op off-device.</summary>
    internal static void Register()
    {
        if (s_registered || s_unavailable)
        {
            return;
        }
        try
        {
            s_resultCallback = OnNativeClipboardResult;
            RegisterResultNative(Marshal.GetFunctionPointerForDelegate(s_resultCallback));
            s_changedCallback = OnNativeClipboardChanged;
            RegisterChangedNative(Marshal.GetFunctionPointerForDelegate(s_changedCallback));
            s_registered = true;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            s_unavailable = true;
        }
    }

    /// <summary>
    /// Runs one clipboard op. Returns (-1, "") when the host library/sink is unavailable, the
    /// request could not be dispatched or the shell did not answer inside
    /// <paramref name="timeout"/>; otherwise the shell's (rc, text) answer.
    /// </summary>
    internal static async Task<(int Rc, string Text)> RequestAsync(int op, string? text, TimeSpan timeout)
    {
        if (!s_registered)
        {
            Register();
            if (s_unavailable)
            {
                return (-1, string.Empty);
            }
        }
        int requestId = Interlocked.Increment(ref s_nextRequestId);
        var completion = new TaskCompletionSource<(int Rc, string Text)>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (s_sync)
        {
            s_pending[requestId] = completion;
        }
        bool dispatched = true;
        try
        {
            RequestNative(requestId, op, text ?? string.Empty);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            dispatched = false;
        }
        if (!dispatched)
        {
            lock (s_sync)
            {
                s_pending.Remove(requestId);
            }
            return (-1, string.Empty);
        }
        Task finished = await Task.WhenAny(completion.Task, Task.Delay(timeout)).ConfigureAwait(false);
        if (finished != completion.Task)
        {
            lock (s_sync)
            {
                s_pending.Remove(requestId);
            }
            return (-1, string.Empty);
        }
        return await completion.Task.ConfigureAwait(false);
    }

    private static void OnNativeClipboardResult(int requestId, int rc, IntPtr textUtf8)
    {
        TaskCompletionSource<(int Rc, string Text)>? completion;
        lock (s_sync)
        {
            if (!s_pending.Remove(requestId, out completion))
            {
                return;
            }
        }
        string text = textUtf8 == IntPtr.Zero
            ? string.Empty
            : Marshal.PtrToStringUTF8(textUtf8) ?? string.Empty;
        completion?.TrySetResult((rc, text));
    }

    // A reverse P/Invoke entry: the push raises the public ClipboardContentChanged event, so an
    // exception must not unwind into the native frame (MB-2).
    private static void OnNativeClipboardChanged()
    {
        try
        {
            Changed?.Invoke();
        }
        catch (Exception ex)
        {
            OpenHarmonyStatus.NativeCallbackFailed("clipboard changed", ex);
        }
    }
}

/// <summary>
/// Network access over the host NDK path plus the shell's network-change push. The value read
/// here is the host's 0 unknown / 1 none / 2 local / 3 internet encoding.
/// </summary>
internal static partial class OpenHarmonyConnectivityBridge
{
    private const string HostLibrary = "libopenharmonyhost.so";

    /// <summary>Returned when the host library or the network getter is unavailable.</summary>
    internal const int Unavailable = -1;

    [LibraryImport(HostLibrary, EntryPoint = "ohos_host_network_access")]
    private static partial int NetworkAccessNative();

    [LibraryImport(HostLibrary, EntryPoint = "ohos_host_network_access_register")]
    private static partial void NetworkAccessRegisterNative(IntPtr callback);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void NetworkAccessCallback(int level);

    private static NetworkAccessCallback? s_callback;
    private static bool s_registered;
    private static bool s_unavailable;

    /// <summary>Raised with the level the host re-read when the shell reported a change.</summary>
    internal static event Action<int>? Changed;

    [ModuleInitializer]
    internal static void Initialize() => Register();

    /// <summary>Registers the native change callback; a guarded no-op off-device.</summary>
    internal static void Register()
    {
        if (s_registered || s_unavailable)
        {
            return;
        }
        try
        {
            s_callback = OnNativeNetworkAccess;
            NetworkAccessRegisterNative(Marshal.GetFunctionPointerForDelegate(s_callback));
            s_registered = true;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            s_unavailable = true;
        }
    }

    /// <summary>The live network level, or <see cref="Unavailable"/> off-device.</summary>
    internal static int ReadNetworkAccess()
    {
        try
        {
            return NetworkAccessNative();
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return Unavailable;
        }
    }

    // A reverse P/Invoke entry: the push raises the public ConnectivityChanged event, so an
    // exception must not unwind into the native frame (MB-2).
    private static void OnNativeNetworkAccess(int level)
    {
        try
        {
            Changed?.Invoke(level);
        }
        catch (Exception ex)
        {
            OpenHarmonyStatus.NativeCallbackFailed("network access", ex);
        }
    }
}
