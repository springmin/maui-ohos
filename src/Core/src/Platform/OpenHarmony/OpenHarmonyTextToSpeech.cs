// TextToSpeech for OpenHarmony through the CoreSpeechKit bridge (A2-TTS 2026-09-27).
//
//   SpeakAsync -> ohos_host_tts_request(id, 1, {"text","locale"}) -> the ArkTS shell's
//     registerTtsSink handler creates (or reuses) the engine with textToSpeech.createEngine
//     (offline mode, person 0), calls engine.speak with the managed request id and answers when
//     the engine reports onComplete/onStop/onError through host.notifyTtsResult ->
//     ohos_host_tts_result -> the registered managed callback.
//   GetLocalesAsync -> op 3 listVoices; the shell serializes the VoiceInfo list and the managed
//     side maps it to Locale values. Without the sink it answers the current device locale,
//     exactly like the pre-kit stub did.
//   Stop -> op 2 engine.stop(); the shell also resolves every pending speak, so an awaiting
//     SpeakAsync completes instead of hanging.
//   IsSupported asks ohos_host_tts_available() and never creates an engine.
//
// The shell registers the sink only when canIUse('SystemCapability.AI.TextToSpeech') passes and
// its runtime resolves @kit.CoreSpeechKit (the variable-specifier probe in the shell templates),
// so the default OpenHarmony-shell build (and the desktop harness, where libopenharmonyhost.so
// is absent) registers nothing: every call keeps the documented stub semantics and nothing
// throws - SpeakAsync returns without speaking, GetLocalesAsync answers the device locale,
// Stop is a no-op and IsSupported is false. On an HMS device the kit itself gates on the
// device's speech capability and answers its BusinessError codes (1002300001 text out of range,
// 1002300002 language not supported, 1002300003 person not supported, 401 argument error; plus
// 1002300005 engine creation failed), which are reported through the bridge status log.
using System.Collections.Concurrent;
using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Microsoft.Maui.Media;
using Microsoft.OpenHarmony.Hosting;

namespace Microsoft.Maui.Platform;

/// <summary>
/// The local (non-kit) outcomes of one TextToSpeech bridge call. Positive values coming back
/// from the shell are the CoreSpeechKit BusinessError codes and pass through unchanged.
/// </summary>
internal enum OpenHarmonyTextToSpeechStatus
{
    /// <summary>The operation completed (speak: the engine finished or stopped).</summary>
    Success = 0,

    /// <summary>No host library, or the shell did not register the CoreSpeechKit sink.</summary>
    Unavailable = -1,

    /// <summary>The shell's kit call failed, the engine is missing or the arguments were malformed.</summary>
    CallFailed = -2,

    /// <summary>The shell did not answer within the bridge's time budget.</summary>
    TimedOut = -100,

    /// <summary>The caller's cancellation token fired.</summary>
    Cancelled = -101,
}

public sealed partial class OpenHarmonyTextToSpeech : ITextToSpeech
{
    public static readonly OpenHarmonyTextToSpeech Instance = new();

    private const string HostLibrary = "libopenharmonyhost.so";

    /// <summary>Shell op codes (see the host protocol in openharmony_host.h).</summary>
    private const int OpCreate = 0;
    private const int OpSpeak = 1;
    private const int OpStop = 2;
    private const int OpLocales = 3;
    private const int OpBusy = 4;

    /// <summary>Time budget for one speech; the engine answers when the utterance finished.</summary>
    private static readonly TimeSpan s_speakTimeout = TimeSpan.FromSeconds(60);

    /// <summary>Time budget for the voice list (one kit round trip).</summary>
    private static readonly TimeSpan s_localesTimeout = TimeSpan.FromSeconds(30);

    /// <summary>Forwards one operation to the ArkTS sink; returns 0 when it was queued.</summary>
    [LibraryImport(HostLibrary, EntryPoint = "ohos_host_tts_request", StringMarshalling = StringMarshalling.Utf8)]
    private static partial int TtsRequest(int requestId, int op, string args);

    /// <summary>1 when the shell registered the CoreSpeechKit sink (never creates an engine).</summary>
    [LibraryImport(HostLibrary, EntryPoint = "ohos_host_tts_available", StringMarshalling = StringMarshalling.Utf8)]
    private static partial int TtsAvailable();

