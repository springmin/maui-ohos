// Bluetooth GATT client for OpenHarmony - a platform extension, NOT a MAUI core interface.
//
// MAUI has no BLE/GATT API (there is no IBle/IBluetooth interface to implement), so this type
// is an OpenHarmony-specific extra compiled into the platform slice. Apps that want BLE link it
// explicitly; nothing in MAUI references it.
//
// It rides the established host/ArkTS request-response bridge (the same shape as
// OpenHarmonyBluetooth/OpenHarmonyPrinting):
//
//   managed call -> P/Invoke ohos_host_bluetooth_gatt_request(requestId, op, payload) ->
//     ArkTS shell sink (host.registerBluetoothGattSink) requests ohos.permission.ACCESS_BLUETOOTH
//     when needed, lazily imports @kit.ConnectivityKit and runs the kotlin-free GATT calls on
//     ble.createGattClientDevice(address) (connect/disconnect, getServices, read/write
//     characteristic and descriptor, setCharacteristicChangeNotification, setBLEMtuSize) ->
//     host.notifyBluetoothGattResult(requestId, code, payload) ->
//     managed OnResultNative completes the awaiting Task.
//
// Unsolicited device events (characteristic value changed, connection state changed, MTU
// changed) are pushed by the same shell sink through host.notifyBluetoothGattEvent(payload) and
// arrive on the events below: ValueChanged, ConnectionStateChanged, MtuChanged. The push is a
// separate export (ohos_host_bluetooth_gatt_register_event), so an older host library that only
// serves the request/response half still works - the events then never fire.
//
// Result codes: 0 = the request completed (an empty success payload is a valid answer),
// -1 = the platform path is unavailable (no host library, no shell sink, the Connectivity Kit is
// missing or the ACCESS_BLUETOOTH permission was denied), -2 = a transient kit failure (the
// adapter is off, the device is not connected, the SDK rejected the argument). -1 flips
// IsSupported false and keeps it there; -2 only fails that one call. A failed request may carry
// a diagnostic message that is logged, never thrown.
//
// Permissions: ohos.permission.ACCESS_BLUETOOTH (user_grant; requested at call time by the
// shell). Without the manifest declaration the shell answers -1 instead of guessing.
//
// Wire format. Requests carry tab-separated fields (the managed side rejects any field that
// contains a tab/LF/CR, so no escaping is needed in that direction). op and fields:
//   0 connect                address
//   1 disconnect             address
//   2 services               address
//   3 read characteristic    address, serviceUuid, characteristicUuid
//   4 write characteristic   address, serviceUuid, characteristicUuid, writeType (1 with
//                            response / 2 without), valueBase64
//   5 notifications          address, serviceUuid, characteristicUuid, enable (0/1)
//   6 read descriptor        address, serviceUuid, characteristicUuid, descriptorUuid
//   7 write descriptor       address, serviceUuid, characteristicUuid, descriptorUuid, valueBase64
//   8 request MTU            address, mtu
//   9 release                address
// Success payloads: op 2 answers one "serviceUuid\tsPrimary\tcharacteristicUuid\tproperties"
// record per characteristic (4 escaped fields per line, the same OpenHarmonyKitRecords shape
// the other extras use; a service without characteristics carries empty uuid/properties), op
// 3/6 answer the raw value as base64, op 8 the negotiated MTU in decimal (empty when the
// platform did not report one), and the remaining ops answer empty.
//
// Event payloads (host.notifyBluetoothGattEvent), tab-separated:
//   value  address, serviceUuid, characteristicUuid, valueBase64
//   state  address, state (ProfileConnectionState), reason (decimal, empty when absent)
//   mtu    address, mtu (decimal)
// Malformed event payloads are ignored; a value event whose base64 does not decode is ignored.
using System.Collections.Concurrent;
using System.Globalization;
using System.Runtime.InteropServices;
using Microsoft.OpenHarmony.Hosting;

namespace Microsoft.Maui.Platform;

/// <summary>Outcome class of one GATT request (see the file header for the code mapping).</summary>
public enum OpenHarmonyGattStatus
{
    /// <summary>The request completed; an empty payload is a valid answer.</summary>
    Success = 0,

    /// <summary>
    /// The platform path is unavailable (no host library, no shell sink, the Connectivity Kit is
    /// missing or ACCESS_BLUETOOTH was denied). <see cref="OpenHarmonyBluetoothGatt.IsSupported"/>
    /// flips to false and stays there.
    /// </summary>
    Unavailable = -1,

