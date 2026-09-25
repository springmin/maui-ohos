// Screenshot (Microsoft.Maui.Media.IScreenshot) for OpenHarmony over the host capture contract
//   int ohos_host_screenshot(const char* out_path)
// The host captures the current surface to the PNG file at out_path and returns 0 on success
// (C1 implements the host + shell half in parallel; the frozen contract fixes only the export
// name, its char* argument and the PNG output). The managed side picks a unique path under
// Path.GetTempPath(), lets the host write it, reads the PNG into memory, deletes the temp file in
// a finally block (also when reading fails) and returns an in-memory result, so no temp file
// outlives the capture. Success is decided by the produced file (non-empty), not the return
// code, so a host half that only signals through the file still works; the code is logged on
// failure.
//
// IsCaptureSupported probes the host library with NativeLibrary.TryLoad/TryGetExport for the
// frozen entry point (the other bridges discover availability on first call; a capture cannot be
// attempted as a probe because it would take a screenshot). Off-device the property is false and
// CaptureAsync returns null without throwing, like the reference implementation's
// "not supported" path.
//
// ScreenshotFormat/quality: the capture is always PNG. Png is returned as-is; Jpeg cannot be
// produced here because the slice has no rasterizer/codec backend to transcode with, so the
// same PNG bytes are returned for either format and the first Jpeg request is noted once
// through the status channel (OpenHarmonyScreenshotResult.LogJpegFallbackOnce) instead of
// silently claiming a conversion; quality is a lossy-encoder knob and is not applied either
// (PNG is lossless).
//
// The shell writes the PNG asynchronously after the host queued the request (the host only
// reports that the request reached the shell sink), so CaptureAsync polls the target path until
// the file is a complete PNG - signature, IHDR size and the IEND chunk - or CaptureTimeout
// elapses. The polling is async (Task.Delay), so a UI-thread caller is not blocked.
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

    [LibraryImport(HostLibrary, EntryPoint = EntryPoint, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int ScreenshotNative(string outPath);

    private static int s_available;   // 0 unknown, 1 exported, -1 missing (cached probe)

    /// <summary>True when the host library exports ohos_host_screenshot.</summary>
    internal static bool IsAvailable
    {
        get
        {
            int known = Volatile.Read(ref s_available);
            if (known != 0)
            {
                return known > 0;
            }
            bool available;
            try
            {
                available = NativeLibrary.TryLoad(HostLibrary, out IntPtr handle) &&
                    NativeLibrary.TryGetExport(handle, EntryPoint, out _);
            }
            catch (Exception)
            {
                available = false;
            }
            Volatile.Write(ref s_available, available ? 1 : -1);
            return available;
        }
    }

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
}

/// <summary>
/// The captured PNG, held in memory (the temp file is deleted as soon as it is read).
/// OpenReadAsync/CopyToAsync always hand out the PNG bytes; a Jpeg request is noted once and
/// still answered with the PNG (the slice has no transcoder, see the file header), and quality
/// is ignored for the same reason.
/// </summary>
public sealed class OpenHarmonyScreenshotResult : IScreenshotResult
{
    private static bool s_jpegFallbackLogged;

    private readonly byte[] _png;

    internal OpenHarmonyScreenshotResult(byte[] png, int width, int height)
    {
        _png = png;
        Width = width;
        Height = height;
    }

    public int Width { get; }

    public int Height { get; }

    public Task<Stream> OpenReadAsync(ScreenshotFormat format = ScreenshotFormat.Png, int quality = 100)
    {
        LogJpegFallbackOnce(format);
        return Task.FromResult<Stream>(new MemoryStream(_png, writable: false));
    }

    public Task CopyToAsync(Stream destination, ScreenshotFormat format = ScreenshotFormat.Png, int quality = 100)
    {
        LogJpegFallbackOnce(format);
        return destination.WriteAsync(_png, 0, _png.Length);
    }

    /// <summary>
    /// One status note per process when a caller asks for Jpeg: the bytes stay PNG and the
    /// quality knob stays unapplied, so the fallback is reported instead of implied.
    /// </summary>
    private static void LogJpegFallbackOnce(ScreenshotFormat format)
    {
        if (format != ScreenshotFormat.Jpeg || s_jpegFallbackLogged)
        {
            return;
        }
        s_jpegFallbackLogged = true;
        OpenHarmonyBridge.WriteStatus(
            "[maui] screenshot: Jpeg was requested but the host capture is PNG and the slice has no JPEG encoder; the PNG bytes are returned and the quality request is not applied");
    }
}
