// Bluetooth and Printing platform extras for OpenHarmony. MAUI Essentials has no Bluetooth
// API and no printing API, so both are exposed as documented platform extras:
//
//   OpenHarmonyBluetooth.IsSupported                  -> adapter usable (permission granted)
//   OpenHarmonyBluetooth.IsEnabledAsync()             -> adapter is STATE_ON
//   OpenHarmonyBluetooth.GetPairedDevicesAsync()      -> list of (name, address)
//   OpenHarmonyBluetooth.StartDiscoveryAsync()        -> classic discovery started
//   OpenHarmonyBluetooth.StopDiscoveryAsync()         -> discovery stopped
//   OpenHarmonyBluetooth.GetDiscoveredDevicesAsync()  -> devices found by discovery (deduplicated,
//                                                        empty addresses dropped, name/address order)
//   OpenHarmonyBluetooth.DeviceFound                  -> push event, at most once per new address
//                                                        (a new StartDiscoveryAsync resets that)
//   OpenHarmonyPrinting.IsSupported                   -> print framework usable
//   OpenHarmonyPrinting.PrintTextAsync(jobName, text) -> render the text to a PDF and print it
//   OpenHarmonyPrinting.PrintFileAsync(path)          -> print an existing PDF/image file
//
// Both ride the established host/ArkTS request-response bridge (the same shape as
// OpenHarmonyContacts/OpenHarmonyCalendar):
//
//   managed call -> P/Invoke ohos_host_bluetooth_query / ohos_host_print_file ->
//     ArkTS shell sink (host.registerBluetoothSink / host.registerPrintSink) requests the
//     runtime Bluetooth permission where one is needed and runs the kit call
//     (@kit.ConnectivityKit access/connection, @ohos.print print.print) ->
//     host.notifyBluetoothResult / host.notifyPrintResult ->
//     ohos_host_bluetooth_result / ohos_host_print_result -> the registered managed callback
//     completes the awaiting Task.
//
// Result codes: 0 = the request completed (an empty payload is a valid empty result),
// -1 = the platform path is unavailable (no host library, no shell sink, the kit is missing,
// the permission was denied or the print service is absent), -2 = a transient kit failure
// (Bluetooth off, discovery refused) that does not disable the extra. -1 flips
// IsSupported/unavailable to false and keeps it there; -2 only fails that one call.
//
// Permissions (declared in the application manifest, module.json "requestPermissions"):
// ohos.permission.ACCESS_BLUETOOTH (user_grant: requested at call time by the shell) and
// ohos.permission.PRINT (system_grant: granted at install once declared). Without the
// declaration the shell answers unavailable instead of guessing.
//
// Wire formats (finding B4): Bluetooth devices: one "name\taddress" record per line, '\n'
// separated (a missing name is an empty first field; the shell escapes each field: '\' -> '\\',
// tab -> '\t', LF -> '\n', CR -> '\r'). Paired devices and discovered devices use the same
// shape: discovery pushes each device as its own notify and answers op 4 with the accumulated
// table. Records are decoded by the shared OpenHarmonyKitRecords parser: a record must carry
// exactly two fields, malformed records are skipped, a field is capped at 512 characters and a
// payload at 2000 records, and an unescaped LF inside a name cannot forge another record. The
// discovered-device surface normalizes the records (one record per address, empty addresses
// dropped, ordered by name then address) and reports each address through DeviceFound only once
// per discovery. The adapter state is the decimal access.BluetoothState value as text
// ("2" = STATE_ON). Print results carry an optional diagnostic message (a shell/print-framework
// error) that is logged, never thrown.
using System.Collections.Concurrent;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.OpenHarmony.Hosting;

namespace Microsoft.Maui.Platform;

/// <summary>One paired Bluetooth device as returned by <see cref="OpenHarmonyBluetooth.GetPairedDevicesAsync"/>.</summary>
public readonly record struct OpenHarmonyBluetoothDevice(string Name, string Address);

/// <summary>Bluetooth adapter state and paired devices over the OpenHarmony Connectivity Kit (platform extra).</summary>
public static class OpenHarmonyBluetooth
{
    private const string HostLibrary = "libopenharmonyhost.so";
    private const int UnavailableCode = -1;

