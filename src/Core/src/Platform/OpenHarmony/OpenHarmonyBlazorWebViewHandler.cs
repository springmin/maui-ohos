// BlazorWebView milestone 2 skeleton for OpenHarmony.
//
// What this file contains (compile-level, all of it references the restored
// Microsoft.AspNetCore.Components.WebView.Maui types):
//   * OpenHarmonyBlazorWebViewHandler — an IBlazorWebViewHandler/ViewHandler shell with the
//     same platform-view shape as the WebView/HybridWebView handlers;
//   * the asset-path half: HostPage/content root registration with the ArkTS shell over
//     milestone 1's OpenHarmonyBlazorWebView mapping, plus an IFileProvider implementation
//     that resolves every request through that mapping;
//   * the JS channel half: outbound messages ride the existing ohos_host_web_eval channel,
//     inbound messages come from OpenHarmonyWebViewHandler.JsMessage (the shell's dotnetHost
//     proxy) and are handed to the platform WebViewManager subclass.
//
// What is intentionally NOT here (the remainder of milestone 2b):
//   * creating the platform WebViewManager (needs services/dispatcher; the constructor is
//     already sketched on OpenHarmonyWebViewManager) and navigating from the handler.
// The other two milestone-2b gaps are closed by milestone 3:
//   * the shell side of the "blazor" web command and the Blazor origin interception are
//     implemented in the platform pack's ArkTS shell (Index.ets): https://0.0.0.0/ is served
//     from <AppDir>/<content root> and the page-end bootstrap installs the callback form of
//     window.external.receiveMessage, publishes window.__dispatchMessageCallback and calls
//     Blazor.start();
//   * the static web assets are staged by the pack targets (OpenHarmony.Hap.targets): the app
//     wwwroot plus wwwroot/_framework/blazor.webview.js travel inside resources/rawfile/dotnet.zip.
//     Native model: the managed app runs in-process on CoreCLR, so no dotnet.js/dotnet.wasm/_*.dll
//     browser assets exist (that is the WebAssembly model of Blazor Web, not Blazor Hybrid).
//   * window.external bootstrap framing: blazor.webview.js posts JS -> .NET through
//     window.external.sendMessage(message) and registers its .NET -> JS callback through
//     window.external.receiveMessage(callback) (verified against the package's
//     staticwebassets/blazor.webview.js), so the shell's bootstrap installs the Tizen-style
//     window.__dispatchMessageCallback fan-out instead of the HybridWebView receiveMessage(message)
//     delivery shim; SendMessage below prefers that callback and keeps the shim only as the
//     pre-bootstrap fallback;
//   * the handler registration: the package registers BlazorWebViewHandler through
//     AddMauiBlazorWebView(); the port replaces it with
//     services.AddMauiBlazorWebView().UsePlatformHandler<OpenHarmonyBlazorWebViewHandler>()
//     (MauiBlazorWebViewBuilderExtensions.UsePlatformHandler&lt;T&gt;).
//
// Compilation gate: the file takes the BlazorWebView package types, so it is compiled only when
// OPENHARMONY_BLAZOR_WEBVIEW is defined. The slice project defines it together with the package
// reference. Foreign compile vehicles that compile the slice sources without restoring that
// package (the ohos-workload verifier harness in this partial checkout, where the standalone
// slice csproj cannot be built because eng/AndroidX.targets is missing) leave the constant
// undefined and the file compiles to an empty translation unit instead of breaking their build;
// to gate milestone 2 there too, add the package reference and the constant to that vehicle.
#if OPENHARMONY_BLAZOR_WEBVIEW
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebView;
using Microsoft.AspNetCore.Components.WebView.Maui;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Primitives;
using Microsoft.Maui.Graphics;
using Microsoft.OpenHarmony.Hosting;

namespace Microsoft.Maui.Platform;

/// <summary>
/// Skeleton <see cref="IBlazorWebViewHandler"/> for OpenHarmony. It wires the app package
/// content root (<see cref="OpenHarmonyBlazorWebView"/>) to the ArkTS shell and taps the JS
/// channel the WebView/HybridWebView handlers already use, but it does not create a
/// <see cref="WebViewManager"/> or navigate a page yet — see the TODOs on
/// <see cref="ConnectHandler"/> and <see cref="RegisterBlazorAssets"/>.
/// </summary>
public sealed class OpenHarmonyBlazorWebViewHandler : OpenHarmonyViewHandler<IBlazorWebView>, IBlazorWebViewHandler
{
    /// <summary>Origin Blazor app content is loaded from (<c>BlazorWebViewHandler.AppOrigin</c>).</summary>
    public const string AppOrigin = OpenHarmonyBlazorWebView.AppOrigin;

