// Managed side of the Asset Store bridge (SECURE-STORAGE-ASSET): requests are queued with ids
// and completed by the host's Asset Store NDK engine (OH_Asset_*, host_asset.c; libasset_ndk.z.so
// is resolved at runtime) through the registered ohos_host_asset_result callback. The value
// travels base64 and is never logged. When no answer arrives (tests, headless, older hosts)
// every call fails fast and the storage layer keeps its documented strong-chain/refusal shapes.
//
// The test seam (AssetTestMode) gives the shapes a device cannot be asked to produce on demand:
// a working store, a missing store, and a store that accepts reads but rejects writes. Off is
// the real host path; the other modes answer every request in memory and never touch the
// native code.
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Microsoft.Maui.Platform;

internal static partial class OpenHarmonyAssetStore
{
    private const string HostLibrary = "libopenharmonyhost.so";

    /// <summary>Op codes of the host's ohos_host_asset_request protocol.</summary>
    internal const int PutOp = 0;
    internal const int GetOp = 1;
    internal const int RemoveOp = 2;

    /// <summary>Completion codes: 0 success, 1 not found, -1 unavailable or failed.</summary>
    internal const int RcOk = 0;
    internal const int RcNotFound = 1;
    internal const int RcFailed = -1;

    /// <summary>Bounds a lost asset answer; the NDK call is local and fast.</summary>
    internal static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(1.5);

    private static readonly ConcurrentDictionary<int, TaskCompletionSource<(int Rc, string Data)>> s_pending = new();
    private static int s_nextId;
    private static bool s_registered;
    private static bool s_unavailable;
    private static bool? s_available;
    private static AssetTestMode s_testMode;
    // The Working/WriteRejected seam's in-memory store (alias -> base64 secret).
    private static readonly ConcurrentDictionary<string, string> s_simulated = new();

    /// <summary>
    /// Slice-harness seam: the Asset Store shapes a device cannot be asked to produce on
    /// demand, so the storage contracts (the asset-first write, the fail-closed refusal, the
    /// documented fallback) are exercised off-device. <see cref="AssetTestMode.Off"/> is the
    /// real host path; the other modes answer every request in memory.
    /// </summary>
    internal enum AssetTestMode
    {
        /// <summary>The real host path (the availability probe and the request queue).</summary>
        Off,
        /// <summary>A working store: put/get/remove round-trip in memory.</summary>
        Working,
        /// <summary>No store: every request fails and the availability probe is false.</summary>
        Unavailable,
        /// <summary>The store answers reads but rejects every write.</summary>
        WriteRejected,
    }

    /// <summary>The harness-selected Asset Store behaviour (see <see cref="AssetTestMode"/>).</summary>
    internal static AssetTestMode TestMode
    {
        get => s_testMode;
        set => s_testMode = value;
    }

    /// <summary>
    /// Harness bookkeeping: the number of entries the simulated store currently holds (0 on the
    /// real host path), so the suite can prove Remove/RemoveAll actually dropped the store rows
    /// and not just the file index.
    /// </summary>
    internal static int SimulatedEntryCount => s_simulated.Count;

    /// <summary>
    /// True when this device exposes the Asset Store credential backend (libasset_ndk.z.so
    /// resolved by the host). The answer is a device capability and is cached for the process
    /// lifetime; off-device and on reduced images it is false.
    /// </summary>
    internal static bool IsAvailable
    {
        get
        {
            switch (s_testMode)
            {
                case AssetTestMode.Working:
                case AssetTestMode.WriteRejected:
                    return true;
                case AssetTestMode.Unavailable:
                    return false;
            }
            if (s_available is bool cached)
            {
                return cached;
            }
            bool available;
            try
            {
                available = AssetAvailable() == 1;
            }
            catch
            {
                // No host library (tests/desktop) or a host without the export: there is no
                // asset store, and a status getter never throws.
                available = false;
            }
            s_available = available;
            return available;
        }
    }

    public static void Complete(int requestId, int rc, string data)
    {
        if (s_pending.TryRemove(requestId, out TaskCompletionSource<(int, string)>? source))
        {
            source.TrySetResult((rc, data));
        }
    }

