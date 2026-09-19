// WebView handler for OpenHarmony: the ArkTS shell owns a hidden ArkWeb component; this handler
// shows it and drives it through OpenHarmonyBridge.WebCommand, and mirrors page events back.
// JavaScript: scripts run on the same ArkWeb component through the host's ohos_host_web_eval /
// notifyWebEvalResult pair, and page scripts reach managed code through the shell's dotnetHost
// proxy (window.__ohosDotNet), which raises JsMessage.
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Microsoft.Maui.Graphics;
using Microsoft.Maui.Handlers;
using Microsoft.OpenHarmony.Hosting;

namespace Microsoft.Maui.Platform;

public sealed class OpenHarmonyWebViewHandler : OpenHarmonyViewHandler<IWebView>
{
    private const string HostLibrary = "libopenharmonyhost.so";

    /// <summary>Scripts that a shell sink never answers must not leave callers waiting forever.</summary>
    private static readonly TimeSpan s_evalTimeout = TimeSpan.FromSeconds(10);

    private static readonly List<OpenHarmonyWebViewHandler> s_handlers = new();
    private static readonly ConcurrentDictionary<int, TaskCompletionSource<string?>> s_evalRequests = new();
    private static WebEvalResultCallback? s_evalResultCallback;
    private static WebJsMessageCallback? s_jsMessageCallback;
    private static bool s_evalRegistered;
    private static bool s_evalUnavailable;
    private static bool s_messageRegistered;
    private static bool s_messageUnavailable;
    private static int s_nextRequestId;

    [DllImport(HostLibrary, EntryPoint = "ohos_host_web_eval", CharSet = CharSet.Ansi)]
    private static extern int WebEvalNative(string script, int requestId);

    [DllImport(HostLibrary, EntryPoint = "ohos_host_web_js_register_result")]
    private static extern void WebRegisterResultNative(IntPtr callback);

    [DllImport(HostLibrary, EntryPoint = "ohos_host_web_js_register_message")]
    private static extern void WebRegisterMessageNative(IntPtr callback);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void WebEvalResultCallback(int requestId, IntPtr resultUtf8, int error);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void WebJsMessageCallback(IntPtr payloadUtf8);

    public static readonly IPropertyMapper<IWebView, OpenHarmonyWebViewHandler> Mapper =
        new PropertyMapper<IWebView, OpenHarmonyWebViewHandler>(ViewMapper)
        {
            [nameof(IWebView.Source)] = MapSource,
        };

    public OpenHarmonyWebViewHandler() : base(Mapper) { }

    protected override OpenHarmonyView CreatePlatformView() => new() { IsWebView = true, Background = Colors.White };

    protected override void ConnectHandler(OpenHarmonyView platformView)
    {
        base.ConnectHandler(platformView);
        lock (s_handlers)
        {
            s_handlers.Add(this);
        }
        // The managed half of the JavaScript bridge is bound as soon as a page can exist.
        EnsureMessageRegistered();
    }

    protected override void DisconnectHandler(OpenHarmonyView platformView)
    {
        lock (s_handlers)
        {
            s_handlers.Remove(this);
        }
        base.DisconnectHandler(platformView);
    }

    public override Size GetDesiredSize(double widthConstraint, double heightConstraint)
        => new(Math.Min(widthConstraint, widthConstraint), Math.Min(400, heightConstraint));

    public override void PlatformArrange(Rect frame)
    {
        base.PlatformArrange(frame);
        // The native Web component is a shell overlay, so it only needs to know it is visible.
        OpenHarmonyBridge.WebCommand("show");
    }

    // MAUI raises the JavaScript commands through IElementHandler.Invoke (Controls.WebView
    // wraps the script in try{JSON.stringify(eval(...))}catch(e){'null'} before calling us).
    public override void Invoke(string command, object? args)
    {
        switch (command)
        {
            case nameof(IWebView.EvaluateJavaScriptAsync) when args is EvaluateJavaScriptAsyncRequest request:
                _ = CompleteEvaluateAsync(request);
                return;
            case nameof(IWebView.Eval) when args is string script:
                // Fire-and-forget evaluation (IWebView.Eval has no result).
                _ = EvaluateJavaScriptAsyncCore(script);
                return;
            default:
                base.Invoke(command, args);
                return;
        }
    }

    /// <summary>Evaluates a script on the shell's ArkWeb page; null when no page or host answers.</summary>
    public Task<string?> EvaluateJavaScriptAsync(string script) => EvaluateJavaScriptAsyncCore(script);