    /// <summary>A transient kit failure (adapter off, device not connected, SDK rejected the call).</summary>
    Failed = -2,
}

/// <summary>
/// Result of a GATT request that carries no value: a <see cref="OpenHarmonyGattStatus"/> and an
/// optional diagnostic <see cref="Message"/> (the shell/kit error text, empty on success).
/// </summary>
public readonly record struct OpenHarmonyGattResult(OpenHarmonyGattStatus Status, string Message)
{
    /// <summary>True when <see cref="Status"/> is <see cref="OpenHarmonyGattStatus.Success"/>.</summary>
    public bool IsSuccess => Status == OpenHarmonyGattStatus.Success;
}

/// <summary>
/// Result of a GATT read: the decoded value bytes (empty on failure) plus the status and the
/// optional diagnostic message.
/// </summary>
public readonly record struct OpenHarmonyGattValue(OpenHarmonyGattStatus Status, byte[] Value, string Message)
{
    /// <summary>True when <see cref="Status"/> is <see cref="OpenHarmonyGattStatus.Success"/>.</summary>
    public bool IsSuccess => Status == OpenHarmonyGattStatus.Success;
}

/// <summary>
/// Result of an MTU request: the negotiated MTU when the platform reported one, otherwise the
/// requested value with a message saying so.
/// </summary>
public readonly record struct OpenHarmonyGattMtuResult(OpenHarmonyGattStatus Status, int Mtu, string Message)
{
    /// <summary>True when <see cref="Status"/> is <see cref="OpenHarmonyGattStatus.Success"/>.</summary>
    public bool IsSuccess => Status == OpenHarmonyGattStatus.Success;
}

/// <summary>Characteristic property flags as reported by the platform (GattProperties).</summary>
[Flags]
public enum OpenHarmonyGattProperty
{
    /// <summary>No property was reported.</summary>
    None = 0,

    /// <summary>The characteristic can be read.</summary>
    Read = 1,

    /// <summary>The characteristic can be written with a response.</summary>
    Write = 2,

    /// <summary>The characteristic can be written without a response.</summary>
    WriteWithoutResponse = 4,

    /// <summary>The characteristic supports notifications.</summary>
    Notify = 8,

    /// <summary>The characteristic supports indications.</summary>
    Indicate = 16,

    /// <summary>The characteristic supports broadcast.</summary>
    Broadcast = 32,

    /// <summary>The characteristic supports authenticated signed writes.</summary>
    AuthenticatedSignedWrites = 64,

    /// <summary>The characteristic has extended properties.</summary>
    ExtendedProperties = 128,
}

/// <summary>One characteristic as reported by service discovery.</summary>
public readonly record struct OpenHarmonyGattCharacteristic(
    string ServiceUuid,
    string CharacteristicUuid,
    OpenHarmonyGattProperty Properties);

/// <summary>One GATT service with its characteristics, as reported by service discovery.</summary>
public sealed class OpenHarmonyGattService
{
    internal OpenHarmonyGattService(string uuid, bool isPrimary, IReadOnlyList<OpenHarmonyGattCharacteristic> characteristics)
    {
        Uuid = uuid;
        IsPrimary = isPrimary;
        Characteristics = characteristics;
    }

    /// <summary>The service UUID as the platform reports it.</summary>
    public string Uuid { get; }

    /// <summary>True for a primary service, false for a secondary/include service.</summary>
    public bool IsPrimary { get; }

    /// <summary>The characteristics this service carries (possibly empty).</summary>
    public IReadOnlyList<OpenHarmonyGattCharacteristic> Characteristics { get; }
}

/// <summary>Connection state as reported by the BLEConnectionStateChange push.</summary>
public enum OpenHarmonyGattConnectionState
{
    /// <summary>The device is disconnected (access.ProfileConnectionState.STATE_DISCONNECTED).</summary>
    Disconnected = 0,

    /// <summary>A connection attempt is in progress.</summary>
    Connecting = 1,

    /// <summary>The device is connected; GATT operations may run.</summary>
    Connected = 2,

    /// <summary>The connection is being torn down.</summary>
    Disconnecting = 3,
}