    /// <summary>Registers the callback the host uses to complete a pending request.</summary>
    [LibraryImport(HostLibrary, EntryPoint = "ohos_host_tts_register_result", StringMarshalling = StringMarshalling.Utf8)]
    private static partial void TtsRegisterResult(IntPtr callback);

    // Informative declaration of the native callback shape; the pointer is taken from
    // OnResultNative below (the delegate is never instantiated).
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void TtsResultCallback(int requestId, int op, int code, IntPtr payloadUtf8);

    private static readonly ConcurrentDictionary<int, TaskCompletionSource<(int Code, string Payload)>> s_pending = new();
    private static unsafe IntPtr s_callback = (IntPtr)(delegate* unmanaged[Cdecl]<int, int, int, IntPtr, void>)&OnResultNative;
    private static int s_nextId;
    private static bool s_registered;
    private static bool s_unavailable;

    /// <summary>The pending request ids (diagnostics/testing).</summary>
    internal static int PendingCount => s_pending.Count;

    /// <summary>True once the native host (or the shell sink) was found to be unavailable.</summary>
    internal static bool IsUnavailable => s_unavailable;

    /// <summary>
    /// True when the host library exports the TTS bridge and the shell registered the sink (a
    /// runtime with the text-to-speech syscap and @kit.CoreSpeechKit), and no call has
    /// established that the kit is unavailable. The probe never creates an engine and answers
    /// false off-device instead of throwing.
    /// </summary>
    public static bool IsSupported
    {
        get
        {
            if (s_unavailable)
            {
                return false;
            }
            try
            {
                return TtsAvailable() == 1;
            }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
            {
                s_unavailable = true;
                return false;
            }
        }
    }

    /// <summary>The shell reports results through the host into this method. Must not throw.</summary>
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    internal static void OnResultNative(int requestId, int op, int code, IntPtr payloadUtf8)
    {
        if (!s_pending.TryRemove(requestId, out TaskCompletionSource<(int Code, string Payload)>? source))
        {
            return;
        }
        string payload = payloadUtf8 != IntPtr.Zero
            ? Marshal.PtrToStringUTF8(payloadUtf8) ?? string.Empty
            : string.Empty;
        source.TrySetResult((code, payload));
    }

    /// <summary>
    /// Enumerates the voices the CoreSpeechKit engine reports. Without the kit (or when the
    /// shell cannot list voices) the current device locale is reported as the only available
    /// locale, matching the pre-kit stub; GetLocalesAsync never throws.
    /// </summary>
    public async Task<IEnumerable<Locale>> GetLocalesAsync()
    {
        if (!s_unavailable)
        {
            (int code, string payload) = await SendAsync(OpLocales, string.Empty, s_localesTimeout).ConfigureAwait(false);
            if (code == (int)OpenHarmonyTextToSpeechStatus.Success && payload.Length > 0)
            {
                List<Locale>? locales = ParseLocales(payload);
                if (locales is not null && locales.Count > 0)
                {
                    return locales;
                }
            }
        }
        return new[] { DeviceLocale() };
    }