    /// <summary>
    /// Sends the script to the ArkTS shell (registerWebEvalSink) and awaits the runJavaScript
    /// result by request id. A missing host library or sink answers null instead of throwing.
    /// </summary>
    internal static async Task<string?> EvaluateJavaScriptAsyncCore(string script)
    {
        if (string.IsNullOrEmpty(script) || s_evalUnavailable)
        {
            return null;
        }
        int requestId = Interlocked.Increment(ref s_nextRequestId);
        var source = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        s_evalRequests[requestId] = source;
        try
        {
            EnsureEvalRegistered();
            if (s_evalUnavailable || WebEvalNative(script, requestId) != 0)
            {
                s_evalRequests.TryRemove(requestId, out _);
                return null;
            }
        }
        catch (DllNotFoundException)
        {
            s_evalUnavailable = true;
            s_evalRequests.TryRemove(requestId, out _);
            return null;
        }
        catch (EntryPointNotFoundException)
        {
            s_evalUnavailable = true;
            s_evalRequests.TryRemove(requestId, out _);
            return null;
        }
        Task completed = await Task.WhenAny(source.Task, Task.Delay(s_evalTimeout)).ConfigureAwait(false);
        if (completed != source.Task)
        {
            s_evalRequests.TryRemove(requestId, out _);
            OpenHarmonyBridge.WriteStatus("[maui] web eval request timed out");
            return null;
        }
        return await source.Task.ConfigureAwait(false);
    }

    private static async Task CompleteEvaluateAsync(EvaluateJavaScriptAsyncRequest request)
    {
        string? result = await EvaluateJavaScriptAsyncCore(request.Script).ConfigureAwait(false);
        // An empty string stands in for "the platform had no result"; Controls.WebView maps
        // "null" to null and trims the quotes JSON.stringify adds on non-Android platforms.
        request.TrySetResult(result ?? string.Empty);
    }

    /// <summary>True once the host library answered a script evaluation or registration.</summary>
    internal static bool IsJavaScriptBridgeAvailable => !s_evalUnavailable;

    /// <summary>The shell answers a script evaluation (host.notifyWebEvalResult).</summary>
    internal static void CompleteEvalResult(int requestId, string result, bool error)
    {
        if (s_evalRequests.TryRemove(requestId, out TaskCompletionSource<string?>? source))
        {
            source.TrySetResult(error ? null : result);
        }
    }

    /// <summary>
    /// A page script posted a message through the dotnetHost proxy (host.notifyJsMessage).
    /// The payload is raised on <see cref="JsMessage"/> and offered to HybridWebView
    /// handlers (see <see cref="OpenHarmonyHybridWebViewHandler.OnJsMessage"/>).
    /// </summary>
    internal static void HandleJsMessage(string payload)
    {
        JsMessage?.Invoke(payload);
        OpenHarmonyHybridWebViewHandler.OnJsMessage(payload);
    }

    private static void OnEvalResultNative(int requestId, IntPtr resultUtf8, int error)
    {
        string result = resultUtf8 == IntPtr.Zero ? string.Empty : Marshal.PtrToStringUTF8(resultUtf8) ?? string.Empty;
        CompleteEvalResult(requestId, result, error != 0);
    }

    private static void OnJsMessageNative(IntPtr payloadUtf8)
    {
        string payload = payloadUtf8 == IntPtr.Zero ? string.Empty : Marshal.PtrToStringUTF8(payloadUtf8) ?? string.Empty;
        HandleJsMessage(payload);
    }

    private static void EnsureEvalRegistered()
    {
        if (s_evalRegistered || s_evalUnavailable)
        {
            return;
        }
        try
        {
            s_evalResultCallback = OnEvalResultNative;
            WebRegisterResultNative(Marshal.GetFunctionPointerForDelegate(s_evalResultCallback));
            s_evalRegistered = true;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            s_evalUnavailable = true;
            OpenHarmonyBridge.WriteStatus("[maui] web eval bridge unavailable (no host library)");
        }
    }

    private static void EnsureMessageRegistered()
    {
        if (s_messageRegistered || s_messageUnavailable)
        {
            return;
        }
        try
        {
            s_jsMessageCallback = OnJsMessageNative;
            WebRegisterMessageNative(Marshal.GetFunctionPointerForDelegate(s_jsMessageCallback));
            s_messageRegistered = true;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            s_messageUnavailable = true;
            OpenHarmonyBridge.WriteStatus("[maui] web message bridge unavailable (no host library)");
        }
    }

    /// <summary>Raised for every message a page posts through the shell's dotnetHost proxy.</summary>
    public static event Action<string>? JsMessage;

    public static void MapSource(OpenHarmonyWebViewHandler handler, IWebView webView)
    {
        switch (webView.Source)
        {
            case UrlWebViewSource url when !string.IsNullOrEmpty(url.Url):
                OpenHarmonyBridge.WebCommand("load", url.Url);
                break;
            case HtmlWebViewSource html:
                OpenHarmonyBridge.WebCommand("data", html.Html);
                break;
            default:
                OpenHarmonyBridge.WebCommand("show");
                break;
        }
    }

    /// <summary>Mirrors a shell page event into the MAUI WebView events.</summary>
    public static void OnPageEvent(string state, string url)
    {
        lock (s_handlers)
        {
            foreach (OpenHarmonyWebViewHandler handler in s_handlers.ToArray())
            {
                if (handler.VirtualView is not { } webView)
                {
                    continue;
                }
                if (state == "started")
                {
                    webView.Navigating(WebNavigationEvent.NewPage, url);
                }
                else
                {
                    // IWebView only exposes Navigating; the Controls Navigated event is raised by
                    // the platform handler internals, so completion is logged for now.
                    OpenHarmonyBridge.WriteStatus($"[maui] web {state}: {url}");
                }
            }
        }
    }
}
