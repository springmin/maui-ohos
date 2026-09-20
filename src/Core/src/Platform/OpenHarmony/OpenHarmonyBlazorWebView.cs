// BlazorWebView readiness for OpenHarmony, milestone 1: the asset path mapping.
//
// The Microsoft.AspNetCore.Components.WebView.Maui package restores from the configured
// dotnet-public feed at the same version the slice pins for Microsoft.Maui.Core
// (11.0.0-rc.1.26451.6) and ships a plain net11.0 lib, but the slice project does not take that
// package reference yet, so this file deliberately has no compile-time dependency on the Blazor
// types. It is the path half milestone 2 needs: BlazorWebView serves app content from the app
// origin (BlazorWebViewHandler.AppOrigin, https://0.0.0.0/) out of a content root (the directory
// part of HostPage, by default "wwwroot"), and the framework assets (_framework/blazor.webview.js,
// the dotnet runtime, the app's static web assets) are copied into that same root by the package's
// ConvertStaticWebAssetsToMauiAssets target. That is exactly the layout the ArkTS shell already
// serves for the hybrid origin: a base directory (the extracted app payload,
// OpenHarmonyBridge.Context.AppDir), a content root and a default file.
//
//   OpenHarmonyBlazorWebView.ResolveContentRoot(appDir)             -> <appDir>/wwwroot
//   OpenHarmonyBlazorWebView.ResolveAssetPath(appDir, requestPath)  -> the file under it
//   OpenHarmonyBlazorWebView.IsFrameworkRequest(requestPath)        -> "_framework/..." request
//
// Milestone 2 (the OpenHarmony BlazorWebView handler partial, the WebViewManager, the asset
// provider over these paths and the window.external init script) starts by adding the package
// reference to the slice project; handler registration is intentionally not stubbed here because
// the Blazor types are not referenceable from this file yet.
namespace Microsoft.Maui.Platform;

/// <summary>
/// Maps the app-origin requests a BlazorWebView makes to the app package files the ArkTS shell
/// serves. Milestone 1 of the BlazorWebView port: no Blazor package types are referenced, so the
/// helpers can be used (and tested) before the slice project takes the package reference.
/// </summary>
public static class OpenHarmonyBlazorWebView
{
    /// <summary>Origin the BlazorWebView loads app content from (BlazorWebViewHandler.AppOrigin).</summary>
    public const string AppOrigin = "https://0.0.0.0/";

    /// <summary>Default Blazor content root: the directory part of <c>IBlazorWebView.HostPage</c>.</summary>
    public const string ContentRoot = "wwwroot";

    /// <summary>Host page served when a request does not name a file.</summary>
    public const string DefaultHostFile = "index.html";

    /// <summary>Directory (under the content root) holding the Blazor framework assets.</summary>
    public const string FrameworkDirectory = "_framework";

    /// <summary>
    /// True when the request targets a Blazor framework asset (<c>_framework/...</c>). The
    /// framework files are ordinary files under the content root, so
    /// <see cref="ResolveAssetPath"/> resolves them like any other asset; the flag exists so the
    /// shell registration can keep them on the same interception path as the app assets while
    /// the hybrid bootstrap script remains the one special-cased <c>_framework</c> file the shell
    /// already serves from the payload root.
    /// </summary>
    public static bool IsFrameworkRequest(string? requestPath)
    {
        string path = NormalizeRequestPath(requestPath);
        return path.StartsWith(FrameworkDirectory + "/", StringComparison.Ordinal);
    }

    /// <summary>
    /// Returns the absolute app package directory Blazor content is served from, i.e.
    /// <paramref name="appDirectory"/> plus <paramref name="contentRoot"/> (default
    /// <see cref="ContentRoot"/>). Null when the app directory is missing or the content root is
    /// not a safe relative path.
    /// </summary>
    public static string? ResolveContentRoot(string? appDirectory, string? contentRoot = null)
    {
        string baseDirectory = appDirectory?.Trim().TrimEnd('/') ?? string.Empty;
        if (baseDirectory.Length == 0)
        {
            return null;
        }
        string root = string.IsNullOrWhiteSpace(contentRoot)
            ? ContentRoot
            : contentRoot.Trim().Trim('/');
        if (!IsSafeRelativePath(root))
        {
            return null;
        }
        return TryGetFullPath(baseDirectory, root);
    }

