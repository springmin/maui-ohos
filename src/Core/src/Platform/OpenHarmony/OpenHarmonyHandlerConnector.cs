// Shared handler wiring for the platform slice: MAUI's own platform-partial handlers have no
// platform view here, so the slice connects its own handlers (exact type -> interfaces ->
// base types) for an element tree.
using System.Diagnostics.CodeAnalysis;
using Microsoft.Maui.Hosting;

namespace Microsoft.Maui.Platform;

/// <summary>
/// One slice handler registration (FIX-INTEROP #5/#6). Keeping the handler Type inside this
/// type lets the trim/AOT analyzers follow the DynamicallyAccessedMembers contract through
/// MAUI's AddHandler and through the connector's factory; a Dictionary&lt;Type, Type&gt;
/// erased it and produced IL2072 (registration) and IL2067 (Activator.CreateInstance). The
/// factory also removes the per-connect Activator.CreateInstance call.
/// </summary>
internal sealed class SliceHandlerRegistration
{
    public SliceHandlerRegistration(
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] Type handlerType)
    {
        HandlerType = handlerType;
        Create = () => (IElementHandler)Activator.CreateInstance(handlerType)!;
    }

    [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)]
    public Type HandlerType { get; }

    /// <summary>Builds one handler instance without a reflection call at connect time.</summary>
    public Func<IElementHandler> Create { get; }
}

public static class OpenHarmonyHandlerConnector
{
    /// <summary>Maui context of the running application (set by the app host).</summary>
    public static IMauiContext? Context { get; set; }

    /// <summary>Resolves the slice handler for a view type (exact type, interfaces, base types).</summary>
    public static Type? FindSliceHandlerType(Type viewType) => FindSliceHandler(viewType)?.HandlerType;

    /// <summary>
    /// Resolves the slice handler registration for a view type (exact type, interfaces, base
    /// types). The interface walk is safe for trimming/AOT even though the analyzer cannot
    /// prove it: interface implementations are never removed from a type (metadata identity
    /// requires them), the slice handler interfaces are rooted as SliceHandlers keys in this
    /// assembly, and get_Interfaces is a pure metadata read on an instantiated type.
    /// Object.GetType() carries no DynamicallyAccessedMembers annotations (the static type
    /// IElement has none and rc.1 exposes no public seam to add them), so the single IL2070 is
    /// suppressed with that structural justification instead of loosening the analyzer.
    /// </summary>
    [UnconditionalSuppressMessage("Trimming", "IL2070",
        Justification = "Interfaces cannot be trimmed off a type; the slice handler interfaces are rooted as SliceHandlers keys in this assembly.")]
    internal static SliceHandlerRegistration? FindSliceHandler(Type viewType)
    {
        if (MauiOpenHarmonyExtensions.SliceHandlers.TryGetValue(viewType, out SliceHandlerRegistration? exact))
        {
            return exact;
        }
        foreach (Type iface in viewType.GetInterfaces())
        {
            if (MauiOpenHarmonyExtensions.SliceHandlers.TryGetValue(iface, out SliceHandlerRegistration? byInterface))
            {
                return byInterface;
            }
        }
        for (Type? type = viewType.BaseType; type is not null; type = type.BaseType)
        {
            if (MauiOpenHarmonyExtensions.SliceHandlers.TryGetValue(type, out SliceHandlerRegistration? byBase))
            {
                return byBase;
            }
        }
        return null;
    }

    /// <summary>Creates one handler instance by its registered type (kept for callers outside
    /// the connector; the connector itself uses the registration's factory).</summary>
    public static IElementHandler? HandlerFor(
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicParameterlessConstructor)] Type handlerType)
        => Activator.CreateInstance(handlerType) as IElementHandler;

    /// <summary>Connects the slice handler of a single element when it has none.</summary>
    public static void Connect(IElement? element)
    {
        if (element is null || element.Handler is not null)
        {
            return;
        }
        if (FindSliceHandler(element.GetType()) is { } registration && Context is not null)
        {
            IElementHandler handler = registration.Create();
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
