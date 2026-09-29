// File and media pickers: the managed side asks the ArkTS shell to open the system picker and
// receives the chosen file's name and bytes (base64). Everything else stays managed.
//
// Request wire format (the shell's Index.ets registerPickerSink decodes it): the single int kind
// keeps its original meaning for 0..4 - 0 documents, 1 photos, 2 videos, 3 capture photo,
// 4 capture video - and is used for every single-select request. A multi-select request sets bit
// 3 and carries the requested maxSelectNumber in the upper bits (0 = the picker maximum, 500):
// documents open DocumentViewPicker, photos/videos open the photoAccessHelper PhotoViewPicker
// (media-only UI) and 3/4 keep the separate camera-capture path.
//
// Result framing: a single pick answers with one rc=0 (name, base64) or rc=-1 (cancel);
// a multi pick answers with one rc=0 per file followed by rc=1 (end marker). The accumulator
// below completes on the marker (or early once the requested count arrived) and answers an
// empty result on rc=-1/timeout, so the existing single-file callers keep their behaviour.
//
// Selection counts in the MAUI 11 contract: MediaPickerOptions.SelectionLimit covers photos and
// videos (default 1, 0 = no limit), while PickOptions (IFilePicker) carries no count at all - so
// IFilePicker.PickMultipleAsync uses the picker maximum and the platform-only overload
// OpenHarmonyFilePicker.PickMultipleAsync(PickOptions?, int) exposes an explicit limit.
using System.Collections.Concurrent;
using Microsoft.Maui.Media;
using Microsoft.Maui.Storage;
using Microsoft.OpenHarmony.Hosting;

namespace Microsoft.Maui.Platform;

internal static partial class OpenHarmonyPickerClient
{
    [System.Runtime.InteropServices.LibraryImport("libopenharmonyhost.so", EntryPoint = "ohos_host_get_app_context")]
    private static partial IntPtr HostProbe();

    private static bool? _deviceAvailable;

    /// <summary>True when running on a device (the host library is loadable).</summary>
    public static bool IsDeviceAvailable
    {
        get
        {
            if (_deviceAvailable is { } known)
            {
                return known;
            }
            try
            {
                _ = HostProbe();
                _deviceAvailable = true;
            }
            catch (Exception)
            {
                _deviceAvailable = false;
            }
            return _deviceAvailable.Value;
        }
    }

    /// <summary>Shell picker modes (the low bits of the request kind).</summary>
    public const int ModeDocument = 0;
    public const int ModePhoto = 1;
    public const int ModeVideo = 2;
    public const int ModeCapturePhoto = 3;
    public const int ModeCaptureVideo = 4;

    /// <summary>DocumentViewPicker/PhotoViewPicker maximum (their SDK contracts allow 1..500).</summary>
    public const int MaxSelectNumber = 500;

    private const int MultiFlag = 0x8;

    /// <summary>Packs a picker mode and a selection limit for the shell. A limit &lt;= 1 keeps
    /// the legacy single-select kind (older shells behave exactly as before); 0 means the picker
    /// maximum and any other value is the requested maxSelectNumber.</summary>
    public static int PackKind(int mode, int maxSelectNumber)
        => maxSelectNumber <= 1
            ? mode
            : mode | MultiFlag | (Math.Min(maxSelectNumber, MaxSelectNumber) << 4);

