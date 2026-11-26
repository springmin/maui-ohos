namespace Microsoft.Maui.Platform;

#if !OPENHARMONY_BLAZOR_WEBVIEW
// Controls does not reference the BlazorWebView package; the real handler is compiled only when
// the app/harness defines OPENHARMONY_BLAZOR_WEBVIEW. This stub keeps the WebView handler's
// per-window overlay release hook available in both configurations.
internal static class OpenHarmonyBlazorWebViewHandler
{
    internal static void ReleaseWindowOverlays(string windowId)
    {
    }
}
#endif
