// Launcher / Browser / Share for OpenHarmony through the ArkTS shell's startAbility bridge.
//
// The managed side forwards a request to the ArkTS shell through the native host
// (ohos_host_ability_start / ohos_host_ability_start_ex): kind 0 opens a URI (Launcher/Browser)
// with an implicit ohos.want.action.viewData Want, kind 1 shares plain text with
// ohos.want.action.sendData, kind 2 is a no-launch availability probe used by
// Launcher.CanOpenAsync, and kind 3 shares a file with an implicit ohos.want.action.sendData
// Want (uri = file:// URI, type = MIME type, flags =
// wantConstant.Flags.FLAG_AUTH_READ_URI_PERMISSION). The shell sink
// (host.registerAbilitySink) imports @ohos.app.ability.common plus @ohos.app.ability.Want and
// calls UIAbilityContext.startAbility.
//
// The five-argument host export (ohos_host_ability_start_ex, reached through the same DllImport
// pair as the three-argument form) carries the optional content title and the Want flags:
// - Title: ShareTextRequest.Title/Subject and ShareFileRequest.Title (and OpenFileRequest.Title)
//   travel under wantConstant.Params.CONTENT_TITLE_KEY ('ohos.extra.param.key.contentTitle'),
//   which the shell sets on the Want when non-empty. An older host library without the
//   five-argument export only gets the three-argument call, and the dropped title is noted once.
// - Read grant: a file:// URI opened with kind 0 also sets
//   wantConstant.Flags.FLAG_AUTH_READ_URI_PERMISSION (passed through the flags argument and,
//   independently, applied by the shell for a file:// kind 0 uri), so the ability manager can
//   grant the receiver read access to the app-sandbox file. Kind 3 always carried it.
//
// The host and the shell can only report whether the request was dispatched (the Want may still
// fail to match in the ability manager afterwards); every native call degrades to false / a
// completed task when libopenharmonyhost.so or the shell sink is unavailable (desktop builds,
// minimal shells), and nothing here ever throws for an unavailable platform.
//
// Known API limits, mirrored from the shell template. The parts that cannot be expressed are
// reported once per process through the status channel (OpenHarmonyAbilityLog) instead of being
// dropped silently:
// - BrowserLaunchOptions: BrowserLaunchMode.External is exactly what startAbility does, so it
//   is honoured as-is. SystemPreferred (the MAUI default) asks for the system's in-app browser,
//   which does not exist in this slice, and the in-app-only knobs (TitleMode, the toolbar and
//   control colors, the launch flags) have no Want representation; such a request is still
//   dispatched to the external handler and noted once.
// - Sharing more than one file has no Want carrier (one uri per Want), so ShareMultipleFilesRequest
//   goes through the Share Kit (systemShare) host bridge when the shell registered the sink (an
//   HMS runtime with @kit.ShareKit; KIT-IMPL 2026-09-25); otherwise it stays the documented
//   no-op reported once per process.
// - CanOpenAsync reports whether the ability bridge is available, not whether an installed
//   ability matches the URI (OpenHarmony has no synchronous URI-handler query here).
// - File sharing (kind 3) goes through an implicit sendData Want because this SDK has no Share
//   Kit (systemShare); whether the receiver honours the read grant and can read the sandbox
//   file is device- and app-dependent.
// - Plain-text sharing through ohos.want.action.sendData has no wantConstant key for the body
//   in this SDK; the shell sends the text under 'ohos.extra.param.key.content', the key used by
//   the OpenHarmony ecosystem samples that predate Share Kit.
using System.Diagnostics.CodeAnalysis;
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

    /// <summary>ohos_host_ability_start_ex flags: bit 0 = FLAG_AUTH_READ_URI_PERMISSION.</summary>
    private const int FlagReadUriPermission = 0x1;

    [DllImport(HostLibrary, EntryPoint = "ohos_host_ability_start")]
    private static extern int AbilityStart(int kind, string uri, string text);

    [DllImport(HostLibrary, EntryPoint = "ohos_host_ability_start_ex")]
    private static extern int AbilityStartEx(int kind, string uri, string text, string title, int flags);

    private static bool s_available = true;
    private static bool s_exAvailable = true;

    /// <summary>True when the host library exports the entry point and the shell sink is registered.</summary>
    public static bool IsAvailable => Dispatch(KindProbe, string.Empty, string.Empty, string.Empty, 0);

    /// <summary>Asks the shell to open a URI; false when the bridge or the shell sink is unavailable.</summary>
    public static bool TryOpenUri(string uri, string? title = null)
        => Dispatch(KindOpenUri, uri, string.Empty, title ?? string.Empty,
            IsFileUri(uri) ? FlagReadUriPermission : 0);

    /// <summary>Asks the shell to share plain text; false when the bridge or the shell sink is unavailable.</summary>
    public static bool TryShareText(string text, string? title = null)
        => Dispatch(KindShareText, string.Empty, text, title ?? string.Empty, 0);

    /// <summary>
    /// Asks the shell to share one file: <paramref name="fileUri"/> is the file:// URI and
    /// <paramref name="mimeType"/> the Want type; false when the bridge or sink is unavailable.
    /// </summary>
    public static bool TryShareFile(string fileUri, string mimeType, string? title = null)
        => Dispatch(KindShareFile, fileUri, mimeType, title ?? string.Empty, 0);

    private static bool Dispatch(int kind, string uri, string text, string title, int flags)
    {
        if (!s_available)
        {
            return false;
        }
        if (s_exAvailable)
        {
            try
            {
                return AbilityStartEx(kind, uri, text, title, flags) == 0;
            }
            catch (DllNotFoundException)
            {
                // An older host library without the five-argument export: fall back below.
                s_exAvailable = false;
            }
            catch (EntryPointNotFoundException)
            {
                s_exAvailable = false;
            }
        }
        if (!string.IsNullOrEmpty(title))
        {
            // The three-argument form cannot carry the title; note it once instead of dropping
            // it silently (a five-argument host library would have transmitted it).
            OpenHarmonyAbilityLog.TitleDroppedOnce();
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

    private static bool IsFileUri(string uri) =>
        uri.StartsWith("file://", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// One-time status notes for the ability bridge's unexpressible parts. dotnet-status.txt is
/// bounded (256 KiB, oldest lines dropped), so a note per request would flood it; the same
/// once-per-process pattern the clipboard's non-text package note uses. The flags are plain
/// statics: a race can write the note twice, which is harmless for diagnostics.
/// </summary>
internal static class OpenHarmonyAbilityLog
{
    private static bool s_titleDropped;
    private static bool s_browserOptionsIgnored;
    private static bool s_multipleFilesDropped;

    /// <summary>
    /// A title/subject could not ride the three-argument ability sink: this host library lacks
    /// ohos_host_ability_start_ex, so wantConstant.Params.CONTENT_TITLE_KEY was not set.
    /// </summary>
    public static void TitleDroppedOnce()
    {
        if (s_titleDropped)
        {
            return;
        }
        s_titleDropped = true;
        OpenHarmonyBridge.WriteStatus(
            "[maui] ability bridge: Title/Subject was not transmitted (this host library has no ohos_host_ability_start_ex export); " +
            "the shell would set wantConstant.Params.CONTENT_TITLE_KEY ('ohos.extra.param.key.contentTitle') when it receives one");
    }

    /// <summary>The in-app browser and its options have no startAbility representation.</summary>
    public static void BrowserOptionsIgnoredOnce()
    {
        if (s_browserOptionsIgnored)
        {
            return;
        }
        s_browserOptionsIgnored = true;
        OpenHarmonyBridge.WriteStatus(
            "[maui] browser: the requested BrowserLaunchOptions were not applied (SystemPreferred in-app mode, title mode, colors and launch flags have no Want representation); the external system handler opens instead");
    }

    /// <summary>More than one shared file needs the Share Kit sink; the note is once per process.</summary>
    public static void MultipleFilesDroppedOnce()
    {
        if (s_multipleFilesDropped)
        {
            return;
        }
        s_multipleFilesDropped = true;
        OpenHarmonyBridge.WriteStatus(
            "[maui] share multiple files request needs the Share Kit (systemShare) sink; " +
            "the shell registers it only when the device runtime provides @kit.ShareKit, so this request stays a no-op");
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
        // file:// URIs are what viewData handlers expect; the bridge sets the read grant for a
        // file:// uri and carries OpenFileRequest.Title under CONTENT_TITLE_KEY (see the header).
        string location = Uri.TryCreate(path, UriKind.Absolute, out Uri? fileUri) && fileUri.IsFile
            ? fileUri.AbsoluteUri
            : path;
        bool dispatched = OpenHarmonyAbilityBridge.TryOpenUri(location, request!.Title);
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
    /// Opens <paramref name="uri"/> with the external system handler. LaunchMode.External is
    /// honoured exactly; SystemPreferred and the in-app-only options cannot be expressed by
    /// startAbility, so such a request is dispatched externally anyway and noted once.
    /// </summary>
    public Task<bool> OpenAsync(Uri uri, BrowserLaunchOptions options)
    {
        if (uri is null)
        {
            return Task.FromResult(false);
        }
        if (!IsExternalLaunch(options))
        {
            OpenHarmonyAbilityLog.BrowserOptionsIgnoredOnce();
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

    /// <summary>
    /// True when the options describe exactly the external launch this bridge performs: the
    /// External mode with no in-app-only knobs set. The in-app knobs (title mode, colors, launch
    /// flags) only apply to SystemPreferred browsers, per the Essentials contract.
    /// </summary>
    private static bool IsExternalLaunch(BrowserLaunchOptions? options) =>
        options is null ||
        (options.LaunchMode == BrowserLaunchMode.External &&
         options.TitleMode == BrowserTitleMode.Default &&
         options.PreferredToolbarColor is null &&
         options.PreferredControlColor is null &&
         options.Flags == BrowserLaunchFlags.None);
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
        // The content title travels under CONTENT_TITLE_KEY (Title first, then Subject); with a
        // host library that has no five-argument export the title is noted once (see Dispatch).
        string title = !string.IsNullOrEmpty(request?.Title)
            ? request!.Title
            : (request?.Subject ?? string.Empty);
        if (string.IsNullOrEmpty(text) || !OpenHarmonyAbilityBridge.TryShareText(text, title))
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
        if (!OpenHarmonyAbilityBridge.TryShareFile(FileUriForPath(path), MimeTypeForPath(path), request!.Title))
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
            // The Share Kit (systemShare) sink carries the whole set when the shell registered it
            // (an HMS runtime with @kit.ShareKit; KIT-IMPL 2026-09-25). The default OpenHarmony
            // shell registers no sink, the bridge answers false and the request keeps the
            // documented no-op note instead of silently sharing only the first file; the note is
            // once per process, not per request.
            var uris = new List<string>(files.Count);
            foreach (var file in files)
            {
                string? sharedPath = file?.FullPath;
                if (!string.IsNullOrEmpty(sharedPath))
                {
                    uris.Add(FileUriForPath(sharedPath!));
                }
            }
            if (uris.Count == files.Count && OpenHarmonyShareKitBridge.TryShare(uris, request!.Title))
            {
                return Task.CompletedTask;
            }
            OpenHarmonyAbilityLog.MultipleFilesDroppedOnce();
            return Task.CompletedTask;
        }
        string? path = files[0]?.FullPath;
        if (string.IsNullOrEmpty(path))
        {
            OpenHarmonyBridge.WriteStatus("[maui] share multiple files request had no file path");
            return Task.CompletedTask;
        }
        if (!OpenHarmonyAbilityBridge.TryShareFile(FileUriForPath(path), MimeTypeForPath(path), request!.Title))
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

    private static void InstallDefault(
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicFields | DynamicallyAccessedMemberTypes.NonPublicFields)]
        Type entry, object implementation)
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
