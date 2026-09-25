// Contacts and Calendar platform extras for OpenHarmony. The MAUI Essentials surface has no
// contacts or calendar API, so these are exposed as documented platform extras:
//
//   OpenHarmonyContacts.FindAsync(prefix, limit)  -> list of (name, phone)
//   OpenHarmonyCalendar.ListUpcomingAsync(days)   -> list of (title, start, end)
//   OpenHarmonyCalendar.AddEventAsync(title, startIso, endIso) -> true when the shell added it
//
// Both ride the established host/ArkTS request-response bridge (the same shape as
// OpenHarmonyTextToSpeech):
//
//   managed call -> P/Invoke ohos_host_contacts_query / ohos_host_calendar_list /
//     ohos_host_calendar_add -> ArkTS shell sink (host.registerContactsSink /
//     host.registerCalendarSink) requests the runtime permission and runs the kit call
//     (@kit.ContactsKit contact.queryContacts / @kit.CalendarKit calendarManager) ->
//     host.notifyContactsResult / host.notifyCalendarResult ->
//     ohos_host_contacts_result / ohos_host_calendar_result -> the registered managed callback
//     completes the awaiting Task.
//
// Wire format (finding B4): one record per line, '\t'-separated fields. Contacts: name, phone.
// Calendar: title, start ISO-8601, end ISO-8601. The shell escapes every field before joining
// it into a record ('\' -> '\\', tab -> '\t', LF -> '\n', CR -> '\r'); the parsers below split
// on the raw separators first and then decode each field with the exact reverse mapping (see
// OpenHarmonyKitRecords). Records with the wrong field count, a broken escape or an over-long
// field are skipped, a payload yields at most 2000 records and an unescaped LF that arrives
// before a record's first tab is folded into that field instead of forging a record.
// An empty payload is a valid empty result;
// code -1 means the platform path is unavailable (no host library, no shell sink, the kit is
// missing or the permission was denied). In that case the calls return an empty list / false
// and report IsSupported == false; they never throw off-device. The runtime permission request
// needs the matching entry in the application manifest (module.json
// "requestPermissions": ohos.permission.READ_CONTACTS for contacts;
// ohos.permission.READ_CALENDAR and ohos.permission.WRITE_CALENDAR for calendar) - without the
// declaration the shell answers unavailable instead of guessing.
using System.Collections.Concurrent;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.OpenHarmony.Hosting;

namespace Microsoft.Maui.Platform;

/// <summary>One contact record as returned by <see cref="OpenHarmonyContacts.FindAsync"/>.</summary>
public readonly record struct OpenHarmonyContact(string Name, string Phone);

/// <summary>One calendar event as returned by <see cref="OpenHarmonyCalendar.ListUpcomingAsync"/>.</summary>
public readonly record struct OpenHarmonyCalendarEvent(string Title, DateTimeOffset Start, DateTimeOffset End);

/// <summary>Contact lookup over the OpenHarmony Contacts Kit (platform extra).</summary>
public static partial class OpenHarmonyContacts
{
    private const string HostLibrary = "libopenharmonyhost.so";

    private static readonly TimeSpan s_timeout = TimeSpan.FromSeconds(15);
    private static readonly ConcurrentDictionary<int, TaskCompletionSource<(int Code, string Payload)>> s_pending = new();
    private static ContactsResultCallback? s_callback;
    private static int s_nextId;
    private static bool s_registered;
    private static bool s_unavailable;

    [LibraryImport(HostLibrary, EntryPoint = "ohos_host_contacts_query", StringMarshalling = StringMarshalling.Utf8)]
    private static partial int ContactsQuery(int requestId, string namePrefix, int limit);

    [LibraryImport(HostLibrary, EntryPoint = "ohos_host_contacts_register_result")]
    private static partial void ContactsRegisterResult(IntPtr callback);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void ContactsResultCallback(int requestId, int code, IntPtr payloadUtf8);

