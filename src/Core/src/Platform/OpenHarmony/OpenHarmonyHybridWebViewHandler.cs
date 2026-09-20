// Minimal HybridWebView handler for OpenHarmony: the MAUI HybridWebView contracts
// (EvaluateJavaScriptAsync, SendRawMessage, RawMessageReceived and InvokeJavaScriptAsync) ride
// the same shell channel as the WebView handler: scripts run through ohos_host_web_eval and
// page messages arrive through the shell's dotnetHost proxy.
//
// Asset serving: HybridRoot/DefaultFile are wired to the ArkTS shell. When the handler is
// connected (or either property changes) it extracts the framework bootstrap script
// (_framework/hybridwebview.js) out of the Microsoft.Maui assembly into the extracted app
// payload directory and registers that directory + HybridRoot + DefaultFile with the shell
// through the "hybrid" web command. The shell answers requests for the MAUI hybrid origin
// (https://0.0.0.1/, see HybridWebViewHandler.AppOrigin) from the payload with
// onInterceptRequest (file reads through @ohos.file.fs) and loads the origin, so a stock
// HybridWebView page renders and window.HybridWebView.SendRawMessage reaches
// RawMessageReceived through the shell's __hwvSendMessage forwarding.
//
// Scope note: the JS -> .NET InvokeDotNet endpoint (__hwvInvokeDotNet) is not implemented -
// ArkWeb expects an intercepted response synchronously, and invoking a managed method needs an
// asynchronous response stream (setResponseIsReady/delayed data) that this batch does not add.
// window.HybridWebView.InvokeDotNet therefore rejects; the .NET -> JS direction
// (EvaluateJavaScriptAsync / InvokeJavaScriptAsync) keeps working. Off-device (no host
// library / no app context) registration is a no-op.
using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Maui.Graphics;
using Microsoft.Maui.Handlers;
using Microsoft.OpenHarmony.Hosting;

namespace Microsoft.Maui.Platform;

public sealed class OpenHarmonyHybridWebViewHandler : OpenHarmonyViewHandler<IHybridWebView>
{
    /// <summary>Message prefix the stock HybridWebView JavaScript uses for raw messages.</summary>
    internal const string RawMessagePrefix = "__RawMessage|";

    internal const string InvokeCompletedPrefix = "__InvokeJavaScriptCompleted|";
    internal const string InvokeFailedPrefix = "__InvokeJavaScriptFailed|";

    /// <summary>Embedded framework script path (resource name in the Microsoft.Maui assembly).</summary>
    internal const string HybridWebViewScriptPath = "_framework/hybridwebview.js";

    /// <summary>MAUI hybrid origin the shell serves the app package from.</summary>
    internal const string HybridAppOrigin = "https://0.0.0.1/";

    private static readonly TimeSpan s_invokeTimeout = TimeSpan.FromSeconds(10);
    private static readonly List<OpenHarmonyHybridWebViewHandler> s_handlers = new();
    private static readonly ConcurrentDictionary<string, TaskCompletionSource<string?>> s_invokeRequests = new();

    public static readonly IPropertyMapper<IHybridWebView, OpenHarmonyHybridWebViewHandler> Mapper =
        new PropertyMapper<IHybridWebView, OpenHarmonyHybridWebViewHandler>(ViewMapper)
        {
            [nameof(IHybridWebView.HybridRoot)] = MapHybridAssets,
            [nameof(IHybridWebView.DefaultFile)] = MapHybridAssets,
        };

    public OpenHarmonyHybridWebViewHandler() : base(Mapper) { }

    protected override OpenHarmonyView CreatePlatformView() => new() { IsWebView = true, Background = Colors.White };

    protected override void ConnectHandler(OpenHarmonyView platformView)
    {
        base.ConnectHandler(platformView);
        lock (s_handlers)
        {
            s_handlers.Add(this);
        }
        // The hybrid page posts its messages through the shell's JS message sink, so the sink
        // must be bound even when the app never connects the regular WebView handler.
        OpenHarmonyWebViewHandler.EnsureMessageRegistered();
        RegisterHybridAssets();
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
        // The ArkWeb component is a shell overlay, so it only needs to know it is visible.
        OpenHarmonyBridge.WebCommand("show");
    }

    /// <summary>
    /// HybridRoot/DefaultFile mapper: (re)registers the asset root with the shell. Without a
    /// host library (tests) or an extracted app directory the call is a no-op.
    /// </summary>
    public static void MapHybridAssets(OpenHarmonyHybridWebViewHandler handler, IHybridWebView webView)
        => handler.RegisterHybridAssets();

