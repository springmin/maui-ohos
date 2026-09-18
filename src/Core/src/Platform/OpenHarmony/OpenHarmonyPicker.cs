// File and media pickers: the managed side asks the ArkTS shell to open the system picker and
// receives the chosen file's name and bytes (base64). Everything else stays managed.
using System.Collections.Concurrent;
using Microsoft.Maui.Media;
using Microsoft.Maui.Storage;
using Microsoft.OpenHarmony.Hosting;

namespace Microsoft.Maui.Platform;

internal static class OpenHarmonyPickerClient
{
    private static readonly ConcurrentDictionary<int, TaskCompletionSource<(string Name, byte[] Data)>> s_pending = new();
    private static int s_nextId;
    private static bool s_unavailable;

    public static void Complete(int requestId, int rc, string name, string dataBase64)
    {
        if (s_pending.TryRemove(requestId, out TaskCompletionSource<(string, byte[])>? source))
        {
            byte[] data = rc == 0 && dataBase64.Length > 0 ? Convert.FromBase64String(dataBase64) : Array.Empty<byte>();
            source.TrySetResult((name, data));
        }
    }

    public static async Task<(string Name, byte[] Data)?> PickAsync(int kind, int timeoutMs = 180000)
    {
        if (s_unavailable)
        {
            return null;
        }
        int id = Interlocked.Increment(ref s_nextId);
        var source = new TaskCompletionSource<(string, byte[])>((TaskCreationOptions)(1 << 0));
        s_pending[id] = source;
        try
        {
            OpenHarmonyBridge.RequestPicker(id, kind);
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
            return null;
        }
        (string name, byte[] data) = await source.Task;
        return data.Length > 0 ? (name, data) : null;
    }

    public static async Task<FileResult?> PickFileAsync(int kind, PickOptions? options, string fallbackName)
    {
        if (await PickAsync(kind) is not { } picked)
        {
            return null;
        }
        string name = string.IsNullOrEmpty(picked.Name) ? fallbackName : picked.Name;
        string path = Path.Combine(OpenHarmonyPaths.CacheDirectory, name);
        try
        {
            Directory.CreateDirectory(OpenHarmonyPaths.CacheDirectory);
            File.WriteAllBytes(path, picked.Data);
        }
        catch (Exception ex)
        {
            OpenHarmonyBridge.WriteStatus($"[maui] picker write failed: {ex.GetType().Name}");
            return null;
        }
        return new FileResult(path);
    }
}

public sealed class OpenHarmonyFilePicker : IFilePicker
{
    public async Task<FileResult?> PickAsync(PickOptions? options = null)
        => await OpenHarmonyPickerClient.PickFileAsync(0, options, "picked-file");

    public async Task<IEnumerable<FileResult>> PickMultipleAsync(PickOptions? options = null)
    {
        FileResult? single = await PickAsync(options);
        return single is null ? Array.Empty<FileResult>() : new[] { single };
    }
}

public sealed class OpenHarmonyMediaPicker : IMediaPicker
{
    public bool IsCaptureSupported => false;

    public Task<FileResult?> PickPhotoAsync(MediaPickerOptions? options = null)
        => OpenHarmonyPickerClient.PickFileAsync(1, null, "picked-photo");

    public Task<FileResult?> PickVideoAsync(MediaPickerOptions? options = null)
        => OpenHarmonyPickerClient.PickFileAsync(2, null, "picked-video");

    public Task<List<FileResult>> PickPhotosAsync(MediaPickerOptions? options = null)
        => PickManyAsync(1, "picked-photo");

    public Task<List<FileResult>> PickVideosAsync(MediaPickerOptions? options = null)
        => PickManyAsync(2, "picked-video");

    public Task<FileResult?> CapturePhotoAsync(MediaPickerOptions? options = null)
        => throw new FeatureNotSupportedException("Camera capture needs a camera component in the ArkTS shell");

    public Task<FileResult?> CaptureVideoAsync(MediaPickerOptions? options = null)
        => throw new FeatureNotSupportedException("Camera capture needs a camera component in the ArkTS shell");

    private static async Task<List<FileResult>> PickManyAsync(int kind, string fallbackName)
    {
        FileResult? single = await OpenHarmonyPickerClient.PickFileAsync(kind, null, fallbackName);
        return single is null ? new List<FileResult>() : new List<FileResult> { single };
    }
}
