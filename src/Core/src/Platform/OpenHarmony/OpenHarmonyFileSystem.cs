// IFileSystem for OpenHarmony: app data/cache directories from the ability context, packaged
// files from the extracted HAP payload (the shell publishes FilesDir/dotnet as AppDir), with a
// fallback for assets the HAP ships raw (resources/rawfile/**).
//
// Raw-resource fallback: the payload directory only holds the published dotnet output, so a file
// packed directly under resources/rawfile/** is not a sandbox file at all. The ArkTS shell owns
// those bytes through resourceManager; the managed side asks through the host bridge
// (ohos_host_raw_file_request -> registerRawFileSink). The answer arrives either as raw bytes the
// host preads from a rawfile descriptor (host.notifyRawFileFd -> the bytes callback, preferred:
// no base64 string, no UTF-16 copy) or as one base64 string (host.notifyRawFileResult, the
// fallback when the shell has no usable descriptor or the host could not read it). Neither
// transport uses temp files or shared paths, so there is no cleanup or name-collision race. One
// read is capped at 8 MiB (mirrors OHOS_HOST_RAW_FILE_MAX_BYTES): the shell refuses a bigger file
// with rc=-3 before reading or encoding, the host refuses an over-long base64 argument or
// descriptor the same way, and both callbacks length-check again, so the transient copies stay
// bounded. The helper below degrades quietly off-device (no host library) and never throws from
// the native boundary.
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Microsoft.Maui.Storage;
using Microsoft.OpenHarmony.Hosting;
using System.Runtime.CompilerServices;

namespace Microsoft.Maui.Platform;

public sealed class OpenHarmonyFileSystem : IFileSystem
{
    public string AppDataDirectory => OpenHarmonyPaths.DataDirectory;

    public string CacheDirectory => OpenHarmonyPaths.CacheDirectory;

    public Task<Stream> OpenAppPackageFileAsync(string filename)
    {
        ArgumentException.ThrowIfNullOrEmpty(filename);
        filename = OpenHarmonyPackagePaths.Normalize(filename);
        // Packaged files are the published output the shell extracted from dotnet.zip into the
        // context's AppDir (FilesDir/dotnet); a miss falls back to the HAP's raw resources.
        string path = Path.Combine(OpenHarmonyPaths.AppPackageDirectory, filename);
        if (File.Exists(path))
        {
            return Task.FromResult<Stream>(File.OpenRead(path));
        }
        return OpenRawPackageFileAsync(filename, path);
    }

    public async Task<bool> AppPackageFileExistsAsync(string filename)
    {
        ArgumentException.ThrowIfNullOrEmpty(filename);
        filename = OpenHarmonyPackagePaths.Normalize(filename);
        if (!await AppPackageFileInPayloadAsync(filename).ConfigureAwait(false))
        {
            // Raw HAP resources: the shell distinguishes "not found" from "bridge unavailable",
            // so false here means the file is neither in the payload nor a rawfile.
            return await OpenHarmonyRawFiles.ExistsAsync(filename).ConfigureAwait(false);
        }
        return true;
    }

    // The payload-directory hit test in one named step (the raw fallback lives below).
    private static Task<bool> AppPackageFileInPayloadAsync(string filename)
    {
        return Task.FromResult(File.Exists(Path.Combine(OpenHarmonyPaths.AppPackageDirectory, filename)));
    }

    // One raw-HAP read. A null answer (missing, over cap, no shell sink) keeps the documented
    // FileNotFoundException; the bridge logs its own one-time diagnostics.
    private static async Task<Stream> OpenRawPackageFileAsync(string filename, string path)
    {
        byte[]? data = await OpenHarmonyRawFiles.ReadAsync(filename).ConfigureAwait(false);
        if (data is null)
        {
            throw new FileNotFoundException($"App package file '{filename}' was not found.", path);
        }
        return new MemoryStream(data, writable: false);
    }
}