    private void RegisterHybridAssets()
    {
        OpenHarmonyAppContext? context = OpenHarmonyBridge.Context;
        if (context is null || string.IsNullOrEmpty(context.AppDir))
        {
            return;
        }
        string root = VirtualView?.HybridRoot is { Length: > 0 } hybridRoot ? hybridRoot : "wwwroot";
        string defaultFile = VirtualView?.DefaultFile is { Length: > 0 } file ? file : "index.html";
        string payloadDir = context.AppDir.TrimEnd('/');
        EnsureHybridWebViewScript(payloadDir);
        OpenHarmonyBridge.WebCommand("hybrid", JsonSerializer.Serialize(new HybridAssetsConfig
        {
            Base = payloadDir,
            Root = root,
            DefaultFile = defaultFile,
        }));
    }

    /// <summary>
    /// Copies the framework bootstrap script (embedded in the Microsoft.Maui assembly) next to
    /// the extracted payload, where the shell serves it from as _framework/hybridwebview.js.
    /// </summary>
    private static void EnsureHybridWebViewScript(string payloadDir)
    {
        try
        {
            using Stream? script = typeof(Microsoft.Maui.Handlers.HybridWebViewHandler).Assembly
                .GetManifestResourceStream(HybridWebViewScriptPath);
            if (script is null)
            {
                return;
            }
            string destination = Path.Combine(payloadDir, "_framework", "hybridwebview.js");
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            using FileStream file = File.Create(destination);
            script.CopyTo(file);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            OpenHarmonyBridge.WriteStatus($"[maui] hybrid bootstrap script extraction failed: {ex.Message}");
        }
    }

    /// <summary>Payload descriptor the shell consumes from the "hybrid" web command.</summary>
    private sealed class HybridAssetsConfig
    {
        [JsonPropertyName("base")]
        public string Base { get; init; } = string.Empty;

        [JsonPropertyName("root")]
        public string Root { get; init; } = string.Empty;

        [JsonPropertyName("defaultFile")]
        public string DefaultFile { get; init; } = string.Empty;
    }

    // Controls.HybridWebView raises these commands through IElementHandler.Invoke/InvokeAsync;
    // each command carries a TaskCompletionSource the caller awaits, so every branch must
    // complete it (an unmapped command would leave the caller waiting forever).
    public override void Invoke(string command, object? args)
    {
        switch (command)
        {
            case nameof(IHybridWebView.EvaluateJavaScriptAsync) when args is EvaluateJavaScriptAsyncRequest request:
                _ = CompleteEvaluateAsync(request);
                return;
            case nameof(IHybridWebView.SendRawMessage) when args is HybridWebViewRawMessage message:
                if (message.Message is { } rawMessage)
                {
                    SendRawMessage(rawMessage);
                }
                return;
            case nameof(IHybridWebView.InvokeJavaScriptAsync) when args is HybridWebViewInvokeJavaScriptRequest request:
                _ = CompleteInvokeAsync(request);
                return;
            default:
                base.Invoke(command, args);
                return;
        }
    }

    /// <summary>Evaluates a script on the shell's ArkWeb page; null when no page or host answers.</summary>
    public Task<string?> EvaluateJavaScriptAsync(string script)
        => OpenHarmonyWebViewHandler.EvaluateJavaScriptAsyncCore(script);

    /// <summary>
    /// Delivers a raw message to the hybrid page. The stock HybridWebView JavaScript receives
    /// host messages through window.external.receiveMessage; the fallback dispatches the same
    /// HybridWebViewMessageReceived event that the stock script raises.
    /// </summary>
    public void SendRawMessage(string rawMessage)
    {
        if (string.IsNullOrEmpty(rawMessage))
        {
            return;
        }
        string json = JsonSerializer.Serialize(rawMessage);
        _ = OpenHarmonyWebViewHandler.EvaluateJavaScriptAsyncCore(
            "(function(m){if(window.external&&typeof window.external.receiveMessage==='function')" +
            "{window.external.receiveMessage(m);}" +
            "else{window.dispatchEvent(new CustomEvent('HybridWebViewMessageReceived',{detail:{message:m}}));}})" +
            "(" + json + ")");
    }