/// <summary>Payload of <see cref="OpenHarmonyBluetoothGatt.ValueChanged"/>.</summary>
public sealed class OpenHarmonyGattValueChangedEventArgs : EventArgs
{
    internal OpenHarmonyGattValueChangedEventArgs(string deviceAddress, string serviceUuid, string characteristicUuid, byte[] value)
    {
        DeviceAddress = deviceAddress;
        ServiceUuid = serviceUuid;
        CharacteristicUuid = characteristicUuid;
        Value = value;
    }

    /// <summary>The peripheral's device address the value belongs to.</summary>
    public string DeviceAddress { get; }

    /// <summary>The service UUID of the changed characteristic.</summary>
    public string ServiceUuid { get; }

    /// <summary>The characteristic UUID that changed.</summary>
    public string CharacteristicUuid { get; }

    /// <summary>The new characteristic value bytes.</summary>
    public byte[] Value { get; }
}

/// <summary>Payload of <see cref="OpenHarmonyBluetoothGatt.ConnectionStateChanged"/>.</summary>
public sealed class OpenHarmonyGattConnectionChangedEventArgs : EventArgs
{
    internal OpenHarmonyGattConnectionChangedEventArgs(string deviceAddress, OpenHarmonyGattConnectionState state, int reason)
    {
        DeviceAddress = deviceAddress;
        State = state;
        Reason = reason;
    }

    /// <summary>The peripheral's device address the state belongs to.</summary>
    public string DeviceAddress { get; }

    /// <summary>The new connection state.</summary>
    public OpenHarmonyGattConnectionState State { get; }

    /// <summary>The platform disconnect reason, or -1 when the push did not carry one.</summary>
    public int Reason { get; }
}

/// <summary>Payload of <see cref="OpenHarmonyBluetoothGatt.MtuChanged"/>.</summary>
public sealed class OpenHarmonyGattMtuChangedEventArgs : EventArgs
{
    internal OpenHarmonyGattMtuChangedEventArgs(string deviceAddress, int mtu)
    {
        DeviceAddress = deviceAddress;
        Mtu = mtu;
    }

    /// <summary>The peripheral's device address the MTU belongs to.</summary>
    public string DeviceAddress { get; }

    /// <summary>The MTU reported by the platform.</summary>
    public int Mtu { get; }
}

/// <summary>
/// BLE GATT client over the OpenHarmony Connectivity Kit (<c>ble.createGattClientDevice</c>).
/// Platform extension, not a MAUI core interface. All calls degrade: off-device (no host
/// library) they answer <see cref="OpenHarmonyGattStatus.Unavailable"/> / empty results and
/// never throw; <see cref="IsSupported"/> reports whether the platform path is available.
/// </summary>
public static class OpenHarmonyBluetoothGatt
{
    private const string HostLibrary = "libopenharmonyhost.so";
    private const int UnavailableCode = -1;

    // Request opcodes: the shell's registerBluetoothGattSink handler switches on these.
    private const int OpConnect = 0;
    private const int OpDisconnect = 1;
    private const int OpGetServices = 2;
    private const int OpReadCharacteristic = 3;
    private const int OpWriteCharacteristic = 4;
    private const int OpSetNotifications = 5;
    private const int OpReadDescriptor = 6;
    private const int OpWriteDescriptor = 7;
    private const int OpRequestMtu = 8;
    private const int OpRelease = 9;

    // GattWriteType: 1 = WRITE (with response), 2 = WRITE_NO_RESPONSE.
    private const int WriteWithResponse = 1;
    private const int WriteWithoutResponse = 2;

    private const int MaxFieldLength = 128;

    private static readonly TimeSpan s_timeout = TimeSpan.FromSeconds(30);
    private static readonly ConcurrentDictionary<int, TaskCompletionSource<(int Code, string Payload)>> s_pending = new();
    private static readonly char[] s_fieldSeparators = { '\t', '\n', '\r' };

    private static GattResultCallback? s_callback;
    private static GattEventCallback? s_eventCallback;
    private static int s_nextId;
    private static bool s_registered;
    private static bool s_eventRegistered;
    private static bool s_eventUnavailable;
    private static bool s_unavailable;

    [DllImport(HostLibrary, EntryPoint = "ohos_host_bluetooth_gatt_request", CharSet = CharSet.Ansi)]
    private static extern int BluetoothGattRequest(int requestId, int op, string payload);