    private sealed class PendingPick
    {
        public PendingPick(bool multi, int requestedCount)
        {
            Multi = multi;
            RequestedCount = requestedCount;
            Source = new TaskCompletionSource<List<(string Name, byte[] Data)>>(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        public bool Multi { get; }

        /// <summary>The explicit limit for a multi pick, 0 when there is none (picker maximum).</summary>
        public int RequestedCount { get; }

        public TaskCompletionSource<List<(string Name, byte[] Data)>> Source { get; }

        public List<(string Name, byte[] Data)> Items { get; } = new();
    }

    private static readonly ConcurrentDictionary<int, PendingPick> s_pending = new();
    private static int s_nextId;
    private static bool s_unavailable;

    /// <summary>
    /// Answers a pending picker request. rc &lt; 0 cancels, rc == 0 carries a file (one call per
    /// file for a multi pick) and rc &gt; 0 is the shell's multi-pick end marker.
    /// </summary>
    public static void Complete(int requestId, int rc, string name, string dataBase64)
    {
        if (!s_pending.TryGetValue(requestId, out PendingPick? pick))
        {
            return;
        }
        if (rc < 0)
        {
            Finish(requestId, pick);
            return;
        }
        if (rc == 0 && dataBase64.Length > 0)
        {
            byte[] data;
            try
            {
                data = Convert.FromBase64String(dataBase64);
            }
            catch (FormatException)
            {
                data = Array.Empty<byte>();
            }
            if (data.Length > 0)
            {
                lock (pick.Items)
                {
                    pick.Items.Add((name, data));
                }
            }
        }
        bool done;
        lock (pick.Items)
        {
            // A single pick completes on its first answer; a multi pick waits for the shell's
            // end marker, with an early completion once the requested count was delivered.
            done = !pick.Multi || rc > 0 || (pick.RequestedCount > 0 && pick.Items.Count >= pick.RequestedCount);
        }
        if (done)
        {
            Finish(requestId, pick);
        }
    }

    private static void Finish(int requestId, PendingPick pick)
    {
        s_pending.TryRemove(requestId, out _);
        List<(string Name, byte[] Data)> items;
        lock (pick.Items)
        {
            items = new List<(string Name, byte[] Data)>(pick.Items);
        }
        pick.Source.TrySetResult(items);
    }

    /// <summary>Picks files through the shell. maxSelectNumber: 1 = single pick, 0 = the picker
    /// maximum, n &gt; 1 = an explicit multi-select limit.</summary>
    public static async Task<List<FileResult>> PickFilesAsync(int mode, int maxSelectNumber, string fallbackName, int timeoutMs = 180000)
    {
        bool multi = maxSelectNumber != 1;
        List<(string Name, byte[] Data)> picked = await PickAllAsync(
            PackKind(mode, maxSelectNumber), multi, multi ? maxSelectNumber : 0, timeoutMs);
        var results = new List<FileResult>();
        foreach ((string name, byte[] data) in picked)
        {
            FileResult? result = WriteResult(name, data, fallbackName);
            if (result is not null)
            {
                results.Add(result);
            }
        }
        return results;
    }

    private static async Task<List<(string Name, byte[] Data)>> PickAllAsync(int kind, bool multi, int requestedCount, int timeoutMs)
    {
        if (s_unavailable)
        {
            return new List<(string, byte[])>();
        }
        int id = Interlocked.Increment(ref s_nextId);
        var pick = new PendingPick(multi, requestedCount);
        s_pending[id] = pick;
        try
        {
            OpenHarmonyBridge.RequestPicker(id, kind);
        }
        catch
        {
            s_pending.TryRemove(id, out _);
            s_unavailable = true;
            return new List<(string, byte[])>();
        }
        Task completed = await Task.WhenAny(pick.Source.Task, Task.Delay(timeoutMs));
        if (completed != pick.Source.Task)
        {
            s_pending.TryRemove(id, out _);
            return new List<(string, byte[])>();
        }
        return await pick.Source.Task;
    }

    private static FileResult? WriteResult(string name, byte[] data, string fallbackName)
    {
        if (data.Length == 0)
        {
            return null;
        }
        string chosen = string.IsNullOrEmpty(name) ? fallbackName : name;
        try
        {
            Directory.CreateDirectory(OpenHarmonyPaths.CacheDirectory);
            string path = UniquePath(chosen);
            File.WriteAllBytes(path, data);
            return new FileResult(path);
        }
        catch (Exception ex)
        {
            OpenHarmonyBridge.WriteStatus($"[maui] picker write failed: {ex.GetType().Name}");
            return null;
        }
    }

    // The cache directory is shared by every pick, so a second selection with the same file
    // name (or a repeated pick of one file) must not overwrite an earlier result.
    private static string UniquePath(string name)
    {
        string path = Path.Combine(OpenHarmonyPaths.CacheDirectory, name);
        if (!File.Exists(path))
        {
            return path;
        }
        string extension = Path.GetExtension(name);
        string stem = Path.GetFileNameWithoutExtension(name);
        for (int i = 2; i < 10000; i++)
        {
            string candidate = Path.Combine(OpenHarmonyPaths.CacheDirectory, $"{stem}-{i}{extension}");
            if (!File.Exists(candidate))
            {
                return candidate;
            }
        }
        return path;
    }
}

public sealed class OpenHarmonyFilePicker : IFilePicker
{
    public async Task<FileResult?> PickAsync(PickOptions? options = null)
        => (await OpenHarmonyPickerClient.PickFilesAsync(OpenHarmonyPickerClient.ModeDocument, 1, "picked-file")).FirstOrDefault();

