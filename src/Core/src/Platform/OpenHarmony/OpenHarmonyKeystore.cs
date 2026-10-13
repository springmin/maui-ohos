// Managed side of the HUKS bridge: requests are queued with ids and completed by the host's
// native keystore engine (or, when the device has no libhuks_ndk.z.so, by the ArkTS sink) through
// ohos_host_keystore_complete. When no answer arrives (tests, headless, older hosts) every call
// fails fast and callers fall back to the file-based implementation.
using System.Collections.Concurrent;
using System.Text;
using Microsoft.OpenHarmony.Hosting;

namespace Microsoft.Maui.Platform;

internal static partial class OpenHarmonyKeystore
{
    private static readonly ConcurrentDictionary<int, TaskCompletionSource<(int Rc, string Data)>> s_pending = new();
    private static int s_nextId;
    private static bool s_unavailable;
    private static KeystoreTestMode s_testMode;

    public static bool IsUnavailable => s_unavailable;

    /// <summary>
    /// Slice-harness seam: the keystore shapes a device cannot be asked to produce on demand,
    /// so the storage contracts (the HUKS-first write, the fail-closed refusal, the documented
    /// fallback) are exercised off-device. <see cref="KeystoreTestMode.Off"/> is the real host
    /// path; the other modes answer every request in memory and never touch the native code.
    /// </summary>
    internal enum KeystoreTestMode
    {
        /// <summary>The real host path (the device probe and the request queue).</summary>
        Off,
        /// <summary>A working keystore: generate/encrypt/decrypt/delete round-trip in memory.</summary>
        Working,
        /// <summary>The capability probe reports HUKS, but every operation is rejected.</summary>
        OperationFailed,
        /// <summary>No keystore: every request fails and the capability probe is false.</summary>
        Unavailable,
    }

    /// <summary>The harness-selected keystore behaviour (see <see cref="KeystoreTestMode"/>).</summary>
    internal static KeystoreTestMode TestMode
    {
        get => s_testMode;
        set => s_testMode = value;
    }

    public static void Complete(int requestId, int rc, string data)
    {
        if (s_pending.TryRemove(requestId, out TaskCompletionSource<(int, string)>? source))
        {
            source.TrySetResult((rc, data));
        }
    }

    public static async Task<byte[]?> EncryptAsync(string alias, byte[] plain, int timeoutMs = 1500)
    {
        string? result = await ExecuteAsync("encrypt", alias, Convert.ToBase64String(plain), timeoutMs);
        return DecodeBase64(result);
    }

    public static async Task<byte[]?> DecryptAsync(string alias, byte[] cipher, int timeoutMs = 1500)
    {
        string? result = await ExecuteAsync("decrypt", alias, Convert.ToBase64String(cipher), timeoutMs);
        return DecodeBase64(result);
    }

    /// <summary>
    /// The shell sink's base64 answer decoded safely: a malformed answer (a broken sink or a
    /// truncated native result) reads as absent instead of throwing out of a storage call.
    /// </summary>
    private static byte[]? DecodeBase64(string? value)
    {
        if (value is null)
        {
            return null;
        }
        try
        {
            return Convert.FromBase64String(value);
        }
        catch (FormatException)
        {
            return null;
        }
    }

    public static async Task<bool> EnsureKeyAsync(string alias, int timeoutMs = 1500)
        => await ExecuteAsync("generate", alias, string.Empty, timeoutMs) is not null;

    public static async Task<bool> DeleteKeyAsync(string alias, int timeoutMs = 1500)
        => await ExecuteAsync("delete", alias, string.Empty, timeoutMs) is not null;

    /// <summary>
    /// True when the host library reports the native HUKS engine (libhuks_ndk.z.so) on this
    /// device: the value key is generated and kept by the system keystore. False (off-device,
    /// reduced image, older host) means the store keeps the documented per-install file key.
    /// The answer is a device capability and is cached for the process lifetime.
    /// </summary>
    public static bool IsAvailable
    {
        get
        {
            switch (s_testMode)
            {
                case KeystoreTestMode.Working:
                case KeystoreTestMode.OperationFailed:
                    return true;
                case KeystoreTestMode.Unavailable:
                    return false;
            }
            if (s_available is bool cached)
            {
                return cached;
            }
            bool available;
            try
            {
                available = KeystoreAvailable() == 1;
            }
            catch
            {
                // No host library (tests/desktop) or a host without the export: there is no
                // keystore, and a status getter never throws.
                available = false;
            }
            s_available = available;
            return available;
        }
    }

    private static bool? s_available;

    private static async Task<string?> ExecuteAsync(string op, string alias, string dataBase64, int timeoutMs)
    {
        switch (s_testMode)
        {
            case KeystoreTestMode.Working:
                return Simulate(op, dataBase64);
            case KeystoreTestMode.OperationFailed:
            case KeystoreTestMode.Unavailable:
                return null;
        }
        if (s_unavailable)
        {
            return null;
        }
        int id = Interlocked.Increment(ref s_nextId);
        var source = new TaskCompletionSource<(int, string)>(TaskCreationOptions.RunContinuationsAsynchronously);
        s_pending[id] = source;
        try
        {
            RequestNative(id, op, alias, dataBase64);
        }
        catch
        {
            s_pending.TryRemove(id, out _);
            s_unavailable = true;
            return null;
        }
        Task completed = await Task.WhenAny(source.Task, Task.Delay(timeoutMs));
        if (completed != source.Task)
        {
            s_pending.TryRemove(id, out _);
            s_unavailable = true;
            return null;
        }
        (int rc, string data) = await source.Task;
        if (rc != 0)
        {
            return null;
        }
        return data;
    }

    /// <summary>
    /// The harness keystore's stand-in cipher: XOR 0x5A, so a sealed payload differs from the
    /// plaintext and round-trips for any input. The device's AES-256-GCM path is exercised on
    /// hardware, not here.
    /// </summary>
    private static string? Simulate(string op, string dataBase64)
    {
        switch (op)
        {
            case "generate":
            case "delete":
                return string.Empty;
            case "encrypt":
            case "decrypt":
                byte[] bytes = Convert.FromBase64String(dataBase64);
                for (int i = 0; i < bytes.Length; i++)
                {
                    bytes[i] ^= 0x5A;
                }
                return Convert.ToBase64String(bytes);
            default:
                return null;
        }
    }

    [System.Runtime.InteropServices.LibraryImport("libopenharmonyhost.so", EntryPoint = "ohos_host_keystore_request", StringMarshalling = System.Runtime.InteropServices.StringMarshalling.Utf8)]
    private static partial void RequestNative(int requestId, string op, string alias, string dataBase64);

    [System.Runtime.InteropServices.LibraryImport("libopenharmonyhost.so", EntryPoint = "ohos_host_keystore_available")]
    private static partial int KeystoreAvailable();
}