    [DllImport(HostLibrary, EntryPoint = "ohos_host_bluetooth_gatt_register_result")]
    private static extern void BluetoothGattRegisterResult(IntPtr callback);

    [DllImport(HostLibrary, EntryPoint = "ohos_host_bluetooth_gatt_register_event")]
    private static extern void BluetoothGattRegisterEvent(IntPtr callback);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void GattResultCallback(int requestId, int code, IntPtr payloadUtf8);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void GattEventCallback(IntPtr payloadUtf8);

    /// <summary>
    /// Raised for every characteristic value change the platform reports (subscribe first with
    /// <see cref="SubscribeAsync"/>). The event carries the device address and the service/
    /// characteristic UUIDs plus the decoded bytes; a malformed push is ignored.
    /// </summary>
    public static event EventHandler<OpenHarmonyGattValueChangedEventArgs>? ValueChanged;

    /// <summary>
    /// Raised for every BLEConnectionStateChange push (connect, disconnect and a dropped link).
    /// </summary>
    public static event EventHandler<OpenHarmonyGattConnectionChangedEventArgs>? ConnectionStateChanged;

    /// <summary>
    /// Raised for every BLEMtuChange push, including one that also completes an in-flight
    /// <see cref="RequestMtuAsync"/>.
    /// </summary>
    public static event EventHandler<OpenHarmonyGattMtuChangedEventArgs>? MtuChanged;

    /// <summary>
    /// True when the host library answers the permission probe for ACCESS_BLUETOOTH and no call
    /// has established that the Connectivity Kit or the shell sink is unavailable. ACCESS_BLUETOOTH
    /// is a user_grant permission, so this only reports true while it is granted (the shell
    /// requests it at call time, and the probe follows the same pattern as OpenHarmonyBluetooth).
    /// Off-device (no host library) it is false immediately.
    /// </summary>
    public static bool IsSupported =>
        !s_unavailable && OpenHarmonyBridge.CheckSelfPermission("ohos.permission.ACCESS_BLUETOOTH");

    /// <summary>
    /// Connects the GATT client for <paramref name="address"/> and completes when the
    /// BLEConnectionStateChange push reports the device connected (the shell waits for that push,
    /// bounded by its own timeout). A false success cannot happen: success means the state push
    /// arrived first. The connection state also arrives through
    /// <see cref="ConnectionStateChanged"/>. Never throws.
    /// </summary>
    public static Task<OpenHarmonyGattResult> ConnectAsync(string? address, CancellationToken cancellationToken = default)
    {
        if (!IsValidField(address))
        {
            return Task.FromResult(Failure("connect", "the device address is required"));
        }
        return RequestAsync(OpConnect, new[] { address! }, cancellationToken);
    }

    /// <summary>
    /// Starts disconnecting the GATT client for <paramref name="address"/>. Success means the
    /// platform accepted the call; the final state arrives through
    /// <see cref="ConnectionStateChanged"/>. Never throws.
    /// </summary>
    public static Task<OpenHarmonyGattResult> DisconnectAsync(string? address, CancellationToken cancellationToken = default)
    {
        if (!IsValidField(address))
        {
            return Task.FromResult(Failure("disconnect", "the device address is required"));
        }
        return RequestAsync(OpDisconnect, new[] { address! }, cancellationToken);
    }

    /// <summary>
    /// Closes the GATT client for <paramref name="address"/> and releases its platform resources
    /// and event registrations. Connect again afterwards with <see cref="ConnectAsync"/>.
    /// Never throws.
    /// </summary>
    public static Task<OpenHarmonyGattResult> ReleaseAsync(string? address, CancellationToken cancellationToken = default)
    {
        if (!IsValidField(address))
        {
            return Task.FromResult(Failure("release", "the device address is required"));
        }
        return RequestAsync(OpRelease, new[] { address! }, cancellationToken);
    }

    /// <summary>
    /// Discovers the services and characteristics of <paramref name="address"/> (which must be
    /// connected first). Returns an empty list when the platform path is unavailable or the
    /// discovery failed; the services keep the platform order and the characteristics are
    /// returned per service. Never throws.
    /// </summary>
    public static async Task<IReadOnlyList<OpenHarmonyGattService>> GetServicesAsync(
        string? address,
        CancellationToken cancellationToken = default)
    {
        if (!IsValidField(address))
        {
            return Array.Empty<OpenHarmonyGattService>();
        }
        (int Code, string Payload)? answer = await SendAsync(OpGetServices, new[] { address! }, cancellationToken).ConfigureAwait(false);
        return answer is { Code: 0 } result ? ParseServices(result.Payload) : Array.Empty<OpenHarmonyGattService>();
    }