    /// <summary>Picks documents with the picker's maximum selection (MAUI's PickOptions carries
    /// no selection count to forward).</summary>
    public async Task<IEnumerable<FileResult>> PickMultipleAsync(PickOptions? options = null)
        => await OpenHarmonyPickerClient.PickFilesAsync(OpenHarmonyPickerClient.ModeDocument, 0, "picked-file");

    /// <summary>OpenHarmony-only overload: multi-select documents with an explicit limit
    /// (0 = the picker maximum, 500). IFilePicker cannot carry this because MAUI's PickOptions
    /// has no count; the instance registered for IFilePicker is this type, so a caller can cast
    /// to <see cref="OpenHarmonyFilePicker"/> when it needs the count.</summary>
    public async Task<IReadOnlyList<FileResult>> PickMultipleAsync(PickOptions? options, int maxSelectNumber)
        => await OpenHarmonyPickerClient.PickFilesAsync(OpenHarmonyPickerClient.ModeDocument, maxSelectNumber, "picked-file");
}

public sealed class OpenHarmonyMediaPicker : IMediaPicker
{
    public bool IsCaptureSupported => OpenHarmonyPickerClient.IsDeviceAvailable;

    // The single-pick overloads ignore MediaPickerOptions.SelectionLimit (the MAUI contract says
    // it has no effect there); the multi-pick overloads forward it: 0 = the picker maximum.
    public async Task<FileResult?> PickPhotoAsync(MediaPickerOptions? options = null)
        => (await OpenHarmonyPickerClient.PickFilesAsync(OpenHarmonyPickerClient.ModePhoto, 1, "picked-photo")).FirstOrDefault();

    public async Task<FileResult?> PickVideoAsync(MediaPickerOptions? options = null)
        => (await OpenHarmonyPickerClient.PickFilesAsync(OpenHarmonyPickerClient.ModeVideo, 1, "picked-video")).FirstOrDefault();

    public async Task<List<FileResult>> PickPhotosAsync(MediaPickerOptions? options = null)
        => await OpenHarmonyPickerClient.PickFilesAsync(OpenHarmonyPickerClient.ModePhoto, SelectionCount(options), "picked-photo");

    public async Task<List<FileResult>> PickVideosAsync(MediaPickerOptions? options = null)
        => await OpenHarmonyPickerClient.PickFilesAsync(OpenHarmonyPickerClient.ModeVideo, SelectionCount(options), "picked-video");

    public async Task<FileResult?> CapturePhotoAsync(MediaPickerOptions? options = null)
        => OpenHarmonyPickerClient.IsDeviceAvailable
            ? (await OpenHarmonyPickerClient.PickFilesAsync(OpenHarmonyPickerClient.ModeCapturePhoto, 1, "captured-photo")).FirstOrDefault()
            : null;

    public async Task<FileResult?> CaptureVideoAsync(MediaPickerOptions? options = null)
        => OpenHarmonyPickerClient.IsDeviceAvailable
            ? (await OpenHarmonyPickerClient.PickFilesAsync(OpenHarmonyPickerClient.ModeCaptureVideo, 1, "captured-video")).FirstOrDefault()
            : null;

    private static int SelectionCount(MediaPickerOptions? options)
    {
        int limit = options?.SelectionLimit ?? 1;
        return limit <= 0 ? 0 : limit; // 0 (or an unset/negative value) = the picker maximum
    }
}