    /// <summary>
    /// True when the host library answers the permission probe for READ_CONTACTS and no call has
    /// established that the kit/sink is unavailable. Off-device (no host library) this is false
    /// immediately; a denied permission or a kit failure flips it false and keeps it there.
    /// </summary>
    public static bool IsSupported =>
        !s_unavailable && OpenHarmonyBridge.CheckSelfPermission("ohos.permission.READ_CONTACTS");

    /// <summary>
    /// Parses the shell payload ("name\tphone" records, '\n' separated, each field escaped by
    /// the shell): records with exactly two fields are decoded and returned, anything malformed
    /// (wrong field count, broken escape, over-long field) is skipped. An unescaped LF inside a
    /// name is kept as literal text instead of starting a new record. Never throws; a payload
    /// yields at most <see cref="OpenHarmonyKitRecords.MaxRecords"/> contacts.
    /// </summary>
    public static IReadOnlyList<OpenHarmonyContact> Parse(string? payload)
    {
        List<string[]> records = OpenHarmonyKitRecords.ParseRecords(payload, 2);
        var contacts = new List<OpenHarmonyContact>(records.Count);
        foreach (string[] fields in records)
        {
            contacts.Add(new OpenHarmonyContact(fields[0], fields[1]));
        }
        return contacts;
    }

    /// <summary>
    /// Returns up to <paramref name="limit"/> contacts whose full name starts with
    /// <paramref name="namePrefix"/> (all contacts when the prefix is empty/null).
    /// </summary>
    public static async Task<IReadOnlyList<OpenHarmonyContact>> FindAsync(
        string? namePrefix = null,
        int limit = 25,
        CancellationToken cancellationToken = default)
    {
        if (limit <= 0 || s_unavailable)
        {
            return Array.Empty<OpenHarmonyContact>();
        }
        (int Code, string Payload)? answer = await SendAsync(
            requestId => ContactsQuery(requestId, namePrefix ?? string.Empty, limit),
            cancellationToken).ConfigureAwait(false);
        if (answer is null)
        {
            return Array.Empty<OpenHarmonyContact>();
        }
        if (answer.Value.Code != 0)
        {
            s_unavailable = true;
            OpenHarmonyBridge.WriteStatus("[maui] contacts query unavailable (kit or permission)");
            return Array.Empty<OpenHarmonyContact>();
        }
        return Parse(answer.Value.Payload);
    }