    /// <summary>
    /// Reads the value of one characteristic. Success carries the bytes (possibly empty);
    /// failure carries <see cref="OpenHarmonyGattStatus"/> plus a diagnostic message. Never throws.
    /// </summary>
    public static async Task<OpenHarmonyGattValue> ReadCharacteristicAsync(
        string? address,
        string? serviceUuid,
        string? characteristicUuid,
        CancellationToken cancellationToken = default)
    {
        if (!IsValidField(address) || !IsValidField(serviceUuid) || !IsValidField(characteristicUuid))
        {
            return new OpenHarmonyGattValue(OpenHarmonyGattStatus.Failed, Array.Empty<byte>(), "read: invalid address or UUID");
        }
        (int Code, string Payload)? answer = await SendAsync(
            OpReadCharacteristic,
            new[] { address!, serviceUuid!, characteristicUuid! },
            cancellationToken).ConfigureAwait(false);
        return ToValue("read", answer);
    }

    /// <summary>
    /// Writes <paramref name="value"/> to one characteristic, with a response when
    /// <paramref name="withResponse"/> is true and without one otherwise (GattWriteType
    /// WRITE / WRITE_NO_RESPONSE). A null/over-long value fails the call instead of throwing.
    /// Never throws.
    /// </summary>
    public static Task<OpenHarmonyGattResult> WriteCharacteristicAsync(
        string? address,
        string? serviceUuid,
        string? characteristicUuid,
        byte[]? value,
        bool withResponse,
        CancellationToken cancellationToken = default)
    {
        if (!IsValidField(address) || !IsValidField(serviceUuid) || !IsValidField(characteristicUuid))
        {
            return Task.FromResult(Failure("write", "invalid address or UUID"));
        }
        if (!IsValidValue(value))
        {
            return Task.FromResult(Failure("write", "the value is null or too long"));
        }
        return RequestAsync(
            OpWriteCharacteristic,
            new[]
            {
                address!,
                serviceUuid!,
                characteristicUuid!,
                withResponse ? WriteWithResponse.ToString(CultureInfo.InvariantCulture) : WriteWithoutResponse.ToString(CultureInfo.InvariantCulture),
                Convert.ToBase64String(value!),
            },
            cancellationToken);
    }

    /// <summary>
    /// Enables or disables characteristic-change notifications/indications for one
    /// characteristic (the CC descriptor write). Success means the platform accepted the call;
    /// values then arrive through <see cref="ValueChanged"/>. Never throws.
    /// </summary>
    public static Task<OpenHarmonyGattResult> SetCharacteristicNotificationsAsync(
        string? address,
        string? serviceUuid,
        string? characteristicUuid,
        bool enable,
        CancellationToken cancellationToken = default)
    {
        if (!IsValidField(address) || !IsValidField(serviceUuid) || !IsValidField(characteristicUuid))
        {
            return Task.FromResult(Failure("notifications", "invalid address or UUID"));
        }
        return RequestAsync(
            OpSetNotifications,
            new[] { address!, serviceUuid!, characteristicUuid!, enable ? "1" : "0" },
            cancellationToken);
    }

    /// <summary>Subscribes to characteristic-change notifications (see
    /// <see cref="SetCharacteristicNotificationsAsync"/>). Never throws.</summary>
    public static Task<OpenHarmonyGattResult> SubscribeAsync(
        string? address,
        string? serviceUuid,
        string? characteristicUuid,
        CancellationToken cancellationToken = default) =>
        SetCharacteristicNotificationsAsync(address, serviceUuid, characteristicUuid, true, cancellationToken);

    /// <summary>Unsubscribes from characteristic-change notifications (see
    /// <see cref="SetCharacteristicNotificationsAsync"/>). Never throws.</summary>
    public static Task<OpenHarmonyGattResult> UnsubscribeAsync(
        string? address,
        string? serviceUuid,
        string? characteristicUuid,
        CancellationToken cancellationToken = default) =>
        SetCharacteristicNotificationsAsync(address, serviceUuid, characteristicUuid, false, cancellationToken);

