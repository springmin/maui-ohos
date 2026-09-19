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
// Wire format: one record per line, '\t'-separated fields. Contacts: name, phone.
// Calendar: title, start ISO-8601, end ISO-8601. An empty payload is a valid empty result;
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
using Microsoft.OpenHarmony.Hosting;

namespace Microsoft.Maui.Platform;

/// <summary>One contact record as returned by <see cref="OpenHarmonyContacts.FindAsync"/>.</summary>
public readonly record struct OpenHarmonyContact(string Name, string Phone);

/// <summary>One calendar event as returned by <see cref="OpenHarmonyCalendar.ListUpcomingAsync"/>.</summary>
public readonly record struct OpenHarmonyCalendarEvent(string Title, DateTimeOffset Start, DateTimeOffset End);

/// <summary>Contact lookup over the OpenHarmony Contacts Kit (platform extra).</summary>
public static class OpenHarmonyContacts
{
    private const string HostLibrary = "libopenharmonyhost.so";

    private static readonly TimeSpan s_timeout = TimeSpan.FromSeconds(15);
    private static readonly ConcurrentDictionary<int, TaskCompletionSource<(int Code, string Payload)>> s_pending = new();
    private static ContactsResultCallback? s_callback;
    private static int s_nextId;
    private static bool s_registered;
    private static bool s_unavailable;

    [DllImport(HostLibrary, EntryPoint = "ohos_host_contacts_query", CharSet = CharSet.Ansi)]
    private static extern int ContactsQuery(int requestId, string namePrefix, int limit);

    [DllImport(HostLibrary, EntryPoint = "ohos_host_contacts_register_result")]
    private static extern void ContactsRegisterResult(IntPtr callback);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void ContactsResultCallback(int requestId, int code, IntPtr payloadUtf8);

    /// <summary>
    /// True when the host library answers the permission probe for READ_CONTACTS and no call has
    /// established that the kit/sink is unavailable. Off-device (no host library) this is false
    /// immediately; a denied permission or a kit failure flips it false and keeps it there.
    /// </summary>
    public static bool IsSupported =>
        !s_unavailable && OpenHarmonyBridge.CheckSelfPermission("ohos.permission.READ_CONTACTS");

    /// <summary>Parses the shell payload ("name\tphone" lines, '\n' separated).</summary>
    public static IReadOnlyList<OpenHarmonyContact> Parse(string? payload)
    {
        var records = new List<OpenHarmonyContact>();
        if (string.IsNullOrEmpty(payload))
        {
            return records;
        }
        foreach (string line in payload.Split('\n'))
        {
            if (line.Length == 0)
            {
                continue;
            }
            int separator = line.IndexOf('\t', StringComparison.Ordinal);
            string name = separator >= 0 ? line[..separator] : line;
            string phone = separator >= 0 ? line[(separator + 1)..] : string.Empty;
            records.Add(new OpenHarmonyContact(name, phone));
        }
        return records;
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
public static class OpenHarmonyCalendar
{
    private const string HostLibrary = "libopenharmonyhost.so";

    private static readonly TimeSpan s_timeout = TimeSpan.FromSeconds(15);
    private static readonly ConcurrentDictionary<int, TaskCompletionSource<(int Code, string Payload)>> s_pending = new();
    private static CalendarResultCallback? s_callback;
    private static int s_nextId;
    private static bool s_registered;
    private static bool s_unavailable;

    [DllImport(HostLibrary, EntryPoint = "ohos_host_calendar_list", CharSet = CharSet.Ansi)]
    private static extern int CalendarList(int requestId, int days);

    [DllImport(HostLibrary, EntryPoint = "ohos_host_calendar_add", CharSet = CharSet.Ansi)]
    private static extern int CalendarAdd(int requestId, string title, string startIso, string endIso);

    [DllImport(HostLibrary, EntryPoint = "ohos_host_calendar_register_result")]
    private static extern void CalendarRegisterResult(IntPtr callback);

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

    /// <summary>Parses the shell payload ("title\tstartIso\tendIso" lines, '\n' separated).</summary>
    public static IReadOnlyList<OpenHarmonyCalendarEvent> Parse(string? payload)
    {
        var records = new List<OpenHarmonyCalendarEvent>();
        if (string.IsNullOrEmpty(payload))
        {
            return records;
        }
        foreach (string line in payload.Split('\n'))
        {
            if (line.Length == 0)
            {
                continue;
            }
            string[] fields = line.Split('\t');
            if (fields.Length < 3 || !TryParseIso(fields[1], out DateTimeOffset start) ||
                !TryParseIso(fields[2], out DateTimeOffset end))
            {
                continue;
            }
            records.Add(new OpenHarmonyCalendarEvent(fields[0], start, end));
        }
        return records;
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
