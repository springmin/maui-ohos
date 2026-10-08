// Screenshot (Microsoft.Maui.Media.IScreenshot) for OpenHarmony over the host capture contract
//   int ohos_host_screenshot(const char* out_path)
//   int ohos_host_screenshot_format(const char* out_path, int format, int quality)
// The host asks the ArkTS shell to snapshot the main window; the shell's image packer writes
// either a PNG (format 0) or a JPEG at the requested quality (format 1) to out_path. The first
// export is the frozen PNG contract (C1 implements the host + shell half in parallel) and is
// still the one CaptureAsync uses; the second is the format-aware addition for Jpeg reads. The
// managed side picks a unique path under Path.GetTempPath(), lets the host write it, reads the
// image into memory, deletes the temp file in a finally block (also when reading fails) and
// returns an in-memory result, so no temp file outlives the call. Success is decided by the
// produced file (non-empty and complete), not the return code, so a host half that only signals
// through the file still works; the code is logged on failure.
//
// IsCaptureSupported asks the host directly (SEC7 pre-probe sweep): the frozen export is called
// with an empty output path, which the host rejects before it queues a snapshot, so the probe has
// no side effect; a missing library/export (DllNotFound/EntryPointNotFound) is cached as false.
// Off-device the property is false and CaptureAsync returns null without throwing, like the
// reference implementation's "not supported" path.
//
// ScreenshotFormat/quality: CaptureAsync takes the PNG (the default format) and Png reads hand
// those bytes back. A Jpeg read asks the shell for a JPEG through ohos_host_screenshot_format
// with the requested quality (0-100, clamped); the encoded bytes are cached on the result per
// quality, so a repeated Jpeg read does not re-capture. A host that predates the format export
// (or a failed JPEG write) falls back to the captured PNG bytes, noted once through the status
// channel (OpenHarmonyScreenshotResult.LogJpegFallbackOnce) instead of silently claiming a
// conversion.
//
// The shell writes the image asynchronously after the host queued the request (the host only
// reports that the request reached the shell sink), so the managed side polls the target path
// until the file is complete - PNG signature/IHDR/IEND or the JPEG SOI/EOI markers - or
// CaptureTimeout elapses. The polling is async (Task.Delay), so a UI-thread caller is not
// blocked.
using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Maui.Media;
using Microsoft.OpenHarmony.Hosting;

namespace Microsoft.Maui.Platform;

/// <summary>The host capture entry point: ohos_host_screenshot(out_path) writes a PNG.</summary>
internal static partial class OpenHarmonyScreenshotBridge
{
    private const string HostLibrary = "libopenharmonyhost.so";
    private const string EntryPoint = "ohos_host_screenshot";
    private const string FormatEntryPoint = "ohos_host_screenshot_format";

    /// <summary>The format value ohos_host_screenshot_format takes for a PNG.</summary>
    internal const int FormatPng = 0;

    /// <summary>The format value ohos_host_screenshot_format takes for a JPEG.</summary>
    internal const int FormatJpeg = 1;