    // access.BluetoothState.STATE_ON; the shell sends the decimal enum value.
    private const int StateOn = 2;

    private static readonly TimeSpan s_timeout = TimeSpan.FromSeconds(15);
    private static readonly ConcurrentDictionary<int, TaskCompletionSource<(int Code, string Payload)>> s_pending = new();

    // Addresses already reported for the current/last discovery (the DeviceFound dedupe key).
    // Addresses are case-insensitive; the set is cleared when a new discovery actually starts.
    private static readonly ConcurrentDictionary<string, byte> s_foundAddresses = new(StringComparer.OrdinalIgnoreCase);
    private static BluetoothResultCallback? s_callback;
    private static BluetoothDeviceCallback? s_deviceCallback;
    private static int s_nextId;
    private static bool s_registered;
    private static bool s_deviceRegistered;
    private static bool s_deviceUnavailable;
    private static bool s_unavailable;

    // op 0 = adapter state, 1 = paired devices, 2 = start discovery, 3 = stop discovery,
    // 4 = the devices found by the current/last discovery (same "name\taddress" table).
    [DllImport(HostLibrary, EntryPoint = "ohos_host_bluetooth_query", CharSet = CharSet.Ansi)]
    private static extern int BluetoothQuery(int requestId, int op);

    [DllImport(HostLibrary, EntryPoint = "ohos_host_bluetooth_register_result")]
    private static extern void BluetoothRegisterResult(IntPtr callback);

    [DllImport(HostLibrary, EntryPoint = "ohos_host_bluetooth_register_device_found")]
    private static extern void BluetoothRegisterDeviceFound(IntPtr callback);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void BluetoothResultCallback(int requestId, int code, IntPtr payloadUtf8);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void BluetoothDeviceCallback(IntPtr payloadUtf8);

    /// <summary>
    /// Raised for every device address the platform reports while discovery runs. The device
    /// carries the same name/address pair as <see cref="GetPairedDevicesAsync"/>; a malformed
    /// payload is ignored. An address raises the event at most once per discovery (a device that
    /// also appears in the op 4 table does not repeat), and starting a new discovery forgets the
    /// previous addresses, so a rescan raises the event for devices found again. Off-device (no
    /// host library) the event never fires.
    /// </summary>
    public static event EventHandler<OpenHarmonyBluetoothDevice>? DeviceFound;

    /// <summary>
    /// True when the host library answers the permission probe for ACCESS_BLUETOOTH and no call
    /// has established that the kit/sink is unavailable. Off-device (no host library) this is
    /// false immediately; a denied permission or a missing kit flips it false and keeps it there.
    /// </summary>
    public static bool IsSupported =>
        !s_unavailable && OpenHarmonyBridge.CheckSelfPermission("ohos.permission.ACCESS_BLUETOOTH");

    /// <summary>
    /// Parses one or more "name\taddress" records (the shell's escaped device payload): each
    /// record is decoded by the shared <see cref="OpenHarmonyKitRecords"/> parser, so records
    /// keep their wire order and duplicates, records with anything but exactly two fields (or a
    /// broken escape) are skipped, and at most
    /// <see cref="OpenHarmonyKitRecords.MaxRecords"/> devices are returned. Use
    /// <see cref="NormalizeDiscoveredDevices"/> for the discovered-device surface.
    /// </summary>
    public static IReadOnlyList<OpenHarmonyBluetoothDevice> ParseDevices(string? payload)
    {
        List<string[]> records = OpenHarmonyKitRecords.ParseRecords(payload, 2);
        var devices = new List<OpenHarmonyBluetoothDevice>(records.Count);
        foreach (string[] fields in records)
        {
            devices.Add(new OpenHarmonyBluetoothDevice(fields[0], fields[1]));
        }
        return devices;
    }

    /// <summary>Parses the shell payload ("name\taddress" lines, '\n' separated).</summary>
    public static IReadOnlyList<OpenHarmonyBluetoothDevice> ParsePairedDevices(string? payload) => ParseDevices(payload);

