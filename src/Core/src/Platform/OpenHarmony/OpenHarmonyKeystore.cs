// Managed side of the HUKS bridge: requests are queued with ids and completed by the ArkTS
// sink through ohos_host_keystore_complete. When no sink answers (tests, headless, older
// hosts) every call fails fast and callers fall back to the file-based implementation.
using System.Collections.Concurrent;
using System.Text;
using Microsoft.OpenHarmony.Hosting;

namespace Microsoft.Maui.Platform;

internal static class OpenHarmonyKeystore
{
    private static readonly ConcurrentDictionary<int, TaskCompletionSource<(int Rc, string Data)>> s_pending = new();
    private static int s_nextId;
    private static bool s_unavailable;

    public static bool IsUnavailable => s_unavailable;

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
        return result is null ? null : Convert.FromBase64String(result);
    }

    public static async Task<byte[]?> DecryptAsync(string alias, byte[] cipher, int timeoutMs = 1500)
    {
        string? result = await ExecuteAsync("decrypt", alias, Convert.ToBase64String(cipher), timeoutMs);
        return result is null ? null : Convert.FromBase64String(result);
    }

    public static async Task<bool> EnsureKeyAsync(string alias, int timeoutMs = 1500)
        => await ExecuteAsync("generate", alias, string.Empty, timeoutMs) is not null;

    private static async Task<string?> ExecuteAsync(string op, string alias, string dataBase64, int timeoutMs)
    {
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

    [System.Runtime.InteropServices.DllImport("libopenharmonyhost.so", EntryPoint = "ohos_host_keystore_request")]
    private static extern void RequestNative(int requestId, string op, string alias, string dataBase64);
}