/// <summary>
/// The one accepted spelling of an app-package file name (MB-3). The name reaches two places
/// that must stay inside the package: the extracted payload directory under
/// <see cref="OpenHarmonyPaths.AppPackageDirectory"/> (Path.Combine) and the shell's
/// resourceManager rawfile reader over the native bridge. The host header describes the
/// managed side as normalizing separators and trimming the leading '/', but trimming alone
/// would still admit ".." traversal and rooted/UNC spellings, so the canonical form defined
/// here normalizes separators and rejects every rooted, ambiguous or escaping name instead: a
/// rooted name would silently discard the package prefix in Path.Combine and a ".." segment
/// would escape it.
/// </summary>
internal static class OpenHarmonyPackagePaths
{
    /// <summary>Longest name the bridge accepts (mirrors the shell's rawfile 4096-character cap).</summary>
    public const int MaxNameLength = 4096;

    /// <summary>
    /// Returns the canonical rawfile-relative name ('/' separators, no leading/trailing
    /// separator, no "." or ".." segments, no control characters); throws
    /// <see cref="ArgumentException"/> for anything rooted, ambiguous or escaping.
    /// </summary>
    public static string Normalize(string filename)
    {
        const string message = "The app package file name must be a relative path inside the package " +
            "(no root, drive letter, '..' segment or control character).";
        string name = filename.Replace('\\', '/');
        if (name.Length == 0 || name.Length > MaxNameLength || name[0] == '/')
        {
            throw new ArgumentException(message, nameof(filename));
        }
        // "C:/x" and "C:x": rooted on Windows and a drive spelling everywhere else.
        if (name.Length > 1 && name[1] == ':')
        {
            throw new ArgumentException(message, nameof(filename));
        }
        var canonical = new System.Text.StringBuilder(name.Length);
        int start = 0;
        while (start <= name.Length)
        {
            int end = name.IndexOf('/', start, StringComparison.Ordinal);
            if (end < 0)
            {
                end = name.Length;
            }
            string segment = name.Substring(start, end - start);
            if (segment.Length == 0 || segment == "..")
            {
                // Empty segments ("a//b", a trailing '/') have no canonical rawfile form;
                // a ".." segment is traversal and is refused outright.
                throw new ArgumentException(message, nameof(filename));
            }
            if (segment != ".")
            {
                if (canonical.Length > 0)
                {
                    canonical.Append('/');
                }
                canonical.Append(segment);
            }
            start = end + 1;
        }
        if (canonical.Length == 0)
        {
            throw new ArgumentException(message, nameof(filename));
        }
        for (int i = 0; i < canonical.Length; i++)
        {
            if (char.IsControl(canonical[i]))
            {
                // The name crosses the bridge as a NUL-terminated UTF-8 string; control
                // characters (especially an embedded NUL) have no place in a rawfile name.
                throw new ArgumentException(message, nameof(filename));
            }
        }
        return canonical.ToString();
    }
}

/// <summary>
/// Managed side of the raw HAP resource bridge: requests are queued with ids and answered by the
/// ArkTS shell's registerRawFileSink handler through the bytes callback (host.notifyRawFileFd,
/// preferred) or ohos_host_raw_file_result (base64 fallback, and the only transport when the
/// shell has no usable rawfile descriptor). Op 0 reads one resources/rawfile/** entry, op 1
/// probes its existence without reading it. rc values mirror ohos_raw_file_rc in
/// openharmony_host.h. When no shell answers (tests, headless, older hosts) every call answers
/// false/null - the host rejects an undispatchable request with rc -1 immediately, and a request
/// that is never answered trips the bounded timeout below.
/// </summary>
internal static partial class OpenHarmonyRawFiles
{
    private const string HostLibrary = "libopenharmonyhost.so";

    // op values; the shell's registerRawFileSink handler switches on them.
    private const int OpRead = 0;
    private const int OpExists = 1;

    // rc values, kept in sync with ohos_raw_file_rc.
    private const int RcOk = 0;
    private const int RcUnavailable = -1;
    private const int RcNotFound = -2;
    private const int RcTooLarge = -3;