    /// <summary>
    /// Normalizes a discovered-device table for the public surface: records without an address
    /// are dropped, records that share an address collapse into one (a later record may supply
    /// the name when the first one had none) and the result is ordered by name, then address.
    /// The comparison is ordinal (case-insensitive) and therefore stable across cultures.
    /// </summary>
    internal static IReadOnlyList<OpenHarmonyBluetoothDevice> NormalizeDiscoveredDevices(
        IEnumerable<OpenHarmonyBluetoothDevice> devices)
    {
        var indexByAddress = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var normalized = new List<OpenHarmonyBluetoothDevice>();
        foreach (OpenHarmonyBluetoothDevice device in devices)
        {
            string address = device.Address.Trim();
            if (address.Length == 0)
            {
                continue;
            }
            string name = device.Name.Trim();
            if (indexByAddress.TryGetValue(address, out int index))
            {
                if (normalized[index].Name.Length == 0 && name.Length > 0)
                {
                    normalized[index] = new OpenHarmonyBluetoothDevice(name, normalized[index].Address);
                }
                continue;
            }
            indexByAddress[address] = normalized.Count;
            normalized.Add(new OpenHarmonyBluetoothDevice(name, address));
        }
        normalized.Sort(static (left, right) =>
        {
            int order = string.Compare(left.Name, right.Name, StringComparison.OrdinalIgnoreCase);
            return order != 0
                ? order
                : string.Compare(left.Address, right.Address, StringComparison.OrdinalIgnoreCase);
        });
        return normalized;
    }

    /// <summary>True when the adapter-state payload is access.BluetoothState.STATE_ON ("2").</summary>
    public static bool ParseAdapterState(string? payload) =>
        int.TryParse(payload, NumberStyles.Integer, CultureInfo.InvariantCulture, out int state) &&
        state == StateOn;

    /// <summary>True when the local Bluetooth adapter is switched on; false when unavailable.</summary>
    public static async Task<bool> IsEnabledAsync(CancellationToken cancellationToken = default)
    {
        (int Code, string Payload)? answer = await SendAsync(0, cancellationToken).ConfigureAwait(false);
        return answer is { } result && result.Code == 0 && ParseAdapterState(result.Payload);
    }

    /// <summary>
    /// Returns the devices that have been paired with this device (name and address). Returns an
    /// empty list when the platform path is unavailable or the adapter is off.
    /// </summary>
    public static async Task<IReadOnlyList<OpenHarmonyBluetoothDevice>> GetPairedDevicesAsync(
        CancellationToken cancellationToken = default)
    {
        (int Code, string Payload)? answer = await SendAsync(1, cancellationToken).ConfigureAwait(false);
        if (answer is not { } result || result.Code != 0)
        {
            return Array.Empty<OpenHarmonyBluetoothDevice>();
        }
        return ParsePairedDevices(result.Payload);
    }

    /// <summary>
    /// Starts classic Bluetooth discovery. Returns false when the platform path is unavailable or
    /// the adapter is off. Discovered devices arrive through <see cref="DeviceFound"/> and are
    /// collected by the shell for <see cref="GetDiscoveredDevicesAsync"/>; stop the scan with
    /// <see cref="StopDiscoveryAsync"/>.
    /// </summary>
    public static async Task<bool> StartDiscoveryAsync(CancellationToken cancellationToken = default)
    {
        (int Code, string Payload)? answer = await SendAsync(2, cancellationToken).ConfigureAwait(false);
        if (answer is not { } result || result.Code != 0)
        {
            return false;
        }
        // The shell clears its accumulated table when a new discovery starts, so a device found
        // again in this scan is new to the event stream: forget the previous addresses.
        s_foundAddresses.Clear();
        return true;
    }

    /// <summary>Stops classic Bluetooth discovery; false when the platform path is unavailable.</summary>
    public static async Task<bool> StopDiscoveryAsync(CancellationToken cancellationToken = default)
    {
        (int Code, string Payload)? answer = await SendAsync(3, cancellationToken).ConfigureAwait(false);
        return answer is { } result && result.Code == 0;
    }