    private static async Task<(int Code, string Payload)?> SendAsync(
        Func<int, int> send,
        CancellationToken cancellationToken)
    {
        int requestId = Interlocked.Increment(ref s_nextId);
        var source = new TaskCompletionSource<(int Code, string Payload)>(TaskCreationOptions.RunContinuationsAsynchronously);
        s_pending[requestId] = source;
        try
        {
            EnsureRegistered();
            if (s_unavailable || send(requestId) != 0)
            {
                s_pending.TryRemove(requestId, out _);
                s_unavailable = true;
                OpenHarmonyBridge.WriteStatus("[maui] contacts sink is not available");
                return null;
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            s_pending.TryRemove(requestId, out _);
            s_unavailable = true;
            OpenHarmonyBridge.WriteStatus("[maui] contacts bridge unavailable (no host library)");
            return null;
        }
        using CancellationTokenRegistration registration = cancellationToken.CanBeCanceled
            ? cancellationToken.Register(() => source.TrySetCanceled(cancellationToken))
            : default;
        Task completed = await Task.WhenAny(source.Task, Task.Delay(s_timeout, CancellationToken.None)).ConfigureAwait(false);
        if (completed != source.Task)
        {
            s_pending.TryRemove(requestId, out _);
            OpenHarmonyBridge.WriteStatus("[maui] contacts request timed out");
            return null;
        }
        try
        {
            return await source.Task.ConfigureAwait(false);
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
            ContactsRegisterResult(Marshal.GetFunctionPointerForDelegate(s_callback));
            s_registered = true;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            s_unavailable = true;
            OpenHarmonyBridge.WriteStatus("[maui] contacts bridge unavailable (no host library)");
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
}

/// <summary>Calendar list/add over the OpenHarmony Calendar Kit (platform extra).</summary>
public static partial class OpenHarmonyCalendar
{
    private const string HostLibrary = "libopenharmonyhost.so";

    private static readonly TimeSpan s_timeout = TimeSpan.FromSeconds(15);
    private static readonly ConcurrentDictionary<int, TaskCompletionSource<(int Code, string Payload)>> s_pending = new();
    private static CalendarResultCallback? s_callback;
    private static int s_nextId;
    private static bool s_registered;
    private static bool s_unavailable;

    [LibraryImport(HostLibrary, EntryPoint = "ohos_host_calendar_list", StringMarshalling = StringMarshalling.Utf8)]
    private static partial int CalendarList(int requestId, int days);

    [LibraryImport(HostLibrary, EntryPoint = "ohos_host_calendar_add", StringMarshalling = StringMarshalling.Utf8)]
    private static partial int CalendarAdd(int requestId, string title, string startIso, string endIso);

    [LibraryImport(HostLibrary, EntryPoint = "ohos_host_calendar_register_result")]
    private static partial void CalendarRegisterResult(IntPtr callback);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void CalendarResultCallback(int requestId, int code, IntPtr payloadUtf8);

    /// <summary>
    /// True when the host library answers the permission probe for READ_CALENDAR and no call has
    /// established that the kit/sink is unavailable. Off-device (no host library) this is false
    /// immediately; a denied permission or a kit failure flips it false and keeps it there.
    /// Event creation additionally needs WRITE_CALENDAR (checked at call time, not here).
    /// </summary>
    public static bool IsSupported =>
        !s_unavailable && OpenHarmonyBridge.CheckSelfPermission("ohos.permission.READ_CALENDAR");

    /// <summary>
    /// Parses the shell payload ("title\tstartIso\tendIso" records, '\n' separated, each field
    /// escaped by the shell): records with exactly three fields and valid ISO-8601 times are
    /// decoded and returned; anything malformed (wrong field count, broken escape, over-long
    /// field, unparsable time) is skipped. An unescaped LF inside a title is kept as literal
    /// text instead of starting a new record. Never throws; a payload yields at most
    /// <see cref="OpenHarmonyKitRecords.MaxRecords"/> events.
    /// </summary>
    public static IReadOnlyList<OpenHarmonyCalendarEvent> Parse(string? payload)
    {
        List<string[]> records = OpenHarmonyKitRecords.ParseRecords(payload, 3);
        var events = new List<OpenHarmonyCalendarEvent>(records.Count);
        foreach (string[] fields in records)
        {
            if (!TryParseIso(fields[1], out DateTimeOffset start) ||
                !TryParseIso(fields[2], out DateTimeOffset end))
            {
                continue;
            }
            events.Add(new OpenHarmonyCalendarEvent(fields[0], start, end));
        }
        return events;
    }

    /// <summary>Lists the events that start within the next <paramref name="days"/> days.</summary>
    public static async Task<IReadOnlyList<OpenHarmonyCalendarEvent>> ListUpcomingAsync(
        int days = 7,
        CancellationToken cancellationToken = default)
    {
        if (days <= 0 || s_unavailable)
        {
            return Array.Empty<OpenHarmonyCalendarEvent>();
        }
        (int Code, string Payload)? answer = await SendAsync(
            requestId => CalendarList(requestId, days),
            cancellationToken).ConfigureAwait(false);
        if (answer is null)
        {
            return Array.Empty<OpenHarmonyCalendarEvent>();
        }
        if (answer.Value.Code != 0)
        {
            s_unavailable = true;
            OpenHarmonyBridge.WriteStatus("[maui] calendar list unavailable (kit or permission)");
            return Array.Empty<OpenHarmonyCalendarEvent>();
        }
        return Parse(answer.Value.Payload);
    }

    /// <summary>
    /// Adds a single event from ISO-8601 start/end times. Returns false when the platform
    /// path is unavailable or the arguments are not valid ISO-8601.
    /// </summary>
    public static async Task<bool> AddEventAsync(
        string title,
        string startIso,
        string endIso,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(title) || !TryParseIso(startIso, out _) ||
            !TryParseIso(endIso, out _) || s_unavailable)
        {
            return false;
        }
        (int Code, string Payload)? answer = await SendAsync(
            requestId => CalendarAdd(requestId, title, startIso, endIso),
            cancellationToken).ConfigureAwait(false);
        if (answer is null)
        {
            return false;
        }
        if (answer.Value.Code != 0)
        {
            s_unavailable = true;
            OpenHarmonyBridge.WriteStatus("[maui] calendar add unavailable (kit or permission)");
            return false;
        }
        return true;
    }

    private static async Task<(int Code, string Payload)?> SendAsync(
        Func<int, int> send,
        CancellationToken cancellationToken)
    {
        int requestId = Interlocked.Increment(ref s_nextId);
        var source = new TaskCompletionSource<(int Code, string Payload)>(TaskCreationOptions.RunContinuationsAsynchronously);
        s_pending[requestId] = source;
        try
        {
            EnsureRegistered();
            if (s_unavailable || send(requestId) != 0)
            {
                s_pending.TryRemove(requestId, out _);
                s_unavailable = true;
                OpenHarmonyBridge.WriteStatus("[maui] calendar sink is not available");
                return null;
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            s_pending.TryRemove(requestId, out _);
            s_unavailable = true;
            OpenHarmonyBridge.WriteStatus("[maui] calendar bridge unavailable (no host library)");
            return null;
        }
        using CancellationTokenRegistration registration = cancellationToken.CanBeCanceled
            ? cancellationToken.Register(() => source.TrySetCanceled(cancellationToken))
            : default;
        Task completed = await Task.WhenAny(source.Task, Task.Delay(s_timeout, CancellationToken.None)).ConfigureAwait(false);
        if (completed != source.Task)
        {
            s_pending.TryRemove(requestId, out _);
            OpenHarmonyBridge.WriteStatus("[maui] calendar request timed out");
            return null;
        }
        try
        {
            return await source.Task.ConfigureAwait(false);
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
            CalendarRegisterResult(Marshal.GetFunctionPointerForDelegate(s_callback));
            s_registered = true;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            s_unavailable = true;
            OpenHarmonyBridge.WriteStatus("[maui] calendar bridge unavailable (no host library)");
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

    private static bool TryParseIso(string value, out DateTimeOffset result) =>
        DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out result);
}

/// <summary>
/// Shared decoder for the shell's tab/newline kit records (finding B4), used by the contacts,
/// calendar and Bluetooth parsers. The ArkTS shell escapes every field before joining it into a
/// record ('\' -> '\\', tab -> '\t', LF -> '\n', CR -> '\r'); this parser splits on the raw
/// separators first and then reverses that mapping per field with a single left-to-right pass.
/// Parsing is defensive and never throws:
/// <list type="bullet">
/// <item>a record must carry exactly the expected number of fields, otherwise it is skipped;</item>
/// <item>an unknown escape or a trailing backslash makes the record malformed and skips it;</item>
/// <item>a field is capped at <see cref="MaxFieldLength"/> decoded characters;</item>
/// <item>a payload yields at most <see cref="MaxRecords"/> records;</item>
/// <item>a raw LF that arrives before the record's first tab (an older shell that joined kit
/// strings unescaped) is folded into the first field as literal text instead of being taken as
/// a record separator, so a name/title containing a raw newline cannot forge another record.</item>
/// </list>
/// </summary>
internal static class OpenHarmonyKitRecords
{
    /// <summary>Maximum decoded length of one field; longer records are skipped.</summary>
    internal const int MaxFieldLength = 512;

    /// <summary>Maximum number of records taken from one payload.</summary>
    internal const int MaxRecords = 2000;

    private static readonly char[] s_separators = { '\t', '\n' };

    /// <summary>
    /// Parses <paramref name="payload"/> into records of exactly <paramref name="fieldCount"/>
    /// decoded fields each (each record is an array in field order). Malformed records are
    /// skipped; see the type remarks for the exact rules.
    /// </summary>
    internal static List<string[]> ParseRecords(string? payload, int fieldCount)
    {
        var records = new List<string[]>();
        if (string.IsNullOrEmpty(payload) || fieldCount < 1)
        {
            return records;
        }

        var fields = new List<string>(fieldCount);
        var firstField = new StringBuilder();
        int position = 0;
        while (position < payload.Length && records.Count < MaxRecords)
        {
            fields.Clear();
            firstField.Clear();
            bool malformed = false;
            bool complete = false;
            while (!complete && !malformed && position < payload.Length)
            {
                if (!TryReadField(payload, ref position, out string value, out char terminator))
                {
                    malformed = true;
                    break;
                }

                if (terminator == '\t')
                {
                    if (fields.Count >= fieldCount)
                    {
                        // One field too many for this record: drop it and resync on the next line.
                        malformed = true;
                        break;
                    }
                    AddField(fields, firstField, value);
                    continue;
                }

                // The field ended at a LF or at the end of the payload.
                if (fields.Count == fieldCount - 1)
                {
                    AddField(fields, firstField, value);
                    records.Add(fields.ToArray());
                    complete = true;
                    continue;
                }
                if (fields.Count == 0 && terminator == '\n' &&
                    (firstField.Length > 0 || value.Length > 0))
                {
                    // A raw LF inside an unescaped first field: keep it as literal text and let
                    // the rest of the (older) record follow, instead of starting a forged one.
                    firstField.Append(value).Append('\n');
                    continue;
                }
                malformed = true;
            }

            // A record that went bad while a separator was consumed leaves the rest of its line
            // unread; skip it so the remaining fields cannot start a new record.
            if (malformed && position > 0 && position <= payload.Length && payload[position - 1] == '\t')
            {
                SkipToLineEnd(payload, ref position);
            }
        }
        return records;
    }

    private static void AddField(List<string> fields, StringBuilder firstField, string value)
    {
        fields.Add(firstField.Length == 0 ? value : firstField.Append(value).ToString());
        firstField.Clear();
    }

    /// <summary>
    /// Reads one escaped field up to and including the next separator (or the end of the
    /// payload) and decodes it. <paramref name="position"/> always ends up past the separator,
    /// even when the field itself is malformed, so the caller can resynchronize.
    /// </summary>
    private static bool TryReadField(string payload, ref int position, out string value, out char terminator)
    {
        int start = position;
        int end = payload.IndexOfAny(s_separators, start);
        if (end < 0)
        {
            end = payload.Length;
            terminator = '\0';
            position = payload.Length;
        }
        else
        {
            terminator = payload[end];
            position = end + 1;
        }
        return TryDecodeField(payload.AsSpan(start, end - start), out value);
    }

    /// <summary>Reverses the shell's per-field escape; false for anything not produced by it.</summary>
    private static bool TryDecodeField(ReadOnlySpan<char> encoded, out string value)
    {
        value = string.Empty;
        if (encoded.Length == 0)
        {
            return true;
        }
        // Every escaped character is at most two encoded characters long, so a longer fragment
        // cannot decode within the field cap.
        if (encoded.Length > MaxFieldLength * 2)
        {
            return false;
        }
        var decoded = new StringBuilder(Math.Min(encoded.Length, MaxFieldLength));
        for (int i = 0; i < encoded.Length; i++)
        {
            char current = encoded[i];
            if (current == '\\')
            {
                if (i + 1 == encoded.Length)
                {
                    return false; // a trailing backslash has no escaped character
                }
                switch (encoded[++i])
                {
                    case '\\':
                        current = '\\';
                        break;
                    case 't':
                        current = '\t';
                        break;
                    case 'n':
                        current = '\n';
                        break;
                    case 'r':
                        current = '\r';
                        break;
                    default:
                        return false; // only the shell's four escapes are meaningful
                }
            }
            if (decoded.Length >= MaxFieldLength)
            {
                return false;
            }
            decoded.Append(current);
        }
        value = decoded.ToString();
        return true;
    }

    private static void SkipToLineEnd(string payload, ref int position)
    {
        int end = payload.IndexOf('\n', position, StringComparison.Ordinal);
        position = end < 0 ? payload.Length : end + 1;
    }
}
