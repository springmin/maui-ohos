// Launcher / Browser / Share for OpenHarmony through the ArkTS shell's startAbility bridge.
//
// The managed side forwards a request to the ArkTS shell through the native host
// (ohos_host_ability_start): kind 0 opens a URI (Launcher/Browser) with an implicit
// ohos.want.action.viewData Want, kind 1 shares plain text with ohos.want.action.sendData,
// kind 2 is a no-launch availability probe used by Launcher.CanOpenAsync, and kind 3 shares a
// file with an implicit ohos.want.action.sendData Want (uri = file:// URI, type = MIME type,
// flags = wantConstant.Flags.FLAG_AUTH_READ_URI_PERMISSION). The shell sink
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
//   the Want bridge.
// - File sharing (kind 3) goes through an implicit sendData Want because this SDK has no Share
//   Kit (systemShare). The shell sets wantConstant.Flags.FLAG_AUTH_READ_URI_PERMISSION
//   (declared as 0x1 in @ohos.app.ability.wantConstant, verified by typecheck) so the ability
//   manager can grant the chosen receiver read access to the file:// URI; whether the receiver
//   honours the grant and can read the sandbox file is device- and app-dependent.
// - The bridge carries one URI per Want, so ShareMultipleFilesRequest dispatches only when it
//   holds exactly one file; with more files it stays a documented no-op (the missing Share Kit
//   is the multi-file carrier on this platform).
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

    /// <summary>Share: send a file URI plus its MIME type with an implicit sendData Want.</summary>
    private const int KindShareFile = 3;

    [DllImport(HostLibrary, EntryPoint = "ohos_host_ability_start")]
    private static extern int AbilityStart(int kind, string uri, string text);

    private static bool s_available = true;

    /// <summary>True when the host library exports the entry point and the shell sink is registered.</summary>
    public static bool IsAvailable => Dispatch(KindProbe, string.Empty, string.Empty);

    /// <summary>Asks the shell to open a URI; false when the bridge or the shell sink is unavailable.</summary>
    public static bool TryOpenUri(string uri) => Dispatch(KindOpenUri, uri, string.Empty);

    /// <summary>Asks the shell to share plain text; false when the bridge or the shell sink is unavailable.</summary>
    public static bool TryShareText(string text) => Dispatch(KindShareText, string.Empty, text);

    /// <summary>
    /// Asks the shell to share one file: <paramref name="fileUri"/> is the file:// URI and
    /// <paramref name="mimeType"/> the Want type; false when the bridge or sink is unavailable.
    /// </summary>
    public static bool TryShareFile(string fileUri, string mimeType) => Dispatch(KindShareFile, fileUri, mimeType);

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
            // The URI is stripped of its query/fragment and truncated (B7) before it reaches
            // dotnet-status.txt; the dispatched payload keeps the full URI.
            string loggedUri = OpenHarmonyWebViewHandler.SanitizeUrlForLog(uri.AbsoluteUri);
            OpenHarmonyBridge.WriteStatus($"[maui] launcher could not dispatch '{loggedUri}'");
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
            // Same B7 rule as the launcher: the log gets scheme+host+path, capped; the
            // dispatched payload keeps the full URI.
            string loggedUri = OpenHarmonyWebViewHandler.SanitizeUrlForLog(uri.AbsoluteUri);
            OpenHarmonyBridge.WriteStatus($"[maui] browser could not dispatch '{loggedUri}'");
        }
        return Task.FromResult(dispatched);
    }
}

/// <summary>MAUI Essentials share on OpenHarmony (implicit sendData Want: text or one file).</summary>
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
        string? path = request?.File?.FullPath;
        if (string.IsNullOrEmpty(path))
        {
            OpenHarmonyBridge.WriteStatus("[maui] share file request had no file path");
            return Task.CompletedTask;
        }
        if (!OpenHarmonyAbilityBridge.TryShareFile(FileUriForPath(path), MimeTypeForPath(path)))
        {
            OpenHarmonyBridge.WriteStatus($"[maui] share file request could not be dispatched ({MimeTypeForPath(path)})");
        }
        return Task.CompletedTask;
    }

    public Task RequestAsync(ShareMultipleFilesRequest request)
    {
        var files = request?.Files;
        if (files is null || files.Count == 0)
        {
            OpenHarmonyBridge.WriteStatus("[maui] share multiple files request had no files");
            return Task.CompletedTask;
        }
        if (files.Count > 1)
        {
            // One Want carries one uri and this SDK has no Share Kit (systemShare) to carry a
            // set, so the multi-file request stays a documented no-op instead of silently
            // sharing only the first file.
            OpenHarmonyBridge.WriteStatus($"[maui] share multiple files request ({files.Count} files) needs Share Kit (not in this SDK)");
            return Task.CompletedTask;
        }
        string? path = files[0]?.FullPath;
        if (string.IsNullOrEmpty(path))
        {
            OpenHarmonyBridge.WriteStatus("[maui] share multiple files request had no file path");
            return Task.CompletedTask;
        }
        if (!OpenHarmonyAbilityBridge.TryShareFile(FileUriForPath(path), MimeTypeForPath(path)))
        {
            OpenHarmonyBridge.WriteStatus($"[maui] share file request could not be dispatched ({MimeTypeForPath(path)})");
        }
        return Task.CompletedTask;
    }

    /// <summary>
    /// file:// URI for an absolute app-sandbox path ("file://" + path, so /data/... becomes
    /// file:///data/...); a path that already carries the scheme passes through. The
    /// bundle-qualified form produced by fileUri.getUriFromPath is not used because the
    /// managed side does not know the bundle name; see the file header for the device-only
    /// uncertainty around the receiver's read grant.
    /// </summary>
    internal static string FileUriForPath(string path)
    {
        if (path.StartsWith("file://", StringComparison.OrdinalIgnoreCase))
        {
            return path;
        }
        return path.StartsWith("/", StringComparison.Ordinal) ? "file://" + path : "file:///" + path;
    }

    /// <summary>
    /// Want type for a file, chosen from the extension (lower-cased): a small explicit map of
    /// common document/image/audio/video/archive types, */* for anything else (the shell sends
    /// the result as the Want's type field).
    /// </summary>
    internal static string MimeTypeForPath(string path)
    {
        switch (System.IO.Path.GetExtension(path).ToLowerInvariant())
        {
            case ".txt":
            case ".log":
                return "text/plain";
            case ".csv":
                return "text/csv";
            case ".html":
            case ".htm":
                return "text/html";
            case ".json":
                return "application/json";
            case ".xml":
                return "application/xml";
            case ".pdf":
                return "application/pdf";
            case ".png":
                return "image/png";
            case ".jpg":
            case ".jpeg":
                return "image/jpeg";
            case ".gif":
                return "image/gif";
            case ".webp":
                return "image/webp";
            case ".mp3":
                return "audio/mpeg";
            case ".wav":
                return "audio/wav";
            case ".mp4":
                return "video/mp4";
            case ".zip":
                return "application/zip";
            case ".doc":
                return "application/msword";
            case ".docx":
                return "application/vnd.openxmlformats-officedocument.wordprocessingml.document";
            case ".xls":
                return "application/vnd.ms-excel";
            case ".xlsx":
                return "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";
            case ".ppt":
                return "application/vnd.ms-powerpoint";
            case ".pptx":
                return "application/vnd.openxmlformats-officedocument.presentationml.presentation";
            default:
                return "*/*";
        }
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