    /// <summary>
    /// Asks the ArkTS shell to speak <paramref name="text"/>. The call completes when the engine
    /// reports completion or stop, on cancellation, or after the bridge timeout; requests
    /// answered with a non-zero code - or requests that no shell answers - complete silently
    /// (TextToSpeech must not throw when the platform lacks a speech engine).
    /// </summary>
    public async Task SpeakAsync(string text, SpeechOptions? options = null, CancellationToken cancelToken = default)
    {
        if (string.IsNullOrEmpty(text) || s_unavailable)
        {
            return;
        }
        int id = Interlocked.Increment(ref s_nextId);
        var source = new TaskCompletionSource<(int Code, string Payload)>(TaskCreationOptions.RunContinuationsAsynchronously);
        s_pending[id] = source;
        try
        {
            EnsureRegistered();
            if (s_unavailable || TtsRequest(id, OpSpeak, SpeakArgs(text, options?.Locale)) != 0)
            {
                s_pending.TryRemove(id, out _);
                s_unavailable = true;
                OpenHarmonyBridge.WriteStatus("[maui] text-to-speech sink is not available (no CoreSpeechKit on this shell/device)");
                return;
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            s_pending.TryRemove(id, out _);
            s_unavailable = true;
            OpenHarmonyBridge.WriteStatus("[maui] text-to-speech bridge unavailable (no host library)");
            return;
        }
        using CancellationTokenRegistration registration = cancelToken.CanBeCanceled
            ? cancelToken.Register(() =>
            {
                source.TrySetCanceled(cancelToken);
                Stop();
            })
            : default;
        Task completed = await Task.WhenAny(source.Task, Task.Delay(s_speakTimeout, CancellationToken.None)).ConfigureAwait(false);
        if (completed != source.Task)
        {
            s_pending.TryRemove(id, out _);
            OpenHarmonyBridge.WriteStatus("[maui] speech request timed out");
            Stop();
            return;
        }
        try
        {
            (int code, _) = await source.Task.ConfigureAwait(false);
            if (code != (int)OpenHarmonyTextToSpeechStatus.Success)
            {
                OpenHarmonyBridge.WriteStatus($"[maui] speech request answered status {code}");
            }
        }
        catch (OperationCanceledException)
        {
            // Cancellation completes the call without an exception (platform contract).
        }
    }

    /// <summary>
    /// Asks the platform to stop the current speech (op 2). This is not part of
    /// <see cref="ITextToSpeech"/>; a device without the CoreSpeechKit sink ignores the request
    /// without throwing. Pending <see cref="SpeakAsync"/> calls complete because the shell
    /// resolves them when the engine stops.
    /// </summary>
    public void Stop()
    {
        if (s_unavailable)
        {
            return;
        }
        try
        {
            EnsureRegistered();
            if (s_unavailable || TtsRequest(Interlocked.Increment(ref s_nextId), OpStop, string.Empty) != 0)
            {
                s_unavailable = true;
                OpenHarmonyBridge.WriteStatus("[maui] text-to-speech sink is not available (stop ignored)");
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            s_unavailable = true;
            OpenHarmonyBridge.WriteStatus("[maui] text-to-speech bridge unavailable (stop ignored)");
        }
    }

    // Queues one operation and completes with the shell's answer. Every failure path
    // (unregistered sink, missing export, timeout) answers a local status instead of throwing.
    private static async Task<(int Code, string Payload)> SendAsync(int op, string args, TimeSpan timeout)
    {
        if (s_unavailable)
        {
            return ((int)OpenHarmonyTextToSpeechStatus.Unavailable, string.Empty);
        }
        int requestId = Interlocked.Increment(ref s_nextId);
        var source = new TaskCompletionSource<(int Code, string Payload)>(TaskCreationOptions.RunContinuationsAsynchronously);
        s_pending[requestId] = source;
        try
        {
            EnsureRegistered();
            if (s_unavailable || TtsRequest(requestId, op, args) != 0)
            {
                s_pending.TryRemove(requestId, out _);
                s_unavailable = true;
                OpenHarmonyBridge.WriteStatus("[maui] text-to-speech sink is not available (no CoreSpeechKit on this shell/device)");
                return ((int)OpenHarmonyTextToSpeechStatus.Unavailable, string.Empty);
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            s_pending.TryRemove(requestId, out _);
            s_unavailable = true;
            OpenHarmonyBridge.WriteStatus("[maui] text-to-speech bridge unavailable (no host library)");
            return ((int)OpenHarmonyTextToSpeechStatus.Unavailable, string.Empty);
        }
        Task completed = await Task.WhenAny(source.Task, Task.Delay(timeout, CancellationToken.None)).ConfigureAwait(false);
        if (completed != source.Task)
        {
            s_pending.TryRemove(requestId, out _);
            OpenHarmonyBridge.WriteStatus("[maui] text-to-speech request timed out");
            return ((int)OpenHarmonyTextToSpeechStatus.TimedOut, string.Empty);
        }
        try
        {
            return await source.Task.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return ((int)OpenHarmonyTextToSpeechStatus.Cancelled, string.Empty);
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
            TtsRegisterResult(s_callback);
            s_registered = true;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            s_unavailable = true;
            OpenHarmonyBridge.WriteStatus("[maui] text-to-speech bridge unavailable (no host library)");
        }
    }

    // The shell's voice list: [{"language","person","style","gender","description"}, ...]. One
    // Locale per language (the first voice wins); an unusable payload answers null so the caller
    // keeps the device-locale fallback. JsonDocument keeps the path trim/AOT friendly.
    private static List<Locale>? ParseLocales(string payload)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(payload);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                return null;
            }
            var locales = new List<Locale>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (JsonElement voice in document.RootElement.EnumerateArray())
            {
                string id = ReadString(voice, "language");
                if (id.Length == 0 || !seen.Add(id))
                {
                    continue;
                }
                string description = ReadString(voice, "description");
                string gender = ReadString(voice, "gender");
                string name = description.Length > 0 ? description : (gender.Length > 0 ? $"{id} ({gender})" : id);
                (string language, string country) = SplitLanguage(id);
                locales.Add(new Locale(language, country, name, id));
            }
            return locales;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string ReadString(JsonElement element, string property)
    {
        return element.TryGetProperty(property, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;
    }

    // "zh-CN" -> language "zh", country "CN"; a language without a region answers the upper-cased
    // language as the country, mirroring the pre-kit fallback below.
    private static (string Language, string Country) SplitLanguage(string id)
    {
        int separator = id.IndexOf('-', StringComparison.Ordinal);
        if (separator <= 0 || separator == id.Length - 1)
        {
            string single = id.ToLowerInvariant();
            return (single, single.ToUpperInvariant());
        }
        return (id[..separator].ToLowerInvariant(), id[(separator + 1)..].ToUpperInvariant());
    }

    // The pre-kit fallback: the current UI culture as the single available locale.
    private static Locale DeviceLocale()
    {
        CultureInfo culture = CultureInfo.CurrentUICulture;
        string id = string.IsNullOrEmpty(culture.Name) ? "en-US" : culture.Name;
        string language = string.IsNullOrEmpty(culture.TwoLetterISOLanguageName)
            ? "en"
            : culture.TwoLetterISOLanguageName;
        string country = id.Contains('-', StringComparison.Ordinal) ? id.Split('-')[^1].ToUpperInvariant() : language.ToUpperInvariant();
        string name = string.IsNullOrEmpty(culture.EnglishName) ? id : culture.EnglishName;
        return new Locale(language, country, name, id);
    }

    // op 1 argument JSON: {"text":"...","locale":"..."} (the locale is the kit language, "" lets
    // the shell pick its documented zh-CN default). Built by hand (invariant culture) so the
    // shell's JSON.parse sees JSON strings; the payload stays trim/AOT friendly.
    private static string SpeakArgs(string text, Locale? locale)
    {
        string selected = locale?.Id ?? string.Empty;
        if (selected.Length == 0)
        {
            selected = locale?.Language ?? string.Empty;
        }
        StringBuilder builder = new(text.Length + selected.Length + 24);
        builder.Append("{\"text\":\"");
        AppendEscaped(builder, text);
        builder.Append("\",\"locale\":\"");
        AppendEscaped(builder, selected);
        builder.Append("\"}");
        return builder.ToString();
    }

    // Minimal JSON string escaping for the text/locale fields ('"', '\', control characters).
    private static void AppendEscaped(StringBuilder builder, string value)
    {
        foreach (char c in value)
        {
            switch (c)
            {
                case '"':
                    builder.Append("\\\"");
                    break;
                case '\\':
                    builder.Append("\\\\");
                    break;
                case '\n':
                    builder.Append("\\n");
                    break;
                case '\r':
                    builder.Append("\\r");
                    break;
                case '\t':
                    builder.Append("\\t");
                    break;
                default:
                    if (c < ' ')
                    {
                        builder.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                    }
                    else
                    {
                        builder.Append(c);
                    }
                    break;
            }
        }
    }

    /// <summary>Installs this implementation as the MAUI Essentials TextToSpeech default.</summary>
    public static void InstallDefault()
    {
        try
        {
            const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
            // The entry point is get-only, so the backing field is the settable surface.
            foreach (FieldInfo field in typeof(TextToSpeech).GetFields(flags))
            {
                if (field.FieldType.IsInstanceOfType(Instance))
                {
                    field.SetValue(null, Instance);
                }
            }
        }
        catch (Exception)
        {
            // The default stays in place when the entry point cannot be replaced.
        }
    }

    [ModuleInitializer]
    internal static void Initialize() => InstallDefault();
}
