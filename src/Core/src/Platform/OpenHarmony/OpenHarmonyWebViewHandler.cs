// WebView handler for OpenHarmony: the ArkTS shell owns a hidden ArkWeb component; this handler
// shows it and drives it through OpenHarmonyBridge.WebCommand, and mirrors page events back.
using Microsoft.Maui.Graphics;
using Microsoft.Maui.Handlers;
using Microsoft.OpenHarmony.Hosting;

namespace Microsoft.Maui.Platform;

public sealed class OpenHarmonyWebViewHandler : OpenHarmonyViewHandler<IWebView>
{
    private static readonly List<OpenHarmonyWebViewHandler> s_handlers = new();

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
