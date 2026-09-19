// Launcher / Browser / Share for OpenHarmony through the ArkTS shell's startAbility bridge.
//
// The managed side forwards a request to the ArkTS shell through the native host
// (ohos_host_ability_start): kind 0 opens a URI (Launcher/Browser) with an implicit
// ohos.want.action.viewData Want, kind 1 shares plain text with ohos.want.action.sendData,
// and kind 2 is a no-launch availability probe used by Launcher.CanOpenAsync. The shell sink
// (host.registerAbilitySink) imports @ohos.app.ability.common plus @ohos.app.ability.Want and
// calls UIAbilityContext.startAbility.
//
// The host and the shell can only report whether the request was dispatched (the Want may still
// fail to match in the ability manager afterwards); every native call degrades to false / a
// completed task when libopenharmonyhost.so or the shell sink is unavailable (desktop builds,
// minimal shells), and nothing here ever throws for an unavailable platform.
//
// Known API limits, mirrored from the shell template:
// - BrowserLaunchOptions cannot be honoured: startAbility always opens the system browser, the
//   system in-app browser does not exist in this slice.
// - CanOpenAsync reports whether the ability bridge is available, not whether an installed
//   ability matches the URI (OpenHarmony has no synchronous URI-handler query here).
// - ShareTextRequest forwards Text (falling back to Uri); Subject/Title are not transmitted by
//   the Want bridge. ShareFileRequest/ShareMultipleFilesRequest are no-ops because the SDK in
//   use has no Share Kit (systemShare) and the sendData file path needs FD/URI permission flags
//   the current chain does not carry.
// - Plain-text sharing through ohos.want.action.sendData has no wantConstant key in this SDK;
//   the shell sends the text under 'ohos.extra.param.key.content', the key used by the
//   OpenHarmony ecosystem samples that predate Share Kit.
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Microsoft.Maui.ApplicationModel;
using Microsoft.Maui.ApplicationModel.DataTransfer;
using Microsoft.OpenHarmony.Hosting;

namespace Microsoft.Maui.Platform;

/// <summary>Forwards ability launches to the ArkTS shell sink; returns whether it was dispatched.</summary>
internal static class OpenHarmonyAbilityBridge
{
    private const string HostLibrary = "libopenharmonyhost.so";

    /// <summary>Launcher/Browser: open <c>uri</c> with an implicit viewData Want.</summary>
    private const int KindOpenUri = 0;

    /// <summary>Share: send <c>text</c> with an implicit sendData Want.</summary>
    private const int KindShareText = 1;

    /// <summary>Availability probe: the shell answers without launching anything.</summary>
    private const int KindProbe = 2;

    [DllImport(HostLibrary, EntryPoint = "ohos_host_ability_start")]
    private static extern int AbilityStart(int kind, string uri, string text);

    private static bool s_available = true;

    /// <summary>True when the host library exports the entry point and the shell sink is registered.</summary>
    public static bool IsAvailable => Dispatch(KindProbe, string.Empty, string.Empty);

    /// <summary>Asks the shell to open a URI; false when the bridge or the shell sink is unavailable.</summary>
    public static bool TryOpenUri(string uri) => Dispatch(KindOpenUri, uri, string.Empty);

    /// <summary>Asks the shell to share plain text; false when the bridge or the shell sink is unavailable.</summary>
    public static bool TryShareText(string text) => Dispatch(KindShareText, string.Empty, text);

    private static bool Dispatch(int kind, string uri, string text)
    {
        if (!s_available)
        {
            return false;
        }
        try
        {
            return AbilityStart(kind, uri, text) == 0;
        }
        catch (DllNotFoundException)
        {
            s_available = false;
            return false;
        }
        catch (EntryPointNotFoundException)
        {
            s_available = false;
            return false;
        }
    }
}

/// <summary>MAUI Essentials launcher on OpenHarmony (implicit viewData Want).</summary>
public sealed class OpenHarmonyLauncher : ILauncher
{
    public static readonly OpenHarmonyLauncher Instance = new();

    /// <summary>
    /// Reports whether the ability bridge is available; OpenHarmony offers no synchronous
    /// URI-handler query, so this cannot say whether an installed ability matches <paramref name="uri"/>.
    /// </summary>
    public Task<bool> CanOpenAsync(Uri uri) => Task.FromResult(uri is not null && OpenHarmonyAbilityBridge.IsAvailable);

    public Task<bool> OpenAsync(Uri uri)
    {
        if (uri is null)
        {
            return Task.FromResult(false);
        }
        bool dispatched = OpenHarmonyAbilityBridge.TryOpenUri(uri.AbsoluteUri);
        if (!dispatched)
        {
            OpenHarmonyBridge.WriteStatus($"[maui] launcher could not dispatch '{uri.AbsoluteUri}'");
        }
        return Task.FromResult(dispatched);
    }