    [LibraryImport(HostLibrary, EntryPoint = EntryPoint, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int ScreenshotNative(string outPath);

    [LibraryImport(HostLibrary, EntryPoint = FormatEntryPoint, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int ScreenshotFormatNative(string outPath, int format, int quality);

    private static int s_available;   // 0 unknown, 1 exported, -1 missing (cached from the direct call)

    /// <summary>True when the host library exports ohos_host_screenshot. SEC7 pre-probe sweep:
    /// the export is called directly with an empty output path - the host rejects it before it
    /// queues a snapshot, so there is no capture side effect - instead of asking the managed
    /// loader, whose misreport could hide a served export; a missing library/export is cached.</summary>
    internal static bool IsAvailable
    {
        get
        {
            int known = Volatile.Read(ref s_available);
            if (known != 0)
            {
                return known > 0;
            }
            try
            {
                // Side-effect-free probe: any return (the -1 empty-path rejection included)
                // proves the export exists; only a load/lookup failure means it does not.
                ScreenshotNative(string.Empty);
                Volatile.Write(ref s_available, 1);
                return true;
            }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
            {
                Volatile.Write(ref s_available, -1);
                return false;
            }
            catch (Exception)
            {
                Volatile.Write(ref s_available, -1);
                return false;
            }
        }
    }

    // 0 unknown, 1 the format export answered a call, -1 missing (cached from the direct call in
    // CaptureFormatToFile below; the Jpeg read then keeps the PNG fallback without retrying).
    private static int s_formatAvailable;

    /// <summary>
    /// Asks the host to write a PNG to <paramref name="outPath"/>. Returns the host code, or -1
    /// when the library/export is unavailable (the caller checks the file for success).
    /// </summary>
    internal static int CaptureToFile(string outPath)
    {
        try
        {
            return ScreenshotNative(outPath);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            Volatile.Write(ref s_available, -1);
            return -1;
        }
    }

    /// <summary>
    /// Asks the host to write the requested format/quality to <paramref name="outPath"/>
    /// (format 0 = PNG, format 1 = JPEG). SEC7 pre-probe sweep: the export is called directly
    /// (the first call is the probe) and a missing library/export is cached, so the caller's PNG
    /// fallback still happens without a managed-loader pre-check that could hide a served export.
    /// Returns the host code, or -1 when the export is unavailable.
    /// </summary>
    internal static int CaptureFormatToFile(string outPath, int format, int quality)
    {
        if (Volatile.Read(ref s_formatAvailable) < 0)
        {
            return -1;
        }
        try
        {
            int rc = ScreenshotFormatNative(outPath, format, quality);
            Volatile.Write(ref s_formatAvailable, 1);
            return rc;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            Volatile.Write(ref s_formatAvailable, -1);
            return -1;
        }
    }
}

/// <summary>MAUI Essentials screenshot on OpenHarmony (host PNG capture, in-memory result).</summary>
public sealed class OpenHarmonyScreenshot : IScreenshot
{
    public static readonly OpenHarmonyScreenshot Instance = new();

    /// <summary>
    /// How long the shell's asynchronous PNG write may take after the host queued the request
    /// (the host returns as soon as the sink accepted it). Generous: a window snapshot is fast.
    /// </summary>
    internal static readonly TimeSpan CaptureTimeout = TimeSpan.FromSeconds(5);

    /// <summary>Poll cadence while waiting for the shell's PNG write.</summary>
    internal static readonly TimeSpan CapturePollInterval = TimeSpan.FromMilliseconds(25);

    /// <summary>
    /// Test seam: when set, replaces the host JPEG capture with a fake. The interaction harness
    /// runs off-device (no signed host/shell), so the Jpeg path is asserted against this hook.
    /// </summary>
    internal static Func<int, Task<byte[]?>>? JpegCaptureOverride { get; set; }

    public bool IsCaptureSupported => OpenHarmonyScreenshotBridge.IsAvailable;

    public async Task<IScreenshotResult> CaptureAsync()
    {
        if (!IsCaptureSupported)
        {
            return null!;
        }
        string path = Path.Combine(
            Path.GetTempPath(),
            "maui-ohos-screenshot-" + Guid.NewGuid().ToString("N") + ".png");
        try
        {
            int rc = OpenHarmonyScreenshotBridge.CaptureToFile(path);
            if (rc != 0)
            {
                // The host returns 0 when the request reached the shell sink and -1 when there
                // is none (or the path is invalid); nothing will create the file in that case,
                // so this is a fast failure without polling.
                OpenHarmonyBridge.WriteStatus($"[maui] screenshot request was not queued (host rc={rc})");
                return null!;
            }
            byte[]? png = null;
            var watch = Stopwatch.StartNew();
            while (watch.Elapsed < CaptureTimeout)
            {
                png = TryReadCompletePng(path);
                if (png is not null)
                {
                    break;
                }
                await Task.Delay(CapturePollInterval).ConfigureAwait(false);
            }
            if (png is null)
            {
                OpenHarmonyBridge.WriteStatus(
                    $"[maui] screenshot file was not written within {(int)CaptureTimeout.TotalMilliseconds} ms");
                return null!;
            }
            (int width, int height) = ReadPngSize(png);
            return new OpenHarmonyScreenshotResult(png, width, height);
        }
        catch (Exception ex)
        {
            OpenHarmonyBridge.WriteStatus($"[maui] screenshot capture failed: {ex.GetType().Name}");
            return null!;
        }
        finally
        {
            // The bytes are in memory now; the temp file never outlives this call.
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch (Exception)
            {
                // A failed cleanup must not fail the capture.
            }
        }
    }

    /// <summary>
    /// Reads the captured file only when it is a complete PNG: the 8-byte signature, a readable
    /// IHDR size and the trailing IEND chunk (a partial asynchronous write fails the last check,
    /// so the poll loop retries instead of returning truncated bytes).
    /// </summary>
    internal static byte[]? TryReadCompletePng(string path)
    {
        byte[] bytes;
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }
            bytes = File.ReadAllBytes(path);
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
        if (bytes.Length < 24)
        {
            return null;
        }
        (int width, int height) = ReadPngSize(bytes);
        if (width <= 0 || height <= 0)
        {
            return null;
        }
        ReadOnlySpan<byte> trailer = bytes.AsSpan(bytes.Length - 12);
        bool iend = trailer[0] == 0 && trailer[1] == 0 && trailer[2] == 0 && trailer[3] == 0 &&
            trailer[4] == (byte)'I' && trailer[5] == (byte)'E' && trailer[6] == (byte)'N' && trailer[7] == (byte)'D' &&
            trailer[8] == 0xAE && trailer[9] == 0x42 && trailer[10] == 0x60 && trailer[11] == 0x82;
        return iend ? bytes : null;
    }

    /// <summary>
    /// Width/height from the PNG IHDR chunk (signature + "IHDR" + big-endian width/height).
    /// 0x0 for anything that is not a PNG header; the capture is PNG, so this is the cheap path.
    /// </summary>
    internal static (int Width, int Height) ReadPngSize(byte[] bytes)
    {
        ReadOnlySpan<byte> signature = stackalloc byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };
        if (bytes.Length < 24 ||
            !bytes.AsSpan(0, signature.Length).SequenceEqual(signature) ||
            bytes[12] != (byte)'I' || bytes[13] != (byte)'H' || bytes[14] != (byte)'D' || bytes[15] != (byte)'R')
        {
            return (0, 0);
        }
        return (BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(16)),
                BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(20)));
    }