    private OpenHarmonyWebViewManager? _webViewManager;

    public static readonly IPropertyMapper<IBlazorWebView, OpenHarmonyBlazorWebViewHandler> Mapper =
        new PropertyMapper<IBlazorWebView, OpenHarmonyBlazorWebViewHandler>(ViewMapper)
        {
            [nameof(IBlazorWebView.HostPage)] = MapHostPage,
            [nameof(IBlazorWebView.RootComponents)] = MapRootComponents,
        };

    public OpenHarmonyBlazorWebViewHandler() : base(Mapper) { }

    protected override OpenHarmonyView CreatePlatformView()
        => new() { IsWebView = true, Background = Colors.White };

    protected override void ConnectHandler(OpenHarmonyView platformView)
    {
        base.ConnectHandler(platformView);
        // Inbound half of the shared JS channel: the shell's dotnetHost proxy raises JsMessage
        // from arbitrary threads; binding the sink is idempotent (same call the WebView and
        // HybridWebView handlers make).
        OpenHarmonyWebViewHandler.JsMessage += OnJsMessage;
        OpenHarmonyWebViewHandler.EnsureMessageRegistered();
        RegisterBlazorAssets();

        // TODO milestone 2b: create the platform WebViewManager and navigate, mirroring the
        // package's StartWebViewCoreIfPossible:
        //   string hostPage = <VirtualView.HostPage>;                 // "wwwroot/index.html"
        //   string contentRootDir = Path.GetDirectoryName(hostPage)!;
        //   _webViewManager = new OpenHarmonyWebViewManager(
        //       this,
        //       <IServiceProvider from the handler's MauiContext/services scope>,
        //       new MauiDispatcher(<IDispatcher from that scope>),
        //       CreateFileProvider(contentRootDir),
        //       VirtualView.JSComponents,
        //       Path.GetRelativePath(contentRootDir, hostPage));
        //   PublishRootComponents();                                  // AddToWebViewManagerAsync
        //   _webViewManager.Navigate(VirtualView.StartPath);
        //   then call VirtualView.BlazorWebViewInitializing/Initialized like the package
        //   handler does. The Blazor bootstrap itself is injected by the ArkTS shell on page
        //   end (milestone 3), so no init script is evaluated from here.
    }

    protected override void DisconnectHandler(OpenHarmonyView platformView)
    {
        OpenHarmonyWebViewHandler.JsMessage -= OnJsMessage;
        // TODO milestone 2b: await _webViewManager.DisposeAsync() and stop the page.
        _webViewManager = null;
        base.DisconnectHandler(platformView);
    }

    /// <summary>HostPage mapper: (re)register the app package content root with the shell.</summary>
    public static void MapHostPage(OpenHarmonyBlazorWebViewHandler handler, IBlazorWebView webView)
        => handler.RegisterBlazorAssets();

    /// <summary>RootComponents mapper: publish the component list once a manager exists.</summary>
    public static void MapRootComponents(OpenHarmonyBlazorWebViewHandler handler, IBlazorWebView webView)
        => handler.PublishRootComponents();

    /// <summary>
    /// Registers the Blazor app origin + content root with the ArkTS shell (the "blazor" web
    /// command): the shell answers <c>https://0.0.0.0/</c> from the payload's
    /// <c>&lt;content root&gt;</c> directory (default <c>wwwroot</c>) with the same
    /// <c>&lt;base&gt;/&lt;root&gt;/&lt;path&gt;</c> convention the hybrid bridge uses, injects the
    /// blazor.webview.js bootstrap on page end and starts the load. The pack targets stage the
    /// content root plus <c>wwwroot/_framework/blazor.webview.js</c> into the hap payload
    /// (native in-process model: no dotnet.js/dotnet.wasm/_*.dll browser assets).
    /// </summary>
    public void RegisterBlazorAssets()
    {
        OpenHarmonyAppContext? context = OpenHarmonyBridge.Context;
        if (context is null || string.IsNullOrEmpty(context.AppDir))
        {
            return;
        }
        string hostPage = string.IsNullOrWhiteSpace(VirtualView?.HostPage)
            ? OpenHarmonyBlazorWebView.ContentRoot + "/" + OpenHarmonyBlazorWebView.DefaultHostFile
            : VirtualView!.HostPage!;
        string contentRootDir = Path.GetDirectoryName(hostPage)?.Replace('\\', '/') ?? string.Empty;
        if (OpenHarmonyBlazorWebView.ResolveContentRoot(context.AppDir, contentRootDir) is null)
        {
            return;
        }
        OpenHarmonyBridge.WebCommand("blazor", JsonSerializer.Serialize(new BlazorAssetsConfig
        {
            Origin = AppOrigin,
            Base = context.AppDir.TrimEnd('/'),
            ContentRoot = contentRootDir,
            HostFile = Path.GetFileName(hostPage),
        }));
    }