    /// <summary>
    /// Maps one app-origin request path (for example "", "index.html", "_framework/blazor.webview.js",
    /// "/css/app.css?v=1" or the full "https://0.0.0.0/_framework/blazor.webview.js") to the
    /// absolute path of the app package file under the content root. An empty path resolves to
    /// <see cref="DefaultHostFile"/>. Returns null when the app directory or content root is not
    /// usable or the request would escape the content root (rooted paths, '\' separators and
    /// "." / ".." segments are rejected), so callers never hand a shell file read an unsafe path.
    /// </summary>
    public static string? ResolveAssetPath(
        string? appDirectory,
        string? requestPath,
        string? contentRoot = null)
    {
        string? root = ResolveContentRoot(appDirectory, contentRoot);
        if (root is null)
        {
            return null;
        }
        string relativePath = NormalizeRequestPath(requestPath);
        if (relativePath.Length == 0)
        {
            relativePath = DefaultHostFile;
        }
        if (!IsSafeRelativePath(relativePath))
        {
            return null;
        }
        string? resolved = TryGetFullPath(root, relativePath);
        if (resolved is null)
        {
            return null;
        }
        string prefix = root.EndsWith("/", StringComparison.Ordinal) ? root : root + "/";
        return resolved.StartsWith(prefix, StringComparison.Ordinal) ? resolved : null;
    }

    /// <summary>
    /// Turns a request path or app-origin URL into a relative path: strips the origin, the query
    /// and the fragment, decodes percent escapes (so an encoded ".." is still caught by
    /// <see cref="IsSafeRelativePath"/>) and drops the leading '/'. A malformed escape becomes an
    /// empty path, which resolves to the default host file instead of a file path. The origin is
    /// stripped textually rather than through <see cref="Uri"/>, whose normalization would fold
    /// ".." segments away before they can be rejected.
    /// </summary>
    private static string NormalizeRequestPath(string? requestPath)
    {
        if (string.IsNullOrWhiteSpace(requestPath))
        {
            return string.Empty;
        }
        string path = requestPath.Trim();
        int scheme = path.IndexOf("://", StringComparison.Ordinal);
        if (scheme >= 0)
        {
            int pathStart = path.IndexOf('/', scheme + 3, StringComparison.Ordinal);
            path = pathStart >= 0 ? path[pathStart..] : string.Empty;
        }
        int query = path.IndexOf('?', StringComparison.Ordinal);
        if (query >= 0)
        {
            path = path[..query];
        }
        int fragment = path.IndexOf('#', StringComparison.Ordinal);
        if (fragment >= 0)
        {
            path = path[..fragment];
        }
        try
        {
            path = Uri.UnescapeDataString(path);
        }
        catch (Exception ex) when (ex is UriFormatException or ArgumentException)
        {
            return string.Empty;
        }
        return path.TrimStart('/');
    }

    /// <summary>
    /// True when the path is a non-empty relative path made of ordinary segments: no '\'
    /// separators, no rooted path and no empty, "." or ".." segment. This is what keeps a
    /// resolved asset inside the content root.
    /// </summary>
    private static bool IsSafeRelativePath(string path)
    {
        if (path.Length == 0 || path.IndexOf('\0', StringComparison.Ordinal) >= 0 ||
            path.IndexOf('\\', StringComparison.Ordinal) >= 0 || Path.IsPathRooted(path))
        {
            return false;
        }
        foreach (string segment in path.Split('/'))
        {
            if (segment.Length == 0 || segment == "." || segment == "..")
            {
                return false;
            }
        }
        return true;
    }

    private static string? TryGetFullPath(string directory, string relativePath)
    {
        try
        {
            return Path.GetFullPath(Path.Combine(directory, relativePath));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }
}