    /// <summary>
    /// Reads one descriptor (for example a report reference or a client configuration
    /// descriptor) of a characteristic. Same shape as <see cref="ReadCharacteristicAsync"/>.
    /// Never throws.
    /// </summary>
    public static async Task<OpenHarmonyGattValue> ReadDescriptorAsync(
        string? address,
        string? serviceUuid,
        string? characteristicUuid,
        string? descriptorUuid,
        CancellationToken cancellationToken = default)
    {
        if (!IsValidField(address) || !IsValidField(serviceUuid) ||
            !IsValidField(characteristicUuid) || !IsValidField(descriptorUuid))
        {
            return new OpenHarmonyGattValue(OpenHarmonyGattStatus.Failed, Array.Empty<byte>(), "descriptor read: invalid address or UUID");
        }
        (int Code, string Payload)? answer = await SendAsync(
            OpReadDescriptor,
            new[] { address!, serviceUuid!, characteristicUuid!, descriptorUuid! },
            cancellationToken).ConfigureAwait(false);
        return ToValue("descriptor read", answer);
    }

    /// <summary>
    /// Writes one descriptor's value. A null/over-long value fails the call instead of throwing.
    /// Never throws.
    /// </summary>
    public static Task<OpenHarmonyGattResult> WriteDescriptorAsync(
        string? address,
        string? serviceUuid,
        string? characteristicUuid,
        string? descriptorUuid,
        byte[]? value,
        CancellationToken cancellationToken = default)
    {
        if (!IsValidField(address) || !IsValidField(serviceUuid) ||
            !IsValidField(characteristicUuid) || !IsValidField(descriptorUuid))
        {
            return Task.FromResult(Failure("descriptor write", "invalid address or UUID"));
        }
        if (!IsValidValue(value))
        {
            return Task.FromResult(Failure("descriptor write", "the value is null or too long"));
        }
        return RequestAsync(
            OpWriteDescriptor,
            new[] { address!, serviceUuid!, characteristicUuid!, descriptorUuid!, Convert.ToBase64String(value!) },
            cancellationToken);
    }

    /// <summary>
    /// Requests a transmission MTU (the platform call is fire-and-forget). The result carries the
    /// negotiated MTU when the platform's BLEMtuChange push arrived; when it does not (or the
    /// platform reports no value), <see cref="OpenHarmonyGattMtuResult.Mtu"/> is the requested
    /// value and <see cref="OpenHarmonyGattMtuResult.Message"/> says it was not negotiated.
    /// Never throws.
    /// </summary>
    public static async Task<OpenHarmonyGattMtuResult> RequestMtuAsync(
        string? address,
        int mtu,
        CancellationToken cancellationToken = default)
    {
        if (!IsValidField(address))
        {
            return new OpenHarmonyGattMtuResult(OpenHarmonyGattStatus.Failed, mtu, "request MTU: the device address is required");
        }
        if (mtu <= 0 || mtu > 517)
        {
            return new OpenHarmonyGattMtuResult(OpenHarmonyGattStatus.Failed, mtu, "request MTU: the MTU must be between 1 and 517");
        }
        (int Code, string Payload)? answer = await SendAsync(
            OpRequestMtu,
            new[] { address!, mtu.ToString(CultureInfo.InvariantCulture) },
            cancellationToken).ConfigureAwait(false);
        if (answer is not { Code: 0 } result)
        {
            OpenHarmonyGattStatus status = answer is { Code: UnavailableCode }
                ? OpenHarmonyGattStatus.Unavailable
                : OpenHarmonyGattStatus.Failed;
            string message = answer?.Payload ?? "the platform bridge is unavailable";
            return new OpenHarmonyGattMtuResult(status, mtu, message);
        }
        if (int.TryParse(result.Payload, NumberStyles.Integer, CultureInfo.InvariantCulture, out int negotiated) && negotiated > 0)
        {
            return new OpenHarmonyGattMtuResult(OpenHarmonyGattStatus.Success, negotiated, string.Empty);
        }
        return new OpenHarmonyGattMtuResult(
            OpenHarmonyGattStatus.Success,
            mtu,
            "the platform did not report a negotiated MTU; the requested value is returned");
    }

