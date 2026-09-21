// BlazorWebView platform handler for OpenHarmony (milestone 2b/3: the managed manager path).
//
// What this file does, end to end:
//   * OpenHarmonyBlazorWebViewHandler — an IBlazorWebViewHandler/ViewHandler shell that binds the
//     shell's JS channel and registers the app package content root with the ArkTS shell, then
//     creates the platform WebViewManager once HostPage and the handler's services exist
//     (StartWebViewCoreIfPossible, mirroring the package's platform partials). It raises the
//     BlazorWebViewInitializing/Initialized events, publishes the control's root components to the
//     manager (AddToWebViewManagerAsync on add, RemoveFromWebViewManagerAsync on remove) and
//     navigates the manager to VirtualView.StartPath, skipping that load when the shell "blazor"
//     registration already started the host-page load (no equivalent reload);
//   * the asset-path half: HostPage/content root registration with the ArkTS shell over
//     milestone 1's OpenHarmonyBlazorWebView mapping, plus the IFileProvider implementation
//     (OpenHarmonyBlazorFileProvider) that resolves every request through that mapping;
//   * the JS channel half: outbound messages ride the existing ohos_host_web_eval channel and are
//     delivered to window.__dispatchMessageCallback (the callback blazor.webview.js registers
//     through window.external.receiveMessage) only when the loaded document carries the
//     shell-stamped window.__ohBlazorId marker; inbound messages come from
//     OpenHarmonyWebViewHandler.JsMessage (the shell's dotnetHost proxy), which prefixes them with
//     a "__OHORIGIN|<document url>|<document id>\n" envelope, and are validated against the app
//     origin and this handler's registration id before they are handed to the platform
//     WebViewManager's protected MessageReceived as MessageReceived(AppOrigin, payload).
//
// The shell side is already in place (ohos-workload pack, milestone 3): the "blazor" web command
// serves https://0.0.0.0/ from <AppDir>/<content root> and the page-end bootstrap installs the
// Tizen-style window.external shim, publishes window.__dispatchMessageCallback and calls
// Blazor.start(). The pack targets stage the app wwwroot plus wwwroot/_framework/blazor.webview.js
// into resources/rawfile/dotnet.zip. Native model: the managed app runs in-process on CoreCLR, so
// no dot.js/dot.wasm/_*.dll browser assets exist (that is the WebAssembly model of Blazor Web,
// not Blazor Hybrid); no init script is evaluated from this handler.
//
// Handler registration (milestone 3, checklist item 2): the package registers its own
// platform-less net11.0 handler through AddMauiBlazorWebView(); replace it with this one:
//     builder.Services.AddMauiBlazorWebView().UsePlatformHandler<OpenHarmonyBlazorWebViewHandler>();
// (Microsoft.Extensions.DependencyInjection.BlazorWebViewServiceCollectionExtensions.AddMauiBlazorWebView
// + MauiBlazorWebViewBuilderExtensions.UsePlatformHandler<T>). MauiOpenHarmonyExtensions.SliceHandlers
// also carries an IBlazorWebView entry (gated by OPENHARMONY_BLAZOR_WEBVIEW) so the slice's app host,
// which connects handlers by exact type/interface, resolves this handler for a BlazorWebView control.
//
// Compilation gate: the file takes the BlazorWebView package types, so it is compiled only when
// OPENHARMONY_BLAZOR_WEBVIEW is defined. The slice project defines it together with the package
// reference. Foreign compile vehicles that compile the slice sources without restoring that
// package (the ohos-workload verifier harness in this partial checkout, where the standalone
// slice csproj cannot be built because eng/AndroidX.targets is missing) leave the constant
// undefined and the file compiles to an empty translation unit instead of breaking their build;
// to gate milestone 2 there too, add the package reference and the constant to that vehicle.
#if OPENHARMONY_BLAZOR_WEBVIEW
using System.Collections.Specialized;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebView;
using Microsoft.AspNetCore.Components.WebView.Maui;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Primitives;
using Microsoft.Maui.Dispatching;
using Microsoft.Maui.Graphics;
using Microsoft.OpenHarmony.Hosting;

namespace Microsoft.Maui.Platform;