    /// <summary>One read is capped at 8 MiB; mirrors OHOS_HOST_RAW_FILE_MAX_BYTES in the host header.</summary>
    public const int MaxBytes = 8 * 1024 * 1024;

    private static readonly TimeSpan s_timeout = TimeSpan.FromSeconds(3);
    private static readonly ConcurrentDictionary<int, TaskCompletionSource<(int Rc, byte[]? Data)>> s_pending = new();
    private static readonly object s_registration = new();

    // ---- answered-request cache -------------------------------------------------------------
    // A packaged asset cannot change while the process runs, so one answered request serves every
    // later one: apps ask for the same assets repeatedly (image source resolution, virtualised
    // list recycling), and each rawfile request is a bridge round trip with a base64 answer. The
    // read cache is bounded by entries and by total bytes (below the 8 MiB read cap), evicts the
    // least recently used entry and never stores a read too large to fit. Existence answers,
    // including the "not found" answer an app probing optional assets gets most of the time, are
    // cached separately with a smaller bound, and a known-missing name skips the read round trip.
    private const int ReadCacheMaxEntries = 16;
    private const int ReadCacheMaxBytes = 12 * 1024 * 1024;
    private const int ExistsCacheMaxEntries = 256;

    private static readonly object s_cacheLock = new();
    private static readonly Dictionary<string, CacheEntry<byte[]>> s_readCache = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, CacheEntry<bool>> s_existsCache = new(StringComparer.Ordinal);
    private static long s_cacheTick;
    private static long s_readCacheBytes;

    private sealed class CacheEntry<T>
    {
        public CacheEntry(T value) => Value = value;

        public T Value { get; set; }

        public long Tick { get; set; }
    }

    /// <summary>Reads one raw HAP resource; null when it is missing or the bridge is unavailable.</summary>
    public static async Task<byte[]?> ReadAsync(string filename)
    {
        if (TryGetCachedRead(filename, out byte[] cached))
        {
            return cached;
        }
        if (TryGetCachedExists(filename, out bool known) && !known)
        {
            // The shell already answered "not found" for this name; no need to ask again.
            return null;
        }
        (int rc, byte[]? data) = await ExecuteAsync(OpRead, filename).ConfigureAwait(false);
        if (rc == RcOk)
        {
            byte[] result = data ?? Array.Empty<byte>();
            StoreRead(filename, result);
            StoreExists(filename, true);
            return result;
        }
        if (rc != RcNotFound)
        {
            LogUnavailableOnce(rc);
        }
        else
        {
            StoreExists(filename, false);
        }
        return null;
    }

    /// <summary>True only when the shell confirmed the rawfile exists (or answered it with bytes).</summary>
    public static async Task<bool> ExistsAsync(string filename)
    {
        if (TryGetCachedExists(filename, out bool cached))
        {
            return cached;
        }
        (int rc, _) = await ExecuteAsync(OpExists, filename).ConfigureAwait(false);
        if (rc == RcOk)
        {
            StoreExists(filename, true);
            return true;
        }
        if (rc != RcNotFound)
        {
            LogUnavailableOnce(rc);
        }
        else
        {
            StoreExists(filename, false);
        }
        return false;
    }

    private static bool TryGetCachedRead(string filename, out byte[] data)
    {
        lock (s_cacheLock)
        {
            if (s_readCache.TryGetValue(filename, out CacheEntry<byte[]>? entry))
            {
                entry.Tick = ++s_cacheTick;
                data = entry.Value;
                return true;
            }
        }
        data = Array.Empty<byte>();
        return false;
    }

    private static bool TryGetCachedExists(string filename, out bool exists)
    {
        lock (s_cacheLock)
        {
            if (s_existsCache.TryGetValue(filename, out CacheEntry<bool>? entry))
            {
                entry.Tick = ++s_cacheTick;
                exists = entry.Value;
                return true;
            }
        }
        exists = false;
        return false;
    }