    /// <summary>Stores/overwrites the base64 secret under the alias. True on success.</summary>
    public static async Task<bool> PutAsync(string alias, string dataBase64, int timeoutMs = 1500)
        => (await ExecuteAsync(PutOp, alias, dataBase64, timeoutMs).ConfigureAwait(false)).Rc == RcOk;

    /// <summary>Reads the alias' base64 secret; the completion carries (rc, data).</summary>
    public static async Task<(int Rc, string Data)> GetAsync(string alias, int timeoutMs = 1500)
        => await ExecuteAsync(GetOp, alias, string.Empty, timeoutMs).ConfigureAwait(false);

    /// <summary>Removes the alias' entry; the completion carries (rc, "").</summary>
    public static async Task<int> RemoveAsync(string alias, int timeoutMs = 1500)
        => (await ExecuteAsync(RemoveOp, alias, string.Empty, timeoutMs).ConfigureAwait(false)).Rc;

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
            RegisterResultNative(s_resultCallback);
            s_registered = true;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            s_unavailable = true;
        }
    }

    private static async Task<(int Rc, string Data)> ExecuteAsync(int op, string alias,
        string dataBase64, int timeoutMs)
    {
        switch (s_testMode)
        {
            case AssetTestMode.Working:
                return Simulate(op, alias, dataBase64);
            case AssetTestMode.WriteRejected:
                return op == PutOp ? (RcFailed, string.Empty) : Simulate(op, alias, dataBase64);
            case AssetTestMode.Unavailable:
                return (RcFailed, string.Empty);
        }
        if (!s_registered)
        {
            Register();
            if (s_unavailable)
            {
                return (RcFailed, string.Empty);
            }
        }
        int id = Interlocked.Increment(ref s_nextId);
        var source = new TaskCompletionSource<(int Rc, string Data)>(TaskCreationOptions.RunContinuationsAsynchronously);
        s_pending[id] = source;
        try
        {
            RequestNative(id, op, alias, dataBase64);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            s_pending.TryRemove(id, out _);
            s_unavailable = true;
            return (RcFailed, string.Empty);
        }
        Task completed = await Task.WhenAny(source.Task, Task.Delay(timeoutMs)).ConfigureAwait(false);
        if (completed != source.Task)
        {
            s_pending.TryRemove(id, out _);
            return (RcFailed, string.Empty);
        }
        return await source.Task.ConfigureAwait(false);
    }

    /// <summary>
    /// The harness store: the in-memory round trip of the three ops (remove reports whether an
    /// entry existed, mirroring the host's rc 0 / 1 split). The WriteRejected mode rejects the
    /// put above and serves the reads from here.
    /// </summary>
    private static (int Rc, string Data) Simulate(int op, string alias, string dataBase64)
    {
        switch (op)
        {
            case PutOp:
                s_simulated[alias] = dataBase64;
                return (RcOk, string.Empty);
            case GetOp:
                return s_simulated.TryGetValue(alias, out string? value)
                    ? (RcOk, value)
                    : (RcNotFound, string.Empty);
            case RemoveOp:
                return s_simulated.TryRemove(alias, out _) ? (RcOk, string.Empty) : (RcNotFound, string.Empty);
            default:
                return (RcFailed, string.Empty);
        }
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void AssetResultCallback(int requestId, int rc, IntPtr dataUtf8);

    private static unsafe IntPtr s_resultCallback = (IntPtr)(delegate* unmanaged[Cdecl]<int, int, IntPtr, void>)&OnNativeAssetResult;

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static void OnNativeAssetResult(int requestId, int rc, IntPtr dataUtf8)
    {
        string data = dataUtf8 == IntPtr.Zero ? string.Empty : Marshal.PtrToStringUTF8(dataUtf8) ?? string.Empty;
        Complete(requestId, rc, data);
    }

    [LibraryImport(HostLibrary, EntryPoint = "ohos_host_asset_request", StringMarshalling = StringMarshalling.Utf8)]
    private static partial void RequestNative(int requestId, int op, string alias, string dataBase64);

    [LibraryImport(HostLibrary, EntryPoint = "ohos_host_asset_register_result")]
    private static partial void RegisterResultNative(IntPtr callback);

    [LibraryImport(HostLibrary, EntryPoint = "ohos_host_asset_available")]
    private static partial int AssetAvailable();
}