/// <summary>
/// <see cref="IBlazorWebViewHandler"/> for OpenHarmony. It wires the app package content root
/// (<see cref="OpenHarmonyBlazorWebView"/>) to the ArkTS shell, taps the JS channel the
/// WebView/HybridWebView handlers already use, and owns the platform <see cref="WebViewManager"/>
/// that drives Blazor's IPC (see <see cref="StartWebViewCoreIfPossible"/>).
/// </summary>
/// <remarks>
/// Register it after the package's own registration:
/// <code>builder.Services.AddMauiBlazorWebView().UsePlatformHandler&lt;OpenHarmonyBlazorWebViewHandler&gt;();</code>
/// The slice's app host additionally resolves it through
/// <c>MauiOpenHarmonyExtensions.SliceHandlers</c> (the <c>IBlazorWebView</c> entry), so an app
/// that only calls <c>AddMauiBlazorWebView()</c> still gets this handler on this platform.
/// </remarks>
public sealed class OpenHarmonyBlazorWebViewHandler : OpenHarmonyViewHandler<IBlazorWebView>, IBlazorWebViewHandler
{
    /// <summary>Origin Blazor app content is loaded from (<c>BlazorWebViewHandler.AppOrigin</c>).</summary>
    public const string AppOrigin = OpenHarmonyBlazorWebView.AppOrigin;

    private OpenHarmonyWebViewManager? _webViewManager;
    private RootComponentsCollection? _rootComponents;
    private string? _registeredAssets;
    // B1/B3 identity of this handler's page: generated once per handler, sent to the shell with
    // the asset registration (BlazorAssetsConfig.id), stamped into served documents as
    // window.__ohBlazorId and echoed in every message envelope.
    private readonly string _pageId = Guid.NewGuid().ToString("N");
    private static readonly Uri s_appOrigin = new(AppOrigin, UriKind.Absolute);
    // Set when the shell "blazor" registration (RegisterBlazorAssets) actually starts the
    // host-page load; handed to the manager created afterwards so its initial Navigate(StartPath)
    // does not send a second, equivalent load.
    private bool _shellStartedHostPageLoad;

    public static readonly IPropertyMapper<IBlazorWebView, OpenHarmonyBlazorWebViewHandler> Mapper =
        new PropertyMapper<IBlazorWebView, OpenHarmonyBlazorWebViewHandler>(ViewMapper)
        {
            [nameof(IBlazorWebView.HostPage)] = MapHostPage,
            [nameof(IBlazorWebView.RootComponents)] = MapRootComponents,
        };

    public OpenHarmonyBlazorWebViewHandler() : base(Mapper) { }

    /// <summary>The live platform manager, or null before the startup properties are set.</summary>
    internal OpenHarmonyWebViewManager? Manager => _webViewManager;

    /// <summary>HostPage of the connected control (the path the shell serves content from).</summary>
    private string? HostPage => VirtualView?.HostPage;

