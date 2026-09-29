// Dispatcher provider for the platform slice: the compositor is single-threaded, so every
// thread resolves to the one OpenHarmony dispatcher. MAUI needs this when bindable objects are
// created outside the app's service scope (e.g. TabbedPage/MultiPage constructors).
using Microsoft.Maui.Dispatching;

namespace Microsoft.Maui.Platform;

public sealed class OpenHarmonyDispatcherProvider : IDispatcherProvider
{
    private readonly IDispatcher _dispatcher;

    public OpenHarmonyDispatcherProvider(IDispatcher dispatcher)
    {
        _dispatcher = dispatcher;
    }

    public IDispatcher? GetForCurrentThread() => _dispatcher;
}