    /// <summary>
    /// Captures a JPEG at <paramref name="quality"/> (0-100, clamped) through the format-aware
    /// host export: unique temp path, host request, async poll for a complete JPEG, in-memory
    /// bytes and the temp file removed in a finally block. Returns null when the export is
    /// unavailable, the host rejects the request or the file does not complete within
    /// <see cref="CaptureTimeout"/>; the caller falls back to the captured PNG.
    /// </summary>
    internal static async Task<byte[]?> CaptureJpegAsync(int quality)
    {
        string path = Path.Combine(
            Path.GetTempPath(),
            "maui-ohos-screenshot-" + Guid.NewGuid().ToString("N") + ".jpg");
        try
        {
            int rc = OpenHarmonyScreenshotBridge.CaptureFormatToFile(
                path, OpenHarmonyScreenshotBridge.FormatJpeg, Math.Clamp(quality, 0, 100));
            if (rc != 0)
            {
                return null;
            }
            var watch = Stopwatch.StartNew();
            while (watch.Elapsed < CaptureTimeout)
            {
                byte[]? jpeg = TryReadCompleteJpeg(path);
                if (jpeg is not null)
                {
                    return jpeg;
                }
                await Task.Delay(CapturePollInterval).ConfigureAwait(false);
            }
            return null;
        }
        finally
        {
            // The bytes are in memory now; the temp file never outlives this call.
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch (Exception)
            {
                // A failed cleanup must not fail the capture.
            }
        }
    }