    /// <summary>HostPage plus a service provider is what a <see cref="WebViewManager"/> needs.</summary>
    private bool RequiredStartupPropertiesSet =>
        !string.IsNullOrWhiteSpace(HostPage) && Services is not null;

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
        // The property mapper runs right after ConnectHandler and sets HostPage/RootComponents;
        // this call covers the case where the control was already fully configured.
        StartWebViewCoreIfPossible();
    }

    protected override void DisconnectHandler(OpenHarmonyView platformView)
    {
        OpenHarmonyWebViewHandler.JsMessage -= OnJsMessage;
        if (_rootComponents is not null)
        {
            _rootComponents.CollectionChanged -= OnRootComponentsCollectionChanged;
            _rootComponents = null;
        }
        // A reconnected handler re-registers with the shell (it may have been reloaded since).
        _registeredAssets = null;
        _shellStartedHostPageLoad = false;
        // Blazor's DisposeAsync tears down the component renderer, its service scope and the
        // static content hot-reload notifier. Mirror the built-in handlers' default: do not block
        // the teardown path, but observe the task so a failure is logged instead of lost.
        OpenHarmonyWebViewManager? manager = _webViewManager;
        _webViewManager = null;
        if (manager is not null)
        {
            _ = DisposeWebViewManagerAsync(manager);
        }
        base.DisconnectHandler(platformView);
    }

    /// <summary>
    /// Creates the platform <see cref="WebViewManager"/> and starts the Blazor page. Mirrors the
    /// package's platform partials (Tizen/Android/iOS):
    /// <list type="number">
    /// <item><description>content root = the directory part of HostPage, host page = its file name;</description></item>
    /// <item><description>the file provider from milestone 1 (<see cref="CreateFileProvider"/>);</description></item>
    /// <item><description><see cref="MauiDispatcher"/> over the app's <see cref="IDispatcher"/>;</description></item>
    /// <item><description><c>BlazorWebViewInitializing</c>/<c>BlazorWebViewInitialized</c> raised on the control;</description></item>
    /// <item><description>the root components published through <see cref="RootComponent.AddToWebViewManagerAsync"/>;</description></item>
    /// <item><description><c>Navigate(VirtualView.StartPath)</c>, suppressed on the first manager when
    /// the <c>blazor</c> command already started the host-page load; later explicit navigations
    /// still reach the shell. The manager lives until <see cref="DisconnectHandler"/>.</description></item>
    /// </list>
    /// Idempotent: a second call (HostPage/RootComponents mapper, reconnect) is a no-op while the
    /// manager exists.
    /// </summary>
    private void StartWebViewCoreIfPossible()
    {
        if (!RequiredStartupPropertiesSet || _webViewManager is not null)
        {
            return;
        }
        if (PlatformView is null)
        {
            throw new InvalidOperationException($"Can't start {nameof(IBlazorWebView)} without a platform web view instance.");
        }

        // We assume the host page is always in the root of the content directory, because it's
        // unclear there's any other use case; this matches the package handlers.
        IBlazorWebView webView = VirtualView!;
        string contentRootDir = Path.GetDirectoryName(webView.HostPage!) ?? string.Empty;
        string hostPageRelativePath = Path.GetRelativePath(contentRootDir, webView.HostPage!);

        // The same provider the control's virtual CreateFileProvider resolves to (that method
        // routes through IBlazorWebViewHandler.CreateFileProvider back to this handler), without
        // depending on the virtual view's Handler slot being populated.
        IFileProvider fileProvider = CreateFileProvider(contentRootDir);

        _webViewManager = new OpenHarmonyWebViewManager(
            this,
            Services!,
            new MauiDispatcher(Services!.GetRequiredService<IDispatcher>()),
            fileProvider,
            webView.JSComponents,
            hostPageRelativePath,
            shellStartedHostPageLoad: _shellStartedHostPageLoad,
            pageDocumentId: _pageId);
        // The "blazor" command's own load is consumed by this manager's first navigation; a
        // registration issued while a manager is live only reloads the shell page itself.
        _shellStartedHostPageLoad = false;

        // Development-time static content hot reload; inert when the runtime does not support it.
        _ = BlazorWebViewStaticContentHotReload.TryAttachToWebViewManager(_webViewManager);

        webView.BlazorWebViewInitializing(new BlazorWebViewInitializingEventArgs());
        webView.BlazorWebViewInitialized(new BlazorWebViewInitializedEventArgs(PlatformView));

        PublishRootComponentsToManager();

        _webViewManager.Navigate(webView.StartPath);
    }

    private static async Task DisposeWebViewManagerAsync(OpenHarmonyWebViewManager manager)
    {
        try
        {
            await manager.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            OpenHarmonyBridge.WriteStatus($"[maui] blazor manager disposal failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>HostPage mapper: (re)register the app package content root and start the manager.</summary>
    public static void MapHostPage(OpenHarmonyBlazorWebViewHandler handler, IBlazorWebView webView)
    {
        handler.RegisterBlazorAssets();
        handler.StartWebViewCoreIfPossible();
    }

    /// <summary>RootComponents mapper: bind the control's component collection (see <see cref="PublishRootComponents"/>).</summary>
    public static void MapRootComponents(OpenHarmonyBlazorWebViewHandler handler, IBlazorWebView webView)
        => handler.PublishRootComponents();

    /// <summary>
    /// Binds <c>IBlazorWebView.RootComponents</c> to this handler and starts the manager. Items
    /// already in the collection are published through <see cref="RootComponent.AddToWebViewManagerAsync"/>;
    /// items added or removed later are published on the manager's dispatcher through
    /// <see cref="OnRootComponentsCollectionChanged"/>.
    /// </summary>
    public void PublishRootComponents()
    {
        RootComponents = VirtualView?.RootComponents;
        StartWebViewCoreIfPossible();
    }

    /// <summary>
    /// Registers the Blazor app origin + content root with the ArkTS shell (the "blazor" web
    /// command): the shell answers <c>https://0.0.0.0/</c> from the payload's
    /// <c>&lt;content root&gt;</c> directory (default <c>wwwroot</c>) with the same
    /// <c>&lt;base&gt;/&lt;root&gt;/&lt;path&gt;</c> convention the hybrid bridge uses, injects the
    /// blazor.webview.js bootstrap on page end and starts the load. The pack targets stage the
    /// content root plus <c>wwwroot/_framework/blazor.webview.js</c> into the hap payload
    /// (native in-process model: no dot.js/dot.wasm/_*.dll browser assets).
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
        // ConnectHandler and the HostPage mapper both land here; only re-issue the shell command
        // when the payload layout actually changed (the command also loads the origin).
        string key = context.AppDir.TrimEnd('/') + "|" + contentRootDir + "|" + Path.GetFileName(hostPage);
        if (key == _registeredAssets)
        {
            return;
        }
        _registeredAssets = key;
        OpenHarmonyBridge.WebCommand("blazor", JsonSerializer.Serialize(new BlazorAssetsConfig
        {
            Origin = AppOrigin,
            Base = context.AppDir.TrimEnd('/'),
            ContentRoot = contentRootDir,
            HostFile = Path.GetFileName(hostPage),
            Id = _pageId,
        }));
        // The shell command arms origin interception and loads the host page (origin root), so
        // the manager created after this registration must not send the same load again.
        _shellStartedHostPageLoad = true;
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

    /// <summary>Dispatches work into the Blazor components' service scope; false before startup.</summary>
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

    /// <summary>
    /// Inbound JS -> .NET: the payload must carry the shell's document-origin envelope, report
    /// this handler's app origin and carry the registration id the shell stamped for this
    /// handler (B1). Only then is it handed to the platform manager, which parses the
    /// blazor.webview.js framing and dispatches it to the components (<c>__bwv:</c> prefix
    /// handling in <see cref="WebViewManager.MessageReceived"/>). A missing/mismatching origin
    /// or id is rejected and logged instead of being dispatched under a fabricated AppOrigin;
    /// payloads that arrive before startup are dropped.
    /// </summary>
    private void OnJsMessage(string payload)
    {
        if (_webViewManager is null || string.IsNullOrEmpty(payload))
        {
            return;
        }
        if (!OpenHarmonyHybridWebViewHandler.TryParseOriginEnvelope(
                payload, out Uri? origin, out string documentId, out string message))
        {
            OpenHarmonyBridge.WriteStatus(
                "[maui] blazor message rejected: missing or malformed document-origin envelope");
            return;
        }
        if (origin is null || !IsAppOrigin(origin) || documentId.Length == 0 || documentId != _pageId)
        {
            OpenHarmonyBridge.WriteStatus(
                "[maui] blazor message rejected: the reported origin/document is not this handler's page");
            return;
        }
        _webViewManager.MessageReceivedFromShell(new Uri(AppOrigin), message);
    }

    /// <summary>True when the reported document url is on the Blazor app origin (B1).</summary>
    private static bool IsAppOrigin(Uri origin)
        => string.Equals(origin.Scheme, s_appOrigin.Scheme, StringComparison.OrdinalIgnoreCase)
            && string.Equals(origin.IdnHost, s_appOrigin.IdnHost, StringComparison.OrdinalIgnoreCase)
            && origin.Port == s_appOrigin.Port;

    /// <summary>The control's root-component collection, bound to the live manager.</summary>
    private RootComponentsCollection? RootComponents
    {
        get => _rootComponents;
        set
        {
            if (ReferenceEquals(_rootComponents, value))
            {
                return;
            }
            if (_rootComponents is not null)
            {
                _rootComponents.CollectionChanged -= OnRootComponentsCollectionChanged;
            }
            _rootComponents = value;
            if (_rootComponents is not null)
            {
                _rootComponents.CollectionChanged += OnRootComponentsCollectionChanged;
                if (_rootComponents.Count > 0 && _webViewManager is not null)
                {
                    PublishRootComponentsToManager();
                }
            }
        }
    }

    private void PublishRootComponentsToManager()
    {
        if (_rootComponents is null || _webViewManager is null)
        {
            return;
        }
        // Since the page isn't loaded yet this completes synchronously; after a page attached it
        // enqueues the add through the manager's dispatcher.
        foreach (RootComponent rootComponent in _rootComponents)
        {
            _ = rootComponent.AddToWebViewManagerAsync(_webViewManager);
        }
    }

    /// <summary>
    /// Adds/removes root components on the manager after the page attached (the collection is an
    /// ObservableCollection; Clear/Reset carries no items and is ignored). The work is marshalled
    /// through <see cref="WebViewManager.Dispatcher"/>, like the package handler does.
    /// </summary>
    private void OnRootComponentsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (_webViewManager is not { } manager)
        {
            return;
        }
        RootComponent[] added = e.NewItems?.Cast<RootComponent>().ToArray() ?? Array.Empty<RootComponent>();
        RootComponent[] removed = e.OldItems?.Cast<RootComponent>().ToArray() ?? Array.Empty<RootComponent>();
        if (added.Length == 0 && removed.Length == 0)
        {
            return;
        }
        _ = manager.Dispatcher.InvokeAsync(async () =>
        {
            foreach (RootComponent component in added.Except(removed))
            {
                await component.AddToWebViewManagerAsync(manager).ConfigureAwait(false);
            }
            foreach (RootComponent component in removed.Except(added))
            {
                await component.RemoveFromWebViewManagerAsync(manager).ConfigureAwait(false);
            }
        });
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

        /// <summary>Per-registration document id (B1/B3), echoed in the shell message envelope.</summary>
        [JsonPropertyName("id")]
        public string Id { get; init; } = string.Empty;
    }
}

/// <summary>
/// Platform <see cref="WebViewManager"/>: maps the base class's platform hooks to the ArkTS shell
/// commands. Created by <see cref="OpenHarmonyBlazorWebViewHandler.StartWebViewCoreIfPossible"/>.
/// </summary>
internal sealed class OpenHarmonyWebViewManager : WebViewManager
{
    private const string ShellLoadCommand = "load";

    // True while the manager's initial navigation is still covered by the shell "blazor"
    // command's own host-page load; consumed by the first NavigateCore.
    private bool _shellStartedHostPageLoad;

    // The registration id of the handler this manager serves; SendMessage only delivers into a
    // document that still carries it (window.__ohBlazorId, B3).
    private readonly string _pageDocumentId;

    public OpenHarmonyWebViewManager(
        OpenHarmonyBlazorWebViewHandler handler,
        IServiceProvider provider,
        Microsoft.AspNetCore.Components.Dispatcher dispatcher,
        IFileProvider fileProvider,
        JSComponentConfigurationStore jsComponents,
        string hostPageRelativePath,
        bool shellStartedHostPageLoad,
        string pageDocumentId)
        : base(provider, dispatcher, new Uri(OpenHarmonyBlazorWebViewHandler.AppOrigin), fileProvider, jsComponents, hostPageRelativePath)
    {
        ArgumentNullException.ThrowIfNull(handler);
        _shellStartedHostPageLoad = shellStartedHostPageLoad;
        _pageDocumentId = pageDocumentId;
    }

    /// <summary>
    /// Shell navigation: the ArkWeb overlay loads the app origin path (the shell "load" op the
    /// WebView handler uses). When the "blazor" registration already armed origin interception
    /// and started the host-page load (<c>StartPath</c> defaults to "/", the exact URL the shell
    /// loads), the first navigation - the handler's <c>Navigate(StartPath)</c> - is that same
    /// document and is skipped instead of reloading it; every later explicit navigation still
    /// sends "load". The shell injects the window.external -> dotnetHost bridge and the Blazor
    /// bootstrap (Blazor.start()) on page end either way.
    /// </summary>
    protected override void NavigateCore(Uri absoluteUri)
    {
        if (_shellStartedHostPageLoad)
        {
            _shellStartedHostPageLoad = false;
            return;
        }
        OpenHarmonyBridge.WebCommand(ShellLoadCommand, absoluteUri.ToString());
    }

    /// <summary>
    /// .NET -> JS over the existing shell eval channel, guarded by the per-document marker
    /// (B3): the eval only delivers when the loaded document still carries the id the shell
    /// stamped for this handler's registration (window.__ohBlazorId). A skipped delivery is
    /// logged; the off-device no-host path evaluates to null and stays silent.
    /// <c>blazor.webview.js</c> registers its incoming-message callback through
    /// <c>window.external.receiveMessage(callback)</c>, so the Blazor bootstrap installs a
    /// Tizen-style <c>window.__dispatchMessageCallback</c> fan-out and the delivery goes there
    /// first. The shell's own <c>window.external.receiveMessage(message)</c> shim stays as the
    /// fallback before the bootstrap runs (it is the HybridWebView-compatible delivery path).
    /// The payload is escaped with <c>JsonSerializer.Serialize</c>.
    /// </summary>
    protected override void SendMessage(string message)
    {
        if (string.IsNullOrEmpty(message))
        {
            return;
        }
        _ = SendMessageCoreAsync(message);
    }

    private async Task SendMessageCoreAsync(string message)
    {
        string? result = await OpenHarmonyWebViewHandler.EvaluateJavaScriptAsyncCore(
            "(function(id,m){" +
            "if(window.__ohBlazorId!==id){return 'skip';}" +
            "if(typeof window.__dispatchMessageCallback==='function')" +
            "{window.__dispatchMessageCallback(m);}" +
            "else if(window.external&&typeof window.external.receiveMessage==='function')" +
            "{window.external.receiveMessage(m);}" +
            "return 'ok';})(" +
            JsonSerializer.Serialize(_pageDocumentId) + "," + JsonSerializer.Serialize(message) + ")").ConfigureAwait(false);
        if (result is not null && result.Trim().Trim('"') == "skip")
        {
            OpenHarmonyBridge.WriteStatus(
                "[maui] blazor message skipped: the loaded document is not this handler's page");
        }
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
