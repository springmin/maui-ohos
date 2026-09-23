// IFileSystem for OpenHarmony: app data/cache directories from the ability context, packaged
// files from the extracted HAP payload (the shell publishes FilesDir/dotnet as AppDir), with a
// fallback for assets the HAP ships raw (resources/rawfile/**).
//
// Raw-resource fallback: the payload directory only holds the published dotnet output, so a file
// packed directly under resources/rawfile/** is not a sandbox file at all. The ArkTS shell owns
// those bytes through resourceManager; the managed side asks through the host bridge
// (ohos_host_raw_file_request -> registerRawFileSink -> host.notifyRawFileResult). The answer
// travels as one base64 string - no temp files or shared paths cross the bridge, so there is no
// cleanup or name-collision race. One read is capped at 8 MiB (mirrors
// OHOS_HOST_RAW_FILE_MAX_BYTES): the shell refuses a bigger file with rc=-3 before encoding, the
// host refuses an over-long base64 argument the same way, and the decode below length-checks
// again, so the transient copies stay bounded. The helper below degrades quietly off-device
// (no host library) and never throws from the native boundary.
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Microsoft.Maui.Storage;
using Microsoft.OpenHarmony.Hosting;

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
/// ArkTS shell's registerRawFileSink handler through ohos_host_raw_file_result. Op 0 reads one
/// resources/rawfile/** entry as base64, op 1 probes its existence without reading it. rc values
/// mirror ohos_raw_file_rc in openharmony_host.h. When no shell answers (tests, headless, older
/// hosts) every call answers false/null - the host rejects an undispatchable request with rc -1
/// immediately, and a request that is never answered trips the bounded timeout below.
/// </summary>
internal static class OpenHarmonyRawFiles
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

    private static RawFileResultCallback? s_callback;
    private static int s_nextId;
    private static bool s_registered;
    private static bool s_unavailable;
    private static bool s_unavailableLogged;

    [DllImport(HostLibrary, EntryPoint = "ohos_host_raw_file_request", CharSet = CharSet.Ansi)]
    private static extern int RawFileRequest(int requestId, int op, string name);

    [DllImport(HostLibrary, EntryPoint = "ohos_host_raw_file_register_result")]
    private static extern void RawFileRegisterResult(IntPtr callback);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void RawFileResultCallback(int requestId, int rc, IntPtr dataBase64Utf8);

    /// <summary>Reads one raw HAP resource; null when it is missing or the bridge is unavailable.</summary>
    public static async Task<byte[]?> ReadAsync(string filename)
    {
        (int rc, byte[]? data) = await ExecuteAsync(OpRead, filename).ConfigureAwait(false);
        if (rc == RcOk)
        {
            return data ?? Array.Empty<byte>();
        }
        if (rc != RcNotFound)
        {
            LogUnavailableOnce(rc);
        }
        return null;
    }

    /// <summary>True only when the shell confirmed the rawfile exists (or answered it with bytes).</summary>
    public static async Task<bool> ExistsAsync(string filename)
    {
        (int rc, _) = await ExecuteAsync(OpExists, filename).ConfigureAwait(false);
        if (rc == RcOk)
        {
            return true;
        }
        if (rc != RcNotFound)
        {
            LogUnavailableOnce(rc);
        }
        return false;
    }

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
        Task completed = await Task.WhenAny(source.Task, Task.Delay(s_timeout)).ConfigureAwait(false);
        if (completed != source.Task)
        {
            s_pending.TryRemove(requestId, out _);
            s_unavailable = true;
            LogUnavailableOnce(RcUnavailable);
            return (RcUnavailable, null);
        }
        return await source.Task.ConfigureAwait(false);
    }

    private static void EnsureRegistered()
    {
        lock (s_registration)
        {
            if (s_registered || s_unavailable)
            {
                return;
            }
            s_callback = OnRawFileResult;
            RawFileRegisterResult(Marshal.GetFunctionPointerForDelegate(s_callback));
            s_registered = true;
        }
    }

    // Runs on the shell's thread when the ArkTS sink answers; completes the matching request.
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