    /// <summary>
    /// Routes a shell dotnetHost.postMessage payload to the HybridWebView protocol:
    /// __InvokeJavaScriptCompleted/__InvokeJavaScriptFailed complete JS invocations and every
    /// other payload (with or without the stock __RawMessage prefix) is a raw message.
    /// </summary>
    internal static void OnJsMessage(string payload)
    {
        if (string.IsNullOrEmpty(payload))
        {
            return;
        }
        if (payload.StartsWith(InvokeCompletedPrefix, StringComparison.Ordinal) ||
            payload.StartsWith(InvokeFailedPrefix, StringComparison.Ordinal))
        {
            CompleteInvoke(payload);
            return;
        }
        string message = payload.StartsWith(RawMessagePrefix, StringComparison.Ordinal)
            ? Uri.UnescapeDataString(payload.Substring(RawMessagePrefix.Length))
            : payload;
        lock (s_handlers)
        {
            foreach (OpenHarmonyHybridWebViewHandler handler in s_handlers.ToArray())
            {
                handler.VirtualView?.RawMessageReceived(message);
            }
        }
    }

    private static async Task CompleteEvaluateAsync(EvaluateJavaScriptAsyncRequest request)
    {
        string? result = await OpenHarmonyWebViewHandler.EvaluateJavaScriptAsyncCore(request.Script).ConfigureAwait(false);
        // An empty string stands in for "the platform had no result" (HybridWebView returns null
        // for null, "null" and "undefined" results alike).
        request.TrySetResult(result ?? string.Empty);
    }

    private static async Task CompleteInvokeAsync(HybridWebViewInvokeJavaScriptRequest request)
    {
        try
        {
            request.TrySetResult(await InvokeJavaScriptAsyncCore(request).ConfigureAwait(false));
        }
        catch (Exception ex)
        {
            request.TrySetException(ex);
        }
    }

    /// <summary>
    /// Starts window.HybridWebView.__InvokeJavaScript and waits for the page to post the result
    /// back through dotnetHost (__InvokeJavaScriptCompleted|taskId|json). The stock MAUI
    /// JavaScript already speaks this protocol.
    /// </summary>
    private static async Task<object?> InvokeJavaScriptAsyncCore(HybridWebViewInvokeJavaScriptRequest request)
    {
        string argList = string.Empty;
        if (request.ParamValues is { } values)
        {
            argList = string.Join(", ", values.Select((value, index) =>
                value is null ? "null" : JsonSerializer.Serialize(value, request.ParamJsonTypeInfos![index]!)));
        }
        string taskId = Guid.NewGuid().ToString("N");
        var source = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        s_invokeRequests[taskId] = source;
        _ = await OpenHarmonyWebViewHandler.EvaluateJavaScriptAsyncCore(
            $"window.HybridWebView.__InvokeJavaScript({JsonSerializer.Serialize(taskId)}, " +
            $"{JsonSerializer.Serialize(request.MethodName)}, [{argList}])").ConfigureAwait(false);
        if (!OpenHarmonyWebViewHandler.IsJavaScriptBridgeAvailable)
        {
            // No host library: the page never received the kickoff, so do not wait for a reply.
            s_invokeRequests.TryRemove(taskId, out _);
            return null;
        }
        Task completed = await Task.WhenAny(source.Task, Task.Delay(s_invokeTimeout)).ConfigureAwait(false);
        if (completed != source.Task)
        {
            s_invokeRequests.TryRemove(taskId, out _);
            OpenHarmonyBridge.WriteStatus("[maui] hybrid JavaScript invocation timed out");
            return null;
        }
        string? result = await source.Task.ConfigureAwait(false);
        if (string.IsNullOrEmpty(result) || result == "null" || result == "undefined")
        {
            return null;
        }
        if (request.ReturnTypeJsonTypeInfo is null)
        {
            return null;
        }
        return JsonSerializer.Deserialize(result, request.ReturnTypeJsonTypeInfo);
    }

    private static void CompleteInvoke(string payload)
    {
        bool failed = payload.StartsWith(InvokeFailedPrefix, StringComparison.Ordinal);
        string content = payload.Substring(failed ? InvokeFailedPrefix.Length : InvokeCompletedPrefix.Length);
        int separator = content.IndexOf('|', StringComparison.Ordinal);
        if (separator < 0)
        {
            return;
        }
        string taskId = content.Substring(0, separator);
        string result = content.Substring(separator + 1);
        if (s_invokeRequests.TryRemove(taskId, out TaskCompletionSource<string?>? source))
        {
            if (failed)
            {
                source.TrySetException(new InvalidOperationException($"JavaScript invocation failed: {result}"));
            }
            else
            {
                source.TrySetResult(result);
            }
        }
    }
}