    public Task<bool> OpenAsync(OpenFileRequest request)
    {
        string? path = request?.File?.FullPath;
        if (string.IsNullOrEmpty(path))
        {
            return Task.FromResult(false);
        }
        // file:// URIs are what viewData handlers expect; read permission flags are not part of
        // this bridge (the target ability fails to open the file when it is not world-readable).
        string location = Uri.TryCreate(path, UriKind.Absolute, out Uri? fileUri) && fileUri.IsFile
            ? fileUri.AbsoluteUri
            : path;
        bool dispatched = OpenHarmonyAbilityBridge.TryOpenUri(location);
        if (!dispatched)
        {
            OpenHarmonyBridge.WriteStatus("[maui] launcher could not dispatch the file request");
        }
        return Task.FromResult(dispatched);
    }

    public async Task<bool> TryOpenAsync(Uri uri)
    {
        // TryOpenAsync must not throw for URI schemes the platform cannot handle; the probe
        // reports bridge availability and the dispatch then reports the attempt.
        if (!await CanOpenAsync(uri).ConfigureAwait(false))
        {
            return false;
        }
        return await OpenAsync(uri).ConfigureAwait(false);
    }
}

/// <summary>MAUI Essentials browser on OpenHarmony (implicit viewData Want).</summary>
public sealed class OpenHarmonyBrowser : IBrowser
{
    public static readonly OpenHarmonyBrowser Instance = new();

    /// <summary>
    /// Opens <paramref name="uri"/> with the system handler. <paramref name="options"/> is
    /// accepted but not transmitted: the shell can only start an external ability, so
    /// BrowserLaunchMode/colors/title mode cannot be applied.
    /// </summary>
    public Task<bool> OpenAsync(Uri uri, BrowserLaunchOptions options)
    {
        if (uri is null)
        {
            return Task.FromResult(false);
        }
        bool dispatched = OpenHarmonyAbilityBridge.TryOpenUri(uri.AbsoluteUri);
        if (!dispatched)
        {
            OpenHarmonyBridge.WriteStatus($"[maui] browser could not dispatch '{uri.AbsoluteUri}'");
        }
        return Task.FromResult(dispatched);
    }
}

/// <summary>MAUI Essentials share on OpenHarmony (implicit sendData Want, plain text only).</summary>
public sealed class OpenHarmonyShare : IShare
{
    public static readonly OpenHarmonyShare Instance = new();

    public Task RequestAsync(ShareTextRequest request)
    {
        string? text = request?.Text;
        if (string.IsNullOrEmpty(text))
        {
            text = request?.Uri;
        }
        if (string.IsNullOrEmpty(text) || !OpenHarmonyAbilityBridge.TryShareText(text))
        {
            OpenHarmonyBridge.WriteStatus("[maui] share request could not be dispatched");
        }
        return Task.CompletedTask;
    }

    public Task RequestAsync(ShareFileRequest request)
    {
        // File sharing needs Share Kit (systemShare) or FD/URI permission flags, neither of
        // which the SDK in use exposes; the request completes as a documented no-op.
        OpenHarmonyBridge.WriteStatus("[maui] share file request needs Share Kit (not in this SDK)");
        return Task.CompletedTask;
    }

    public Task RequestAsync(ShareMultipleFilesRequest request)
    {
        OpenHarmonyBridge.WriteStatus("[maui] share multiple files request needs Share Kit (not in this SDK)");
        return Task.CompletedTask;
    }
}

/// <summary>Installs the launcher/browser/share implementations as the MAUI Essentials defaults.</summary>
internal static class OpenHarmonyAppLauncher
{
    /// <summary>
    /// Replaces the static backing fields of Launcher, Browser and Share (the entry points are
    /// get-only, so the fields are the settable surface - the same pattern as the sensors and
    /// TextToSpeech installers).
    /// </summary>
    public static void InstallDefaults()
    {
        InstallDefault(typeof(Launcher), OpenHarmonyLauncher.Instance);
        InstallDefault(typeof(Browser), OpenHarmonyBrowser.Instance);
        InstallDefault(typeof(Share), OpenHarmonyShare.Instance);
    }

    private static void InstallDefault(Type entry, object implementation)
    {
        try
        {
            const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
            foreach (FieldInfo field in entry.GetFields(flags))
            {
                if (field.FieldType.IsInstanceOfType(implementation))
                {
                    field.SetValue(null, implementation);
                }
            }
        }
        catch (Exception)
        {
            // The default stays in place when the entry point cannot be replaced.
        }
    }

    [ModuleInitializer]
    internal static void Initialize() => InstallDefaults();
}