    /// <summary>
    /// Returns the devices reported since the current/last discovery was started (the shell
    /// accumulates the bluetoothDeviceFind callbacks), deduplicated by address, with empty
    /// addresses dropped and a stable name-then-address order. The addresses also count as
    /// reported, so <see cref="DeviceFound"/> does not repeat them. Returns an empty list when
    /// the platform path is unavailable or nothing was found; it never throws. Start a
    /// discovery first.
    /// </summary>
    public static async Task<IReadOnlyList<OpenHarmonyBluetoothDevice>> GetDiscoveredDevicesAsync(
        CancellationToken cancellationToken = default)
    {
        (int Code, string Payload)? answer = await SendAsync(4, cancellationToken).ConfigureAwait(false);
        if (answer is not { } result || result.Code != 0)
        {
            return Array.Empty<OpenHarmonyBluetoothDevice>();
        }
        IReadOnlyList<OpenHarmonyBluetoothDevice> devices =
            NormalizeDiscoveredDevices(ParseDevices(result.Payload));
        foreach (OpenHarmonyBluetoothDevice device in devices)
        {
            s_foundAddresses.TryAdd(device.Address, 0);
        }
        return devices;
    }

    /// <summary>
    /// Native-shaped entry point for the host's discovered-device notify (harness-testable).
    /// One "name\taddress" record per call; malformed payloads raise nothing. Each address
    /// raises <see cref="DeviceFound"/> at most once per discovery, so the shell may push a
    /// device it also reports through the op 4 table without the app seeing it twice.
    /// </summary>
    internal static void OnDeviceFoundPayload(string? payload)
    {
        IReadOnlyList<OpenHarmonyBluetoothDevice> devices = NormalizeDiscoveredDevices(ParseDevices(payload));
        foreach (OpenHarmonyBluetoothDevice device in devices)
        {
            if (s_foundAddresses.TryAdd(device.Address, 0))
            {
                DeviceFound?.Invoke(null, device);
            }
        }
    }