    /// <summary>
    /// TODO milestone 2b: with a live manager this mirrors the package handler's
    /// MapRootComponents (each <c>RootComponent.AddToWebViewManagerAsync</c>). Until the manager
    /// exists it only reports the count so the property mapping has a visible effect.
    /// </summary>
    public void PublishRootComponents()
    {
        int count = VirtualView?.RootComponents.Count ?? 0;
        if (count > 0)
        {
            OpenHarmonyBridge.WriteStatus($"[maui] blazor root components pending manager: {count}");
        }
    }

    /// <summary>
    /// File provider over the app package content root. Every request path goes through
    /// milestone 1's <see cref="OpenHarmonyBlazorWebView.ResolveAssetPath(string?, string?, string?)"/>,
    /// so rooted paths, '\' separators and "." / ".." segments are rejected before any file
    /// system access. On device the shell is the one serving the files (ArkWeb interception);
    /// this provider is the managed half used by a WebViewManager and by tests.
    /// </summary>
    public IFileProvider CreateFileProvider(string contentRootDir)
    {
        ArgumentNullException.ThrowIfNull(contentRootDir);
        return new OpenHarmonyBlazorFileProvider(OpenHarmonyBridge.Context?.AppDir, contentRootDir);
    }

    /// <summary>Dispatches work into the Blazor components' service scope; false before 2b.</summary>
    public async Task<bool> TryDispatchAsync(Action<IServiceProvider> workItem)
    {
        ArgumentNullException.ThrowIfNull(workItem);
        if (_webViewManager is null)
        {
            return false;
        }
        return await _webViewManager.TryDispatchAsync(workItem).ConfigureAwait(false);
    }

    /// <summary>Evaluates a script on the shell's ArkWeb page (the existing web eval channel).</summary>
    public Task<string?> EvaluateJavaScriptAsync(string script)
        => OpenHarmonyWebViewHandler.EvaluateJavaScriptAsyncCore(script);

    /// <summary>Inbound JS -> .NET: hands a shell dotnetHost payload to the platform manager.</summary>
    private void OnJsMessage(string payload)
    {
        if (_webViewManager is null || string.IsNullOrEmpty(payload))
        {
            return;
        }
        _webViewManager.MessageReceivedFromShell(new Uri(AppOrigin), payload);
    }

    /// <summary>Payload descriptor the shell consumes from the "blazor" web command.</summary>
    private sealed class BlazorAssetsConfig
    {
        [JsonPropertyName("origin")]
        public string Origin { get; init; } = string.Empty;

        [JsonPropertyName("base")]
        public string Base { get; init; } = string.Empty;

        [JsonPropertyName("root")]
        public string ContentRoot { get; init; } = string.Empty;

        [JsonPropertyName("defaultFile")]
        public string HostFile { get; init; } = string.Empty;
    }
}

/// <summary>
/// Skeleton platform <see cref="WebViewManager"/>: maps the base class's platform hooks to the
/// ArkTS shell commands. Not instantiated yet — milestone 2b creates it in the handler's
/// ConnectHandler (see the TODO there).
/// </summary>
internal sealed class OpenHarmonyWebViewManager : WebViewManager
{
    private const string ShellLoadCommand = "load";

    public OpenHarmonyWebViewManager(
        OpenHarmonyBlazorWebViewHandler handler,
        IServiceProvider provider,
        Microsoft.AspNetCore.Components.Dispatcher dispatcher,
        IFileProvider fileProvider,
        JSComponentConfigurationStore jsComponents,
        string hostPageRelativePath)
        : base(provider, dispatcher, new Uri(OpenHarmonyBlazorWebViewHandler.AppOrigin), fileProvider, jsComponents, hostPageRelativePath)
    {
        ArgumentNullException.ThrowIfNull(handler);
    }

    /// <summary>
    /// Shell navigation: the ArkWeb overlay loads the app origin path (the same shell command
    /// the WebView handler uses). The shell injects the window.external -> dotnetHost bridge and
    /// the Blazor bootstrap (Blazor.start()) on page end, so this only has to trigger the load.
    /// </summary>
    protected override void NavigateCore(Uri absoluteUri)
        => OpenHarmonyBridge.WebCommand(ShellLoadCommand, absoluteUri.ToString());