    /// <summary>
    /// Reads the requested file only when it is a complete JPEG: the SOI marker (FF D8 FF) at the
    /// start and the EOI marker (FF D9) at the end. A partial asynchronous write fails the check,
    /// so the poll loop retries instead of returning truncated bytes.
    /// </summary>
    internal static byte[]? TryReadCompleteJpeg(string path)
    {
        byte[] bytes;
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }
            bytes = File.ReadAllBytes(path);
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
        if (bytes.Length < 4 ||
            bytes[0] != 0xFF || bytes[1] != 0xD8 || bytes[2] != 0xFF ||
            bytes[^2] != 0xFF || bytes[^1] != 0xD9)
        {
            return null;
        }
        return bytes;
    }
}

/// <summary>
/// The captured screenshot, held in memory (the temp file is deleted as soon as it is read).
/// Png reads hand back the captured PNG bytes. A Jpeg read captures (once per quality) a JPEG
/// through ohos_host_screenshot_format and caches it on the result; when the host predates the
/// format export or the JPEG write fails, the PNG bytes are returned instead and the fallback is
/// noted once through the status channel (quality is then not applied).
/// </summary>
public sealed class OpenHarmonyScreenshotResult : IScreenshotResult
{
    private static bool s_jpegFallbackLogged;

    private readonly byte[] _png;
    private readonly object _jpegSync = new();
    private byte[]? _jpeg;
    private int _jpegQuality = -1;
    private bool _jpegUnavailable;

    internal OpenHarmonyScreenshotResult(byte[] png, int width, int height)
    {
        _png = png;
        Width = width;
        Height = height;
    }

    public int Width { get; }

    public int Height { get; }

    public async Task<Stream> OpenReadAsync(ScreenshotFormat format = ScreenshotFormat.Png, int quality = 100)
        => new MemoryStream(await ReadBytesAsync(format, quality).ConfigureAwait(false), writable: false);

    public async Task CopyToAsync(Stream destination, ScreenshotFormat format = ScreenshotFormat.Png, int quality = 100)
    {
        byte[] bytes = await ReadBytesAsync(format, quality).ConfigureAwait(false);
        await destination.WriteAsync(bytes, 0, bytes.Length).ConfigureAwait(false);
    }

    /// <summary>
    /// The bytes a read of <paramref name="format"/> hands out: the captured PNG for Png, or a
    /// JPEG at <paramref name="quality"/> (0-100, clamped) with the PNG as the documented
    /// fallback. The JPEG capture runs once per quality; a failed or unsupported capture is
    /// remembered on the result so a repeated read does not retry a 5 s poll.
    /// </summary>
    private async Task<byte[]> ReadBytesAsync(ScreenshotFormat format, int quality)
    {
        if (format != ScreenshotFormat.Jpeg)
        {
            return _png;
        }
        int requested = Math.Clamp(quality, 0, 100);
        lock (_jpegSync)
        {
            if (_jpeg is not null && _jpegQuality == requested)
            {
                return _jpeg;
            }
            if (_jpegUnavailable)
            {
                return _png;
            }
        }
        byte[]? jpeg;
        try
        {
            jpeg = OpenHarmonyScreenshot.JpegCaptureOverride is { } capture
                ? await capture(requested).ConfigureAwait(false)
                : await OpenHarmonyScreenshot.CaptureJpegAsync(requested).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DllNotFoundException or EntryPointNotFoundException)
        {
            jpeg = null;
        }
        lock (_jpegSync)
        {
            if (jpeg is not null)
            {
                _jpeg = jpeg;
                _jpegQuality = requested;
                return jpeg;
            }
            _jpegUnavailable = true;
            LogJpegFallbackOnce(format);
            return _png;
        }
    }

    /// <summary>
    /// One status note per process when a Jpeg request falls back to the PNG capture (the host
    /// has no format export, or the JPEG write/failure path did not produce one): the bytes stay
    /// PNG and the quality knob stays unapplied, so the fallback is reported instead of implied.
    /// </summary>
    private static void LogJpegFallbackOnce(ScreenshotFormat format)
    {
        if (format != ScreenshotFormat.Jpeg || s_jpegFallbackLogged)
        {
            return;
        }
        s_jpegFallbackLogged = true;
        OpenHarmonyBridge.WriteStatus(
            "[maui] screenshot: Jpeg was requested but no JPEG was produced (host export unavailable or capture failed); the PNG bytes are returned and the quality request is not applied");
    }
}