    private static async Task<(int Code, string Payload)?> SendAsync(
        int op,
        CancellationToken cancellationToken)
    {
        if (s_unavailable)
        {
            return null;
        }
        int requestId = Interlocked.Increment(ref s_nextId);
        var source = new TaskCompletionSource<(int Code, string Payload)>(TaskCreationOptions.RunContinuationsAsynchronously);
        s_pending[requestId] = source;
        try
        {
            EnsureRegistered();
            if (s_unavailable || BluetoothQuery(requestId, op) != 0)
            {
                s_pending.TryRemove(requestId, out _);
                s_unavailable = true;
                OpenHarmonyBridge.WriteStatus("[maui] bluetooth sink is not available");
                return null;
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            s_pending.TryRemove(requestId, out _);
            s_unavailable = true;
            OpenHarmonyBridge.WriteStatus("[maui] bluetooth bridge unavailable (no host library)");
            return null;
        }
        using CancellationTokenRegistration registration = cancellationToken.CanBeCanceled
            ? cancellationToken.Register(() => source.TrySetCanceled(cancellationToken))
            : default;
        Task completed = await Task.WhenAny(source.Task, Task.Delay(s_timeout, CancellationToken.None)).ConfigureAwait(false);
        if (completed != source.Task)
        {
            s_pending.TryRemove(requestId, out _);
            OpenHarmonyBridge.WriteStatus("[maui] bluetooth request timed out");
            return null;
        }
        try
        {
            (int Code, string Payload) answer = await source.Task.ConfigureAwait(false);
            if (answer.Code == UnavailableCode)
            {
                s_unavailable = true;
            }
            return answer;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
    }

    private static void EnsureRegistered()
    {
        if (s_registered || s_unavailable)
        {
            return;
        }
        try
        {
            s_callback = OnResultNative;
            BluetoothRegisterResult(Marshal.GetFunctionPointerForDelegate(s_callback));
            s_registered = true;
            EnsureDeviceRegistered();
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            s_unavailable = true;
            OpenHarmonyBridge.WriteStatus("[maui] bluetooth bridge unavailable (no host library)");
        }
    }

    // The discovered-device notify is a separate export: a host without it still serves the
    // query operations, so a missing export disables DeviceFound only (not the whole extra).
    private static void EnsureDeviceRegistered()
    {
        if (s_deviceRegistered || s_deviceUnavailable)
        {
            return;
        }
        try
        {
            s_deviceCallback = OnDeviceFoundNative;
            BluetoothRegisterDeviceFound(Marshal.GetFunctionPointerForDelegate(s_deviceCallback));
            s_deviceRegistered = true;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            s_deviceUnavailable = true;
            OpenHarmonyBridge.WriteStatus("[maui] bluetooth discovered-device bridge unavailable (older host)");
        }
    }

    private static void OnResultNative(int requestId, int code, IntPtr payloadUtf8)
    {
        string payload = payloadUtf8 == IntPtr.Zero
            ? string.Empty
            : Marshal.PtrToStringUTF8(payloadUtf8) ?? string.Empty;
        if (s_pending.TryRemove(requestId, out TaskCompletionSource<(int Code, string Payload)>? source))
        {
            source.TrySetResult((code, payload));
        }
    }

    // A reverse P/Invoke entry: raising DeviceFound runs application handlers, so an exception
    // must not unwind into the native frame (MB-2).
    private static void OnDeviceFoundNative(IntPtr payloadUtf8)
    {
        try
        {
            string payload = payloadUtf8 == IntPtr.Zero
                ? string.Empty
                : Marshal.PtrToStringUTF8(payloadUtf8) ?? string.Empty;
            OnDeviceFoundPayload(payload);
        }
        catch (Exception ex)
        {
            OpenHarmonyStatus.NativeCallbackFailed("bluetooth device found", ex);
        }
    }
}

/// <summary>Printing over the OpenHarmony print framework (platform extra).</summary>
public static class OpenHarmonyPrinting
{
    private const string HostLibrary = "libopenharmonyhost.so";
    private const int UnavailableCode = -1;

    // The framework lays out a text document as A4 with 11 pt Helvetica on a 14 pt leading.
    private const int LinesPerPage = 53;
    private const int FirstBaselineY = 792;
    private const int LeftMarginX = 50;
    private const int PageWidth = 595;
    private const int PageHeight = 842;

    private static readonly TimeSpan s_timeout = TimeSpan.FromSeconds(15);
    private static readonly ConcurrentDictionary<int, TaskCompletionSource<(int Code, string Message)>> s_pending = new();
    private static PrintResultCallback? s_callback;
    private static int s_nextId;
    private static bool s_registered;
    private static bool s_unavailable;

    [DllImport(HostLibrary, EntryPoint = "ohos_host_print_file", CharSet = CharSet.Ansi)]
    private static extern int PrintFile(int requestId, string path);

    [DllImport(HostLibrary, EntryPoint = "ohos_host_print_register_result")]
    private static extern void PrintRegisterResult(IntPtr callback);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void PrintResultCallback(int requestId, int code, IntPtr messageUtf8);

    /// <summary>
    /// True when the host library answers the permission probe for PRINT and no call has
    /// established that the print service is unavailable. ohos.permission.PRINT is a
    /// system_grant permission, so a packaged app that declares it starts with it granted;
    /// off-device (no host library) this is false immediately.
    /// </summary>
    public static bool IsSupported =>
        !s_unavailable && OpenHarmonyBridge.CheckSelfPermission("ohos.permission.PRINT");

    /// <summary>
    /// Renders the text into a single- or multi-page A4 PDF (Helvetica, WinAnsi text) and
    /// returns the bytes. This is what <see cref="PrintTextAsync"/> sends to the print
    /// framework, which accepts PDF and picture files only. ASCII is written verbatim,
    /// Latin-1 code points become octal escapes and everything else is replaced with '?'.
    /// </summary>
    public static byte[] BuildTextPdf(string? text)
    {
        string normalized = (text ?? string.Empty).Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        string[] lines = normalized.Split('\n');
        int pageCount = Math.Max(1, (lines.Length + LinesPerPage - 1) / LinesPerPage);
        int fontObject = 3 + pageCount * 2;
        int objectCount = fontObject;

        var objects = new List<string>(objectCount);
        var kids = new StringBuilder();
        for (int page = 0; page < pageCount; page++)
        {
            if (page > 0)
            {
                kids.Append(' ');
            }
            kids.Append(3 + page * 2).Append(" 0 R");
        }
        objects.Add("<< /Type /Catalog /Pages 2 0 R >>");
        objects.Add($"<< /Type /Pages /Kids [{kids}] /Count {pageCount} >>");
        for (int page = 0; page < pageCount; page++)
        {
            int pageObject = 3 + page * 2;
            int contentObject = pageObject + 1;
            var content = new StringBuilder();
            content.Append("BT\n/F1 11 Tf\n14 TL\n")
                .Append(LeftMarginX).Append(' ').Append(FirstBaselineY).Append(" Td\n");
            int first = page * LinesPerPage;
            int last = Math.Min(lines.Length, first + LinesPerPage);
            for (int i = first; i < last; i++)
            {
                content.Append('(').Append(EscapePdfText(lines[i])).Append(") Tj\n");
                if (i < last - 1)
                {
                    content.Append("T*\n");
                }
            }
            content.Append("ET\n");
            objects.Add(
                $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {PageWidth} {PageHeight}] " +
                $"/Resources << /Font << /F1 {fontObject} 0 R >> >> /Contents {contentObject} 0 R >>");
            string stream = content.ToString();
            objects.Add($"<< /Length {Encoding.ASCII.GetByteCount(stream)} >>\nstream\n{stream}endstream");
        }
        objects.Add("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>");

        // Every character emitted below is ASCII, so StringBuilder.Length is the byte offset.
        var output = new StringBuilder();
        output.Append("%PDF-1.4\n");
        var offsets = new int[objectCount + 1];
        for (int i = 0; i < objects.Count; i++)
        {
            offsets[i + 1] = output.Length;
            output.Append(i + 1).Append(" 0 obj\n").Append(objects[i]).Append("\nendobj\n");
        }
        int xrefOffset = output.Length;
        output.Append("xref\n0 ").Append(objectCount + 1).Append('\n');
        output.Append("0000000000 65535 f \n");
        for (int i = 1; i <= objectCount; i++)
        {
            output.Append(offsets[i].ToString("D10", CultureInfo.InvariantCulture)).Append(" 00000 n \n");
        }
        output.Append("trailer\n<< /Size ").Append(objectCount + 1).Append(" /Root 1 0 R >>\n")
            .Append("startxref\n").Append(xrefOffset).Append("\n%%EOF\n");
        return Encoding.ASCII.GetBytes(output.ToString());
    }

    /// <summary>
    /// Renders the text to a PDF in the app cache directory (<c>&lt;sanditized jobName&gt;.pdf</c>)
    /// and returns the path. Exposed so callers can reuse the same document with
    /// <see cref="PrintFileAsync"/>; the bytes are exactly <see cref="BuildTextPdf"/>.
    /// </summary>
    public static string WriteTextPdfFile(string? jobName, string? text)
    {
        string path = Path.Combine(OpenHarmonyPaths.CacheDirectory, SanitizeJobName(jobName) + ".pdf");
        File.WriteAllBytes(path, BuildTextPdf(text));
        return path;
    }

    /// <summary>
    /// Prints an existing PDF or picture file through the system print UI. Returns false when
    /// the file does not exist, the platform path is unavailable or the print service rejected
    /// the job; it never throws. A true result means the print UI accepted the job - the final
    /// page outcome is owned by the system print service.
    /// </summary>
    public static async Task<bool> PrintFileAsync(string? path, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }
        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            OpenHarmonyBridge.WriteStatus($"[maui] print path is invalid: {ex.GetType().Name}");
            return false;
        }
        if (!File.Exists(fullPath))
        {
            OpenHarmonyBridge.WriteStatus($"[maui] print file not found: {fullPath}");
            return false;
        }
        if (s_unavailable)
        {
            return false;
        }
        (int Code, string Message)? answer = await SendAsync(
            requestId => PrintFile(requestId, fullPath),
            cancellationToken).ConfigureAwait(false);
        if (answer is not { } result || result.Code != 0)
        {
            if (answer is { } failed)
            {
                OpenHarmonyBridge.WriteStatus($"[maui] print request failed ({failed.Code}): {failed.Message}");
            }
            return false;
        }
        return true;
    }

    /// <summary>
    /// Renders <paramref name="text"/> to a PDF and prints it through <see cref="PrintFileAsync"/>.
    /// Returns false for empty text or when the platform path is unavailable; it never throws.
    /// </summary>
    public static async Task<bool> PrintTextAsync(
        string? jobName,
        string? text,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }
        string path;
        try
        {
            path = WriteTextPdfFile(jobName, text);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            OpenHarmonyBridge.WriteStatus($"[maui] print document could not be written: {ex.GetType().Name}");
            return false;
        }
        return await PrintFileAsync(path, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Sanitizes a job name into a safe PDF file name stem (never empty).</summary>
    internal static string SanitizeJobName(string? jobName)
    {
        if (string.IsNullOrWhiteSpace(jobName))
        {
            return "print";
        }
        var builder = new StringBuilder(jobName.Length);
        foreach (char c in jobName)
        {
            builder.Append(
                (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || c == '-' || c == '_'
                    ? c
                    : '_');
        }
        string name = builder.ToString().Trim('_');
        if (name.Length == 0)
        {
            return "print";
        }
        return name.Length > 48 ? name[..48] : name;
    }

    private static string EscapePdfText(string line)
    {
        var builder = new StringBuilder(line.Length + 8);
        foreach (char c in line)
        {
            switch (c)
            {
                case '\\':
                    builder.Append("\\\\");
                    break;
                case '(':
                    builder.Append("\\(");
                    break;
                case ')':
                    builder.Append("\\)");
                    break;
                case '\t':
                    builder.Append(' ');
                    break;
                default:
                    if (c >= 32 && c <= 126)
                    {
                        builder.Append(c);
                    }
                    else if (c is >= (char)160 and <= (char)255)
                    {
                        // WinAnsiEncoding matches Latin-1 for this range; write it as an octal escape.
                        builder.Append('\\').Append(Convert.ToString(c, 8).PadLeft(3, '0'));
                    }
                    else
                    {
                        builder.Append('?');
                    }
                    break;
            }
        }
        return builder.ToString();
    }

    private static async Task<(int Code, string Message)?> SendAsync(
        Func<int, int> send,
        CancellationToken cancellationToken)
    {
        int requestId = Interlocked.Increment(ref s_nextId);
        var source = new TaskCompletionSource<(int Code, string Message)>(TaskCreationOptions.RunContinuationsAsynchronously);
        s_pending[requestId] = source;
        try
        {
            EnsureRegistered();
            if (s_unavailable || send(requestId) != 0)
            {
                s_pending.TryRemove(requestId, out _);
                s_unavailable = true;
                OpenHarmonyBridge.WriteStatus("[maui] print sink is not available");
                return null;
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            s_pending.TryRemove(requestId, out _);
            s_unavailable = true;
            OpenHarmonyBridge.WriteStatus("[maui] print bridge unavailable (no host library)");
            return null;
        }
        using CancellationTokenRegistration registration = cancellationToken.CanBeCanceled
            ? cancellationToken.Register(() => source.TrySetCanceled(cancellationToken))
            : default;
        Task completed = await Task.WhenAny(source.Task, Task.Delay(s_timeout, CancellationToken.None)).ConfigureAwait(false);
        if (completed != source.Task)
        {
            s_pending.TryRemove(requestId, out _);
            OpenHarmonyBridge.WriteStatus("[maui] print request timed out");
            return null;
        }
        try
        {
            (int Code, string Message) answer = await source.Task.ConfigureAwait(false);
            if (answer.Code == UnavailableCode)
            {
                s_unavailable = true;
            }
            return answer;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
    }

    private static void EnsureRegistered()
    {
        if (s_registered || s_unavailable)
        {
            return;
        }
        try
        {
            s_callback = OnResultNative;
            PrintRegisterResult(Marshal.GetFunctionPointerForDelegate(s_callback));
            s_registered = true;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            s_unavailable = true;
            OpenHarmonyBridge.WriteStatus("[maui] print bridge unavailable (no host library)");
        }
    }

    private static void OnResultNative(int requestId, int code, IntPtr messageUtf8)
    {
        string message = messageUtf8 == IntPtr.Zero
            ? string.Empty
            : Marshal.PtrToStringUTF8(messageUtf8) ?? string.Empty;
        if (s_pending.TryRemove(requestId, out TaskCompletionSource<(int Code, string Message)>? source))
        {
            source.TrySetResult((code, message));
        }
    }
}