    private static void StoreRead(string filename, byte[] data)
    {
        if (data.Length == 0 || data.Length > ReadCacheMaxBytes)
        {
            // Nothing to cache, or an entry that would evict every other one.
            return;
        }
        lock (s_cacheLock)
        {
            if (s_readCache.TryGetValue(filename, out CacheEntry<byte[]>? existing))
            {
                existing.Tick = ++s_cacheTick;
                return;
            }
            while (s_readCache.Count >= ReadCacheMaxEntries || s_readCacheBytes + data.Length > ReadCacheMaxBytes)
            {
                if (!EvictOldest(s_readCache, entry => s_readCacheBytes -= entry.Value.Length))
                {
                    break;
                }
            }
            s_readCache[filename] = new CacheEntry<byte[]>(data) { Tick = ++s_cacheTick };
            s_readCacheBytes += data.Length;
        }
    }

    private static void StoreExists(string filename, bool exists)
    {
        lock (s_cacheLock)
        {
            if (s_existsCache.TryGetValue(filename, out CacheEntry<bool>? existing))
            {
                existing.Value = exists;
                existing.Tick = ++s_cacheTick;
                return;
            }
            if (s_existsCache.Count >= ExistsCacheMaxEntries)
            {
                EvictOldest(s_existsCache, null);
            }
            s_existsCache[filename] = new CacheEntry<bool>(exists) { Tick = ++s_cacheTick };
        }
    }

    /// <summary>Removes the least recently used entry; false when the cache is empty.</summary>
    private static bool EvictOldest<T>(Dictionary<string, CacheEntry<T>> cache, Action<CacheEntry<T>>? onEvict)
    {
        string? oldestKey = null;
        long oldestTick = long.MaxValue;
        foreach (KeyValuePair<string, CacheEntry<T>> pair in cache)
        {
            if (pair.Value.Tick < oldestTick)
            {
                oldestTick = pair.Value.Tick;
                oldestKey = pair.Key;
            }
        }
        if (oldestKey is null)
        {
            return false;
        }
        if (onEvict is not null && cache.TryGetValue(oldestKey, out CacheEntry<T>? evicted))
        {
            onEvict(evicted);
        }
        cache.Remove(oldestKey);
        return true;
    }

    private static unsafe IntPtr s_callback = (IntPtr)(delegate* unmanaged[Cdecl]<int, int, IntPtr, void>)&OnRawFileResult;
    private static unsafe IntPtr s_bytesCallback = (IntPtr)(delegate* unmanaged[Cdecl]<int, int, IntPtr, UIntPtr, void>)&OnRawFileBytesResult;
    private static int s_nextId;
    private static bool s_registered;
    private static bool s_unavailable;
    private static bool s_unavailableLogged;

    [LibraryImport(HostLibrary, EntryPoint = "ohos_host_raw_file_request", StringMarshalling = StringMarshalling.Utf8)]
    private static partial int RawFileRequest(int requestId, int op, string name);

    [LibraryImport(HostLibrary, EntryPoint = "ohos_host_raw_file_register_result")]
    private static partial void RawFileRegisterResult(IntPtr callback);

    // The byte transport exists in the same host library as the descriptor notify, but keep the
    // registration optional: a host library built before H7 answers EntryPointNotFound here and
    // the base64 callback above still serves every request.
    [LibraryImport(HostLibrary, EntryPoint = "ohos_host_raw_file_register_result_bytes")]
    private static partial void RawFileRegisterResultBytes(IntPtr callback);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void RawFileResultCallback(int requestId, int rc, IntPtr dataBase64Utf8);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void RawFileBytesCallback(int requestId, int rc, IntPtr data, UIntPtr length);

