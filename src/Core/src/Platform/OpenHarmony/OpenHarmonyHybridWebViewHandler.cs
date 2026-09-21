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
// The app context (and with it <c>AppDir</c>) can be published after the handler connects
// (host startup ordering), so registration is lazy: a connect without a payload directory
// is remembered and retried from the bridge's Initialized/SurfaceChanged signals (late
// subscribers get the current state replayed) and from the first PlatformArrange, and every
// pending root is registered exactly once (repeated mapper passes or event replays do not
// re-issue the command).
//
// JS -> .NET invocation: the stock hybridwebview.js POSTs { MethodName, ParamValues } to
// <origin>/__hwvInvokeDotNet. The ArkTS shell holds the intercepted WebResourceResponse open
// with setResponseIsReady(false), forwards the invocation through
// host.notifyHybridInvoke(requestId, method, argsJson), and completes the response when this
// handler calls ohos_host_hwv_invoke_result(requestId, payloadJson) with the
// DotNetInvokeResult JSON the page's fetch expects. A missing invoker, a failed or timed-out
// target method and a missing host/shell path all answer an error payload, so the page's
// promise rejects instead of hanging. Off-device (no host library / no app context) the
// registration is remembered as pending and retried when the bridge publishes the context;
// callers can still drive the managed half directly.
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
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

    /// <summary>
    /// Largest JS -&gt; .NET payload the managed boundary accepts from one page message, in UTF-16
    /// code units (4 MiB). The host marshals the whole UTF-8 string out of the shell before managed
    /// code runs, so this cap cannot undo that first native copy; it bounds everything managed does
    /// with the string (the <see cref="Uri.UnescapeDataString(string)"/> copy, prefix parsing,
    /// per-handler dispatch, JSON parsing of invocation arguments/results and any app-level work)
    /// and rejects oversized payloads with a clear error instead of letting a hostile page grow the
    /// managed heap without bound. 4 MiB is ~130x the largest legitimate payload the interaction
    /// harness exercises (32 KiB) and far above any window.HybridWebView control message.
    /// </summary>
    internal const int MaxPagePayloadLength = 4 * 1024 * 1024;

    internal const string InvokeCompletedPrefix = "__InvokeJavaScriptCompleted|";
    internal const string InvokeFailedPrefix = "__InvokeJavaScriptFailed|";

    /// <summary>Embedded framework script path (resource name in the Microsoft.Maui assembly).</summary>
    internal const string HybridWebViewScriptPath = "_framework/hybridwebview.js";

    /// <summary>MAUI hybrid origin the shell serves the app package from.</summary>
    internal const string HybridAppOrigin = "https://0.0.0.1/";

    private const string HostLibrary = "libopenharmonyhost.so";

    private static readonly TimeSpan s_invokeTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan s_dotNetInvokeTimeout = TimeSpan.FromSeconds(10);
    private static readonly List<OpenHarmonyHybridWebViewHandler> s_handlers = new();
    private static readonly ConcurrentDictionary<string, TaskCompletionSource<string?>> s_invokeRequests = new();

    // Handlers that connected before the app context published an AppDir. Registration is
    // retried from the bridge signals below (and from the first arrange) and the handler is
    // removed as soon as its assets land, so each pending root registers exactly once.
    private static readonly HashSet<OpenHarmonyHybridWebViewHandler> s_pendingRegistration = new();
    private static bool s_registrationHooksAttached;
    private string? _registeredAssets;

    /// <summary>Raised after the "hybrid" shell command is issued (test/diagnostic hook).</summary>
    internal event Action<string, string, string>? HybridAssetsRegistered;

    /// <summary>True while this handler's asset registration waits for the app context.</summary>
    internal bool IsHybridAssetsRegistrationPending
    {
        get
        {
            lock (s_handlers)
            {
                return s_pendingRegistration.Contains(this);
            }
        }
    }

    /// <summary>The payload layout last registered with the shell, or null (test/diagnostic hook).</summary>
    internal string? RegisteredHybridAssets
    {
        get
        {
            lock (s_handlers)
            {
                return _registeredAssets;
            }
        }
    }

    // The shell forwards __hwvInvokeDotNet invocations to ohos_host_hwv_register_invoke's
    // callback; the handler that last registered its assets with the shell is the page a
    // request belongs to (the shell serves one hybrid origin at a time).
    private static HybridInvokeCallback? s_hybridInvokeCallback;
    private static bool s_invokeRegistered;
    private static bool s_invokeUnavailable;
    private static OpenHarmonyHybridWebViewHandler? s_activeInvokeHandler;

    [DllImport(HostLibrary, EntryPoint = "ohos_host_hwv_register_invoke")]
    private static extern void RegisterInvokeNative(IntPtr callback);

    [DllImport(HostLibrary, EntryPoint = "ohos_host_hwv_invoke_result", CharSet = CharSet.Ansi)]
    private static extern int InvokeResultNative(int requestId, string payloadJson);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void HybridInvokeCallback(int requestId, IntPtr methodUtf8, IntPtr argsUtf8);

    /// <summary>Receives the completed invocation payload when the host library is present.</summary>
    internal static event Action<int, string>? HybridInvokeResultSent;

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
        EnsureHybridInvokeRegistered();
        s_activeInvokeHandler = this;
        RegisterHybridAssets();
    }

    protected override void DisconnectHandler(OpenHarmonyView platformView)
    {
        lock (s_handlers)
        {
            s_handlers.Remove(this);
            s_pendingRegistration.Remove(this);
        }
        if (ReferenceEquals(s_activeInvokeHandler, this))
        {
            s_activeInvokeHandler = null;
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
        // First render: if ConnectHandler ran before the app context was published, this is
        // the point where the shell (and the extracted payload) is definitely available.
        if (IsHybridAssetsRegistrationPending)
        {
            RegisterHybridAssets();
        }
    }

    /// <summary>
    /// HybridRoot/DefaultFile mapper: (re)registers the asset root with the shell. Without a
    /// host library (tests) or an extracted app directory the call is remembered and retried
    /// when the app context becomes available.
    /// </summary>
    public static void MapHybridAssets(OpenHarmonyHybridWebViewHandler handler, IHybridWebView webView)
        => handler.RegisterHybridAssets();

    private void RegisterHybridAssets()
    {
        OpenHarmonyAppContext? context = OpenHarmonyBridge.Context;
        if (context is null || string.IsNullOrEmpty(context.AppDir))
        {
            // The shell may publish the app directory after the handler connected; do not drop
            // the registration. The bridge replays its current context/surface to late
            // subscribers, so the hooks fire both when the state already exists and when it
            // appears later.
            MarkRegistrationPending(this);
            return;
        }
        string root = VirtualView?.HybridRoot is { Length: > 0 } hybridRoot ? hybridRoot : "wwwroot";
        string defaultFile = VirtualView?.DefaultFile is { Length: > 0 } file ? file : "index.html";
        string payloadDir = context.AppDir.TrimEnd('/');
        string key = payloadDir + "|" + root + "|" + defaultFile;
        bool register;
        lock (s_handlers)
        {
            // The shell is serving this handler's page, so invocations belong to it.
            s_activeInvokeHandler = this;
            register = key != _registeredAssets;
            _registeredAssets = key;
            s_pendingRegistration.Remove(this);
        }
        if (!register)
        {
            // ConnectHandler and the HybridRoot/DefaultFile mapper both land here; only
            // re-issue the shell command when the payload layout actually changed.
            return;
        }
        EnsureHybridWebViewScript(payloadDir);
        OpenHarmonyBridge.WebCommand("hybrid", JsonSerializer.Serialize(new HybridAssetsConfig
        {
            Base = payloadDir,
            Root = root,
            DefaultFile = defaultFile,
        }));
        HybridAssetsRegistered?.Invoke(payloadDir, root, defaultFile);
    }

    /// <summary>Remembers a handler whose assets are waiting for the app context.</summary>
    private static void MarkRegistrationPending(OpenHarmonyHybridWebViewHandler handler)
    {
        lock (s_handlers)
        {
            s_pendingRegistration.Add(handler);
        }
        EnsureRegistrationHooks();
    }

    /// <summary>
    /// Installs one process-wide retry subscription on the bridge signals that can announce a
    /// late app context (Initialized replays the current context, SurfaceChanged the last
    /// surface). Idempotent; the hooks stay for the process lifetime.
    /// </summary>
    private static void EnsureRegistrationHooks()
    {
        lock (s_handlers)
        {
            if (s_registrationHooksAttached)
            {
                return;
            }
            s_registrationHooksAttached = true;
        }
        OpenHarmonyBridge.Initialized += _ => RetryPendingRegistrations();
        OpenHarmonyBridge.SurfaceChanged += _ => RetryPendingRegistrations();
    }

    /// <summary>
    /// Re-attempts the asset registration of every connected handler that is still waiting for
    /// the app context. Exposed for tests; the bridge signals and the first arrange call it.
    /// </summary>
    internal static void RetryPendingRegistrations()
    {
        OpenHarmonyHybridWebViewHandler[] pending;
        lock (s_handlers)
        {
            if (s_pendingRegistration.Count == 0)
            {
                return;
            }
            pending = s_pendingRegistration.Where(s_handlers.Contains).ToArray();
        }
        foreach (OpenHarmonyHybridWebViewHandler handler in pending)
        {
            handler.RegisterHybridAssets();
        }
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

    // --- JS -> .NET invocation (__hwvInvokeDotNet) --------------------------------------

    /// <summary>True once the host library accepted the invocation callback registration.</summary>
    internal static bool IsInvokeBridgeAvailable => s_invokeRegistered && !s_invokeUnavailable;

    /// <summary>Registers the managed invocation callback with the host (idempotent, device only).</summary>
    internal static void EnsureHybridInvokeRegistered()
    {
        if (s_invokeRegistered || s_invokeUnavailable)
        {
            return;
        }
        try
        {
            // Keep the delegate alive for the process lifetime: the host stores the raw
            // function pointer and calls it on an arbitrary thread.
            s_hybridInvokeCallback = OnHybridInvokeNative;
            RegisterInvokeNative(Marshal.GetFunctionPointerForDelegate(s_hybridInvokeCallback));
            s_invokeRegistered = true;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            s_invokeUnavailable = true;
            OpenHarmonyBridge.WriteStatus("[maui] hybrid invoke bridge unavailable (no host library)");
        }
    }

    private static void OnHybridInvokeNative(int requestId, IntPtr methodUtf8, IntPtr argsUtf8)
    {
        string method = methodUtf8 == IntPtr.Zero ? string.Empty : Marshal.PtrToStringUTF8(methodUtf8) ?? string.Empty;
        string args = argsUtf8 == IntPtr.Zero ? string.Empty : Marshal.PtrToStringUTF8(argsUtf8) ?? string.Empty;
        _ = CompleteHybridInvokeAsync(requestId, method, args);
    }

    /// <summary>
    /// Managed half of host.notifyHybridInvoke: services one JS invocation and completes it.
    /// Exposed for tests; the native callback feeds the same path.
    /// </summary>
    internal static Task OnHybridInvokeAsync(int requestId, string methodName, string argsJson)
        => CompleteHybridInvokeAsync(requestId, methodName, argsJson);

    private static async Task CompleteHybridInvokeAsync(int requestId, string methodName, string argsJson)
    {
        // JS -> .NET invocation arguments are page -> .NET payloads too, so the same cap applies
        // before anything is parsed or dispatched; the page gets a clear error result back.
        if (methodName.Length > MaxPagePayloadLength || argsJson.Length > MaxPagePayloadLength)
        {
            SendInvokeResult(requestId, ErrorPayload(new InvalidOperationException(
                $"the invocation payload exceeds the {MaxPagePayloadLength} character page payload cap " +
                $"(method={methodName.Length}, args={argsJson.Length})")));
            return;
        }
        string payload;
        try
        {
            OpenHarmonyHybridWebViewHandler? handler = s_activeInvokeHandler;
            payload = handler is null
                ? ErrorPayload(new InvalidOperationException("no HybridWebView page is registered with the shell"))
                : await handler.InvokeDotNetAsync(methodName, argsJson).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            payload = ErrorPayload(ex);
        }
        SendInvokeResult(requestId, payload);
    }

    /// <summary>
    /// Dispatches one __hwvInvokeDotNet invocation to <see cref="IHybridWebView.Invoker"/> and
    /// returns the DotNetInvokeResult JSON the stock hybridwebview.js expects. Never throws:
    /// every failure becomes an error payload the page rejects with.
    /// </summary>
    internal async Task<string> InvokeDotNetAsync(string methodName, string argsJson)
    {
        if (string.IsNullOrEmpty(methodName))
        {
            return ErrorPayload(new InvalidOperationException("the invocation did not provide a method name"));
        }
        string[]? paramValues = null;
        if (!string.IsNullOrEmpty(argsJson) && argsJson != "[]")
        {
            try
            {
                paramValues = JsonSerializer.Deserialize<string[]>(argsJson);
            }
            catch (JsonException ex)
            {
                return ErrorPayload(new InvalidOperationException(
                    $"the invocation parameters were not a JSON string array: {ex.Message}"));
            }
        }
        if (VirtualView is not { } webView)
        {
            return ErrorPayload(new InvalidOperationException("the HybridWebView is not connected"));
        }
        try
        {
            // Controls.HybridWebView.Invoker throws when no InvokeJavaScriptTarget was set;
            // other implementations may return null. Both become an error payload.
            Task<string?> invocation = webView.Invoker.InvokeMethodAsync(methodName, paramValues);
            Task completed = await Task.WhenAny(invocation, Task.Delay(s_dotNetInvokeTimeout)).ConfigureAwait(false);
            if (completed != invocation)
            {
                return ErrorPayload(new TimeoutException(
                    $"the .NET method '{methodName}' did not complete within {s_dotNetInvokeTimeout.TotalSeconds:0} seconds"));
            }
            return SuccessPayload(await invocation.ConfigureAwait(false));
        }
        catch (Exception ex)
        {
            return ErrorPayload(ex);
        }
    }

    private static void SendInvokeResult(int requestId, string payload)
    {
        try
        {
            InvokeResultNative(requestId, payload);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            s_invokeUnavailable = true;
            OpenHarmonyBridge.WriteStatus("[maui] hybrid invoke bridge unavailable (no host library)");
        }
        // Tests and diagnostics observe the payload even when the native export is absent.
        HybridInvokeResultSent?.Invoke(requestId, payload);
    }

    /// <summary>Result shape the stock invokeDotNet code reads (see HybridWebViewHandler).</summary>
    private static string SuccessPayload(string? jsonResult)
        => JsonSerializer.Serialize(new DotNetInvokeResult
        {
            Result = jsonResult,
            IsJson = jsonResult is not null,
        });

    private static string ErrorPayload(Exception ex)
        => JsonSerializer.Serialize(new DotNetInvokeResult
        {
            IsError = true,
            ErrorMessage = ex.Message,
            ErrorType = ex.GetType().Name,
            ErrorStackTrace = ex.StackTrace,
        });

    private sealed class DotNetInvokeResult
    {
        [JsonPropertyName("Result")]
        public string? Result { get; init; }

        [JsonPropertyName("IsJson")]
        public bool IsJson { get; init; }

        [JsonPropertyName("IsError")]
        public bool IsError { get; init; }

        [JsonPropertyName("ErrorMessage")]
        public string? ErrorMessage { get; init; }

        [JsonPropertyName("ErrorType")]
        public string? ErrorType { get; init; }

        [JsonPropertyName("ErrorStackTrace")]
        public string? ErrorStackTrace { get; init; }
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
        if (payload.Length > MaxPagePayloadLength)
        {
            RejectOversizedPayload(payload);
            return;
        }
        if (payload.StartsWith(InvokeCompletedPrefix, StringComparison.Ordinal) ||
            payload.StartsWith(InvokeFailedPrefix, StringComparison.Ordinal))
        {
            CompleteInvoke(payload);
            return;
        }
        string message;
        try
        {
            message = payload.StartsWith(RawMessagePrefix, StringComparison.Ordinal)
                ? Uri.UnescapeDataString(payload.Substring(RawMessagePrefix.Length))
                : payload;
        }
        catch (UriFormatException)
        {
            // UnescapeDataString's contract still allows UriFormatException for a malformed escape
            // sequence; never let that cross back into the native callback, drop the message.
            OpenHarmonyBridge.WriteStatus("[maui] hybrid raw message rejected: invalid escape sequence");
            return;
        }
        lock (s_handlers)
        {
            foreach (OpenHarmonyHybridWebViewHandler handler in s_handlers.ToArray())
            {
                handler.VirtualView?.RawMessageReceived(message);
            }
        }
    }

    /// <summary>
    /// Rejects one page payload above <see cref="MaxPagePayloadLength"/>: a pending JS invocation
    /// whose result is oversized is completed with a clear error (the page's promise rejects)
    /// instead of waiting for its timeout, and every other oversized payload is dropped with a
    /// status log. Never throws.
    /// </summary>
    private static void RejectOversizedPayload(string payload)
    {
        string prefix = payload.StartsWith(InvokeCompletedPrefix, StringComparison.Ordinal) ? InvokeCompletedPrefix
            : payload.StartsWith(InvokeFailedPrefix, StringComparison.Ordinal) ? InvokeFailedPrefix
            : string.Empty;
        if (prefix.Length > 0)
        {
            int separator = payload.IndexOf('|', prefix.Length, StringComparison.Ordinal);
            if (separator > prefix.Length)
            {
                string taskId = payload.Substring(prefix.Length, separator - prefix.Length);
                if (s_invokeRequests.TryRemove(taskId, out TaskCompletionSource<string?>? source))
                {
                    source.TrySetException(new InvalidOperationException(
                        $"the JavaScript invocation payload exceeds the {MaxPagePayloadLength} character page payload cap"));
                }
            }
        }
        OpenHarmonyBridge.WriteStatus(
            $"[maui] hybrid page payload rejected: {payload.Length} characters exceed the {MaxPagePayloadLength} character cap");
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
