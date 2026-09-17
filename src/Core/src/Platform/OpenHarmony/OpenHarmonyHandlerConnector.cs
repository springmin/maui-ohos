// Shared handler wiring for the platform slice: MAUI's own platform-partial handlers have no
// platform view here, so the slice connects its own handlers (exact type -> interfaces ->
// base types) for an element tree.
using Microsoft.Maui.Hosting;

namespace Microsoft.Maui.Platform;

public static class OpenHarmonyHandlerConnector
{
    /// <summary>Maui context of the running application (set by the app host).</summary>
    public static IMauiContext? Context { get; set; }

    /// <summary>Resolves the slice handler for a view type (exact type, interfaces, base types).</summary>
    public static Type? FindSliceHandlerType(Type viewType)
    {
        if (MauiOpenHarmonyExtensions.SliceHandlers.TryGetValue(viewType, out Type? exact))
        {
            return exact;
        }
        foreach (Type iface in viewType.GetInterfaces())
        {
            if (MauiOpenHarmonyExtensions.SliceHandlers.TryGetValue(iface, out Type? byInterface))
            {
                return byInterface;
            }
        }
        for (Type? type = viewType.BaseType; type is not null; type = type.BaseType)
        {
            if (MauiOpenHarmonyExtensions.SliceHandlers.TryGetValue(type, out Type? byBase))
            {
                return byBase;
            }
        }
        return null;
    }

    public static IElementHandler? HandlerFor(Type handlerType)
        => Activator.CreateInstance(handlerType) as IElementHandler;

    /// <summary>Connects the slice handler of a single element when it has none.</summary>
    public static void Connect(IElement? element)
    {
        if (element is null || element.Handler is not null)
        {
            return;
        }
        if (FindSliceHandlerType(element.GetType()) is { } handlerType &&
            HandlerFor(handlerType) is { } handler &&
            Context is not null)
        {
            handler.SetMauiContext(Context);
            element.Handler = handler;
        }
    }

    /// <summary>Connects handlers for an element and its descendants (idempotent).</summary>
    public static void ConnectTree(IElement? element)
    {
        Connect(element);
        if (element is ILayout layout)
        {
            foreach (IView child in layout)
            {
                ConnectTree(child);
            }
        }
        else if (element is Microsoft.Maui.Controls.NavigationPage navigation)
        {
            // A navigation page's visible content is the current page.
            ConnectTree(navigation.CurrentPage);
        }
        else if (element is IContentView contentView)
        {
            ConnectTree(contentView.PresentedContent);
        }
    }
}
