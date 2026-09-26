// TextToSpeech for OpenHarmony through the Speech Kit bridge.
//
// The managed side forwards a speak request to the ArkTS shell through the native host
// (ohos_host_tts_speak); the shell's registerTtsSink handler owns the engine call and answers
// with ohos_host_tts_result(requestId, code) (host.notifyTtsResult) so SpeakAsync can complete.
// The OpenHarmony SDK this slice builds against does not declare a speech synthesis module
// (@kit.CoreSpeechKit and @ohos.ai.tts are both absent from ets/kits and ets/api), so the shell
// sink currently reports "unavailable" (code -1) exactly like the keystore sink; the managed
// side then degrades without ever throwing. GetLocalesAsync cannot enumerate an engine either,
// so it reports the current device locale (CultureInfo.CurrentUICulture) as a single Locale.
using System.Collections.Concurrent;
using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Microsoft.Maui.Media;
using Microsoft.OpenHarmony.Hosting;

namespace Microsoft.Maui.Platform;

public sealed partial class OpenHarmonyTextToSpeech : ITextToSpeech
{
    public static readonly OpenHarmonyTextToSpeech Instance = new();

    private const string HostLibrary = "libopenharmonyhost.so";

    /// <summary>Forwards the request to the ArkTS sink; returns 0 when it was dispatched.</summary>
    [LibraryImport(HostLibrary, EntryPoint = "ohos_host_tts_speak", StringMarshalling = StringMarshalling.Utf8)]
    private static partial int TtsSpeak(int requestId, string text, string locale);

    /// <summary>Registers the callback the host uses to complete a pending request.</summary>
    [LibraryImport(HostLibrary, EntryPoint = "ohos_host_tts_register_result")]
    private static partial void TtsRegisterResult(IntPtr callback);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void TtsResultCallback(int requestId, int code);

    private static readonly ConcurrentDictionary<int, TaskCompletionSource<bool>> s_pending = new();
    private static unsafe IntPtr s_callback = (IntPtr)(delegate* unmanaged[Cdecl]<int, int, void>)&Complete;
    private static int s_nextId;
    private static bool s_registered;
    private static bool s_unavailable;

    /// <summary>The pending request ids (diagnostics/testing).</summary>
    internal static int PendingCount => s_pending.Count;

    /// <summary>True once the native host (or the shell sink) was found to be unavailable.</summary>
    internal static bool IsUnavailable => s_unavailable;

    /// <summary>The shell reports results through the host into this method.</summary>
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    internal static void Complete(int requestId, int code)
    {
        if (s_pending.TryRemove(requestId, out TaskCompletionSource<bool>? source))
        {
            source.TrySetResult(code == 0);
        }
    }

    /// <summary>
    /// The Speech Kit engine enumerates voices, but no engine enumeration is exposed without the
    /// kit, so the device locale is reported as the only available locale.
    /// </summary>
    public Task<IEnumerable<Locale>> GetLocalesAsync()
    {
        CultureInfo culture = CultureInfo.CurrentUICulture;
        string id = string.IsNullOrEmpty(culture.Name) ? "en-US" : culture.Name;
        string language = string.IsNullOrEmpty(culture.TwoLetterISOLanguageName)
            ? "en"
            : culture.TwoLetterISOLanguageName;
        string country = id.Contains('-', StringComparison.Ordinal) ? id.Split('-')[^1].ToUpperInvariant() : language.ToUpperInvariant();
        string name = string.IsNullOrEmpty(culture.EnglishName) ? id : culture.EnglishName;
        return Task.FromResult<IEnumerable<Locale>>(new[] { new Locale(language, country, name, id) });
    }

    /// <summary>
    /// Asks the ArkTS shell to speak <paramref name="text"/>. Requests answered with a non-zero
    /// code - or requests that no shell answers - complete silently (TextToSpeech must not throw
    /// when the platform lacks a speech engine).
    /// </summary>
    public async Task SpeakAsync(string text, SpeechOptions? options = null, CancellationToken cancelToken = default)
    {
        if (string.IsNullOrEmpty(text) || s_unavailable)
        {
            return;
        }
        int id = Interlocked.Increment(ref s_nextId);
        var source = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        s_pending[id] = source;
        try
        {
            EnsureRegistered();
            int rc = TtsSpeak(id, text, options?.Locale?.Id ?? string.Empty);
            if (rc != 0)
            {
                s_pending.TryRemove(id, out _);
                OpenHarmonyBridge.WriteStatus("[maui] speech sink is not available");
                return;
            }
        }
        catch (DllNotFoundException)
        {
            s_pending.TryRemove(id, out _);
            s_unavailable = true;
            return;
        }
        catch (EntryPointNotFoundException)
        {
            s_pending.TryRemove(id, out _);
            s_unavailable = true;
            return;
        }
        // The shell answers when the engine finished (or immediately with a failure code);
        // the timeout is only a safety net for a sink that never answers.
        using CancellationTokenRegistration registration = cancelToken.CanBeCanceled
            ? cancelToken.Register(() => source.TrySetCanceled(cancelToken))
            : default;
        Task completed = await Task.WhenAny(source.Task, Task.Delay(TimeSpan.FromSeconds(60))).ConfigureAwait(false);
        if (completed != source.Task)
        {
            s_pending.TryRemove(id, out _);
            OpenHarmonyBridge.WriteStatus("[maui] speech request timed out");
            return;
        }
        try
        {
            await source.Task.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Cancellation completes the call without an exception (platform contract).
        }
    }

    private static void EnsureRegistered()
    {
        if (s_registered)
        {
            return;
        }
        s_registered = true;
        TtsRegisterResult(s_callback);
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