    /// <summary>
    /// Parses the op 2 success payload (one 4-field record per characteristic; see the file
    /// header) into services. Malformed records are skipped by the shared
    /// <see cref="OpenHarmonyKitRecords"/> parser, services keep their first-seen order and a
    /// service without characteristics still appears with an empty list.
    /// </summary>
    public static IReadOnlyList<OpenHarmonyGattService> ParseServices(string? payload)
    {
        List<string[]> records = OpenHarmonyKitRecords.ParseRecords(payload, 4);
        var services = new List<(string Uuid, bool IsPrimary, List<OpenHarmonyGattCharacteristic> Characteristics)>();
        var indexByUuid = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (string[] fields in records)
        {
            string uuid = fields[0].Trim();
            if (uuid.Length == 0)
            {
                continue;
            }
            if (!indexByUuid.TryGetValue(uuid, out int index))
            {
                index = services.Count;
                indexByUuid[uuid] = index;
                services.Add((uuid, fields[1] == "1", new List<OpenHarmonyGattCharacteristic>()));
            }
            string characteristicUuid = fields[2].Trim();
            if (characteristicUuid.Length == 0)
            {
                continue;
            }
            int.TryParse(fields[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out int properties);
            services[index].Characteristics.Add(
                new OpenHarmonyGattCharacteristic(uuid, characteristicUuid, (OpenHarmonyGattProperty)properties));
        }
        var result = new List<OpenHarmonyGattService>(services.Count);
        foreach ((string uuid, bool isPrimary, List<OpenHarmonyGattCharacteristic> characteristics) in services)
        {
            result.Add(new OpenHarmonyGattService(uuid, isPrimary, characteristics.ToArray()));
        }
        return result;
    }

    /// <summary>
    /// Native-shaped entry point for the host's event notify (harness-testable): one
    /// "kind\tfields..." payload, ignored when malformed. Value events decode their base64 and
    /// raise <see cref="ValueChanged"/>, state events raise <see cref="ConnectionStateChanged"/>,
    /// MTU events raise <see cref="MtuChanged"/>.
    /// </summary>
    internal static void OnEventPayload(string? payload)
    {
        if (string.IsNullOrEmpty(payload))
        {
            return;
        }
        string[] fields = payload.Split('\t');
        if (fields.Length == 5 && fields[0] == "value")
        {
            byte[]? value = TryDecodeBase64(fields[4]);
            if (value is not null && fields[1].Length > 0)
            {
                ValueChanged?.Invoke(null, new OpenHarmonyGattValueChangedEventArgs(fields[1], fields[2], fields[3], value));
            }
        }
        else if (fields.Length == 4 && fields[0] == "state")
        {
            if (fields[1].Length == 0 ||
                !int.TryParse(fields[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out int state))
            {
                return;
            }
            int reason = -1;
            if (fields[3].Length > 0)
            {
                int.TryParse(fields[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out reason);
            }
            ConnectionStateChanged?.Invoke(null, new OpenHarmonyGattConnectionChangedEventArgs(
                fields[1], (OpenHarmonyGattConnectionState)state, reason));
        }
        else if (fields.Length == 3 && fields[0] == "mtu")
        {
            if (fields[1].Length > 0 &&
                int.TryParse(fields[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out int mtu))
            {
                MtuChanged?.Invoke(null, new OpenHarmonyGattMtuChangedEventArgs(fields[1], mtu));
            }
        }
    }

    /// <summary>A field is valid when it is non-empty, short and cannot forge a record separator.</summary>
    private static bool IsValidField(string? value) =>
        !string.IsNullOrWhiteSpace(value) &&
        value.Length <= MaxFieldLength &&
        value.IndexOfAny(s_fieldSeparators) < 0;

    /// <summary>Base64 stays well below the host's 1 MiB per-result cap at this length.</summary>
    private static bool IsValidValue(byte[]? value) => value is not null && value.Length <= 512 * 1024;

    private static OpenHarmonyGattResult Failure(string operation, string why) =>
        new(OpenHarmonyGattStatus.Failed, $"{operation}: {why}");

    private static OpenHarmonyGattValue ToValue(string operation, (int Code, string Payload)? answer)
    {
        if (answer is not { } result)
        {
            return new OpenHarmonyGattValue(OpenHarmonyGattStatus.Unavailable, Array.Empty<byte>(), $"{operation}: the platform bridge is unavailable");
        }
        if (result.Code == 0)
        {
            byte[]? value = TryDecodeBase64(result.Payload);
            return value is null
                ? new OpenHarmonyGattValue(OpenHarmonyGattStatus.Failed, Array.Empty<byte>(), $"{operation}: the platform answer was not base64")
                : new OpenHarmonyGattValue(OpenHarmonyGattStatus.Success, value, string.Empty);
        }
        return new OpenHarmonyGattValue(
            result.Code == UnavailableCode ? OpenHarmonyGattStatus.Unavailable : OpenHarmonyGattStatus.Failed,
            Array.Empty<byte>(),
            result.Payload.Length > 0 ? result.Payload : $"{operation} failed ({result.Code})");
    }

    private static byte[]? TryDecodeBase64(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return Array.Empty<byte>();
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

    private static async Task<OpenHarmonyGattResult> RequestAsync(
        int op,
        string[] fields,
        CancellationToken cancellationToken)
    {
        (int Code, string Payload)? answer = await SendAsync(op, fields, cancellationToken).ConfigureAwait(false);
        if (answer is not { } result)
        {
            return new OpenHarmonyGattResult(OpenHarmonyGattStatus.Unavailable, "the platform bridge is unavailable");
        }
        if (result.Code == 0)
        {
            return new OpenHarmonyGattResult(OpenHarmonyGattStatus.Success, string.Empty);
        }
        return new OpenHarmonyGattResult(
            result.Code == UnavailableCode ? OpenHarmonyGattStatus.Unavailable : OpenHarmonyGattStatus.Failed,
            result.Payload.Length > 0 ? result.Payload : $"the platform answered {result.Code}");
    }

    private static async Task<(int Code, string Payload)?> SendAsync(
        int op,
        string[] fields,
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
            if (s_unavailable || BluetoothGattRequest(requestId, op, string.Join('\t', fields)) != 0)
            {
                s_pending.TryRemove(requestId, out _);
                s_unavailable = true;
                OpenHarmonyBridge.WriteStatus("[maui] bluetooth gatt sink is not available");
                return null;
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            s_pending.TryRemove(requestId, out _);
            s_unavailable = true;
            OpenHarmonyBridge.WriteStatus("[maui] bluetooth gatt bridge unavailable (no host library)");
            return null;
        }
        using CancellationTokenRegistration registration = cancellationToken.CanBeCanceled
            ? cancellationToken.Register(() => source.TrySetCanceled(cancellationToken))
            : default;
        Task completed = await Task.WhenAny(source.Task, Task.Delay(s_timeout, CancellationToken.None)).ConfigureAwait(false);
        if (completed != source.Task)
        {
            s_pending.TryRemove(requestId, out _);
            OpenHarmonyBridge.WriteStatus("[maui] bluetooth gatt request timed out");
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
            BluetoothGattRegisterResult(Marshal.GetFunctionPointerForDelegate(s_callback));
            s_registered = true;
            EnsureEventRegistered();
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            s_unavailable = true;
            OpenHarmonyBridge.WriteStatus("[maui] bluetooth gatt bridge unavailable (no host library)");
        }
    }

    // The event notify is a separate export: a host without it still serves the request/response
    // half, so a missing export disables the pushed events only (not the whole extra).
    private static void EnsureEventRegistered()
    {
        if (s_eventRegistered || s_eventUnavailable)
        {
            return;
        }
        try
        {
            s_eventCallback = OnEventNative;
            BluetoothGattRegisterEvent(Marshal.GetFunctionPointerForDelegate(s_eventCallback));
            s_eventRegistered = true;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            s_eventUnavailable = true;
            OpenHarmonyBridge.WriteStatus("[maui] bluetooth gatt event bridge unavailable (older host)");
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

    // A reverse P/Invoke entry: the pushed events run application handlers (ValueChanged /
    // ConnectionStateChanged / MtuChanged), so an exception must not unwind into the native
    // frame (MB-2).
    private static void OnEventNative(IntPtr payloadUtf8)
    {
        try
        {
            string payload = payloadUtf8 == IntPtr.Zero
                ? string.Empty
                : Marshal.PtrToStringUTF8(payloadUtf8) ?? string.Empty;
            OnEventPayload(payload);
        }
        catch (Exception ex)
        {
            OpenHarmonyStatus.NativeCallbackFailed("bluetooth gatt event", ex);
        }
    }
}