    private static async Task<(int Rc, byte[]? Data)> ExecuteAsync(int op, string filename)
    {
        if (s_unavailable)
        {
            return (RcUnavailable, null);
        }
        int requestId = Interlocked.Increment(ref s_nextId);
        var source = new TaskCompletionSource<(int Rc, byte[]? Data)>(TaskCreationOptions.RunContinuationsAsynchronously);
        s_pending[requestId] = source;
        try
        {
            EnsureRegistered();
            if (s_unavailable || RawFileRequest(requestId, op, filename) != 0)
            {
                // No shell sink (older shell) or no host export: fail fast instead of waiting
                // out the timeout for every asset, and remember so later calls are free.
                s_pending.TryRemove(requestId, out _);
                s_unavailable = true;
                LogUnavailableOnce(RcUnavailable);
                return (RcUnavailable, null);
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            s_pending.TryRemove(requestId, out _);
            s_unavailable = true;
            LogUnavailableOnce(RcUnavailable);
            return (RcUnavailable, null);
        }
        // WaitAsync's timeout uses the runtime's shared timer queue; the per-request
        // Task.Delay(3s) this replaces allocated a timer plus its task on every request, answered
        // or not (240 B measured), while the success path here allocates nothing for the timeout.
        try
        {
            return await source.Task.WaitAsync(s_timeout).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            s_pending.TryRemove(requestId, out _);
            s_unavailable = true;
            LogUnavailableOnce(RcUnavailable);
            return (RcUnavailable, null);
        }
    }

    private static void EnsureRegistered()
    {
        lock (s_registration)
        {
            if (s_registered || s_unavailable)
            {
                return;
            }
            RawFileRegisterResult(s_callback);
            try
            {
                RawFileRegisterResultBytes(s_bytesCallback);
            }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
            {
                // An older host library has no byte transport; the base64 callback stays the
                // only one and the shell's descriptor notify (also absent there) never runs.
                s_bytesCallback = IntPtr.Zero;
            }
            s_registered = true;
        }
    }

    // Runs on the shell's thread when the host answered a rawfile descriptor read; the host's
    // buffer is only valid for this call, so the bytes are copied out before it returns.
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static void OnRawFileBytesResult(int requestId, int rc, IntPtr data, UIntPtr length)
    {
        if (!s_pending.TryRemove(requestId, out TaskCompletionSource<(int Rc, byte[]? Data)>? source))
        {
            return;
        }
        byte[]? bytes = null;
        if (rc == RcOk)
        {
            ulong size = length.ToUInt64();
            if (size > MaxBytes || (size > 0 && data == IntPtr.Zero))
            {
                rc = size > MaxBytes ? RcTooLarge : RcUnavailable;
            }
            else if (size == 0)
            {
                bytes = Array.Empty<byte>();
            }
            else
            {
                bytes = new byte[(int)size];
                Marshal.Copy(data, bytes, 0, (int)size);
            }
        }
        source.TrySetResult((rc, bytes));
    }

    // Runs on the shell's thread when the ArkTS sink answers; completes the matching request.
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static void OnRawFileResult(int requestId, int rc, IntPtr dataBase64Utf8)
    {
        if (!s_pending.TryRemove(requestId, out TaskCompletionSource<(int Rc, byte[]? Data)>? source))
        {
            return;
        }
        byte[]? data = null;
        if (rc == RcOk)
        {
            try
            {
                string base64 = dataBase64Utf8 == IntPtr.Zero
                    ? string.Empty
                    : Marshal.PtrToStringUTF8(dataBase64Utf8) ?? string.Empty;
                data = base64.Length == 0 ? Array.Empty<byte>() : Convert.FromBase64String(base64);
                if (data.Length > MaxBytes)
                {
                    rc = RcTooLarge;
                    data = null;
                }
            }
            catch (Exception ex) when (ex is FormatException or ArgumentOutOfRangeException)
            {
                // A malformed answer must never surface as a broken stream.
                rc = RcUnavailable;
                data = null;
            }
        }
        source.TrySetResult((rc, data));
    }

    // One line the first time the bridge reports "unavailable" (a missing file is normal and
    // never logged); later calls stay quiet so an app checking many assets cannot flood the log.
    private static void LogUnavailableOnce(int rc)
    {
        if (s_unavailableLogged)
        {
            return;
        }
        s_unavailableLogged = true;
        OpenHarmonyBridge.WriteStatus($"[maui] raw file bridge unavailable (rc={rc})");
    }
}
