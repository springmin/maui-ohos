// Application-facing group templates for the OpenHarmony CarouselView.
//
// CarouselView derives from ItemsView and has no grouping API in this MAUI version (no
// IsGrouped, no GroupHeaderTemplate/GroupFooterTemplate - those live on GroupableItemsView, which
// only CollectionView derives from). When ItemsSource is grouped (every element is a non-string
// collection, the shape CollectionView groups use) the slice shows each group's items as slides.
// These attached properties give the app the missing half: with a header/footer template set,
// the slide stream gains a slide for every group's header and footer, realised from the template
// and bound to the group object, so the grouping the data already carries is expressible instead
// of being flattened away.
//
// The handler reads the templates back through the getters; setting/clearing one re-materialises
// the slides and requests a redraw. A template that materialises content which is not a View is
// reported once and the group falls back to the item template (or its text).
using Microsoft.Maui.Controls;

namespace Microsoft.Maui.Platform;

public static class OpenHarmonyCarouselView
{
    /// <summary>Attached template for a group header slide (null = no header slides).</summary>
    public static readonly BindableProperty GroupHeaderTemplateProperty = BindableProperty.CreateAttached(
        "GroupHeaderTemplate",
        typeof(DataTemplate),
        typeof(OpenHarmonyCarouselView),
        null,
        propertyChanged: OnGroupTemplateChanged);

    /// <summary>Attached template for a group footer slide (null = no footer slides).</summary>
    public static readonly BindableProperty GroupFooterTemplateProperty = BindableProperty.CreateAttached(
        "GroupFooterTemplate",
        typeof(DataTemplate),
        typeof(OpenHarmonyCarouselView),
        null,
        propertyChanged: OnGroupTemplateChanged);

    /// <summary>The template that draws a group header slide, or null when none is set.</summary>
    public static DataTemplate? GetGroupHeaderTemplate(BindableObject target)
        => (DataTemplate?)target.GetValue(GroupHeaderTemplateProperty);

    /// <summary>Sets the group header slide template; null removes the header slides.</summary>
    public static void SetGroupHeaderTemplate(BindableObject target, DataTemplate? value)
        => target.SetValue(GroupHeaderTemplateProperty, value);

    /// <summary>The template that draws a group footer slide, or null when none is set.</summary>
    public static DataTemplate? GetGroupFooterTemplate(BindableObject target)
        => (DataTemplate?)target.GetValue(GroupFooterTemplateProperty);

    /// <summary>Sets the group footer slide template; null removes the footer slides.</summary>
    public static void SetGroupFooterTemplate(BindableObject target, DataTemplate? value)
        => target.SetValue(GroupFooterTemplateProperty, value);

    /// <summary>A template change re-materialises the slides of a connected carousel.</summary>
    private static void OnGroupTemplateChanged(BindableObject bindable, object oldValue, object newValue)
    {
        if (bindable is CarouselView carousel && carousel.Handler is OpenHarmonyCarouselViewHandler handler)
        {
            handler.RebuildFromGroupTemplates();
        }
    }
}