    /// <summary>
    /// .NET -> JS over the existing shell eval channel. <c>blazor.webview.js</c> registers its
    /// incoming-message callback through <c>window.external.receiveMessage(callback)</c>, so the
    /// Blazor bootstrap script (TODO milestone 2b) installs a Tizen-style
    /// <c>window.__dispatchMessageCallback</c> fan-out and the delivery goes there first. The
    /// shell's own <c>window.external.receiveMessage(message)</c> shim stays as the fallback
    /// before the bootstrap runs (it is the HybridWebView-compatible delivery path).
    /// </summary>
    protected override void SendMessage(string message)
    {
        if (string.IsNullOrEmpty(message))
        {
            return;
        }
        _ = OpenHarmonyWebViewHandler.EvaluateJavaScriptAsyncCore(
            "(function(m){if(typeof window.__dispatchMessageCallback==='function')" +
            "{window.__dispatchMessageCallback(m);}" +
            "else if(window.external&&typeof window.external.receiveMessage==='function')" +
            "{window.external.receiveMessage(m);}})" +
            "(" + JsonSerializer.Serialize(message) + ")");
    }

    /// <summary>
    /// Forwards a shell dotnetHost payload into the Blazor dispatcher. The base class exposes
    /// its message entry point only to derived classes, so this protected-member wrapper is the
    /// way in from the slice assembly (verified by compiling this file with the package).
    /// </summary>
    internal void MessageReceivedFromShell(Uri uri, string message)
        => MessageReceived(uri, message);
}

/// <summary>
/// Read-only <see cref="IFileProvider"/> over the extracted app payload, backed by milestone 1's
/// request-path mapping. The published payload never changes at run time, so
/// <see cref="Watch"/> returns <see cref="NullChangeToken.Singleton"/>.
/// </summary>
internal sealed class OpenHarmonyBlazorFileProvider : IFileProvider
{
    private readonly string? _appDirectory;
    private readonly string _contentRoot;

    public OpenHarmonyBlazorFileProvider(string? appDirectory, string contentRoot)
    {
        _appDirectory = appDirectory;
        _contentRoot = contentRoot;
    }

    public IFileInfo GetFileInfo(string subpath)
    {
        string? path = OpenHarmonyBlazorWebView.ResolveAssetPath(_appDirectory, subpath, _contentRoot);
        return path is not null && File.Exists(path)
            ? new OpenHarmonyBlazorFileInfo(path, subpath)
            : new NotFoundFileInfo(subpath);
    }

    public IDirectoryContents GetDirectoryContents(string subpath)
    {
        string? path = OpenHarmonyBlazorWebView.ResolveAssetPath(_appDirectory, subpath, _contentRoot);
        return path is not null && Directory.Exists(path)
            ? new OpenHarmonyBlazorDirectoryContents(path)
            : NotFoundDirectoryContents.Singleton;
    }

    public IChangeToken Watch(string filter) => NullChangeToken.Singleton;
}

/// <summary>One existing file under the content root.</summary>
internal sealed class OpenHarmonyBlazorFileInfo : IFileInfo
{
    private readonly FileInfo _file;

    public OpenHarmonyBlazorFileInfo(string physicalPath, string requestPath)
    {
        _file = new FileInfo(physicalPath);
        Name = Path.GetFileName(physicalPath);
        PhysicalPath = physicalPath;
        RequestPath = requestPath;
    }

    /// <summary>The request path this file was resolved from (diagnostics only).</summary>
    public string RequestPath { get; }

    public bool Exists => _file.Exists;

    public long Length => _file.Length;

    public string? PhysicalPath { get; }

    public string Name { get; }

    public DateTimeOffset LastModified => _file.LastWriteTimeUtc;

    public bool IsDirectory => false;

    public Stream CreateReadStream() => new FileStream(_file.FullName, FileMode.Open, FileAccess.Read, FileShare.Read);
}

/// <summary>Directory entries under the content root, resolved lazily.</summary>
internal sealed class OpenHarmonyBlazorDirectoryContents : IDirectoryContents
{
    private readonly string _directory;

    public OpenHarmonyBlazorDirectoryContents(string directory) => _directory = directory;

    public bool Exists => true;

    public IEnumerator<IFileInfo> GetEnumerator()
        => Directory.EnumerateFileSystemEntries(_directory)
            .Select(path => (IFileInfo)new OpenHarmonyBlazorFileInfo(path, Path.GetFileName(path)))
            .GetEnumerator();

    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
}
#endif
