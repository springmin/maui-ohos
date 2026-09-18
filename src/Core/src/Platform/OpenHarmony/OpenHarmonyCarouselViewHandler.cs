// CarouselView handler for OpenHarmony: shows one item and changes Position on a horizontal
// swipe (no page animation yet).
using Microsoft.Maui.Controls;
using Microsoft.Maui.Graphics;
using Microsoft.Maui.Handlers;
using Microsoft.OpenHarmony.Hosting;

namespace Microsoft.Maui.Platform;

public sealed class OpenHarmonyCarouselViewHandler : OpenHarmonyViewHandler<CarouselView>
{
    private IView? _current;

    public static readonly IPropertyMapper<CarouselView, OpenHarmonyCarouselViewHandler> Mapper =
        new PropertyMapper<CarouselView, OpenHarmonyCarouselViewHandler>(ViewMapper)
        {
            [nameof(ItemsView.ItemsSource)] = MapItems,
            [nameof(ItemsView.ItemTemplate)] = MapItems,
            [nameof(CarouselView.Position)] = MapItems,
        };

    public OpenHarmonyCarouselViewHandler() : base(Mapper) { }

    protected override OpenHarmonyView CreatePlatformView()
    {
        var view = new OpenHarmonyView { Background = Colors.Black };
        // Horizontal swipes change the position: re-use the pan recognizer plumbing by
        // handling the swipe in the platform view's touch.
        view.Swipe = (deltaX, _) =>
        {
            if (Math.Abs(deltaX) < 50)
            {
                return;
            }
            if (VirtualView is { } carousel)
            {
                int count = Count(carousel);
                int position = carousel.Position + (deltaX < 0 ? 1 : -1);
                // Loop around the ends instead of clamping.
                position = count > 0 ? (position + count) % count : 0;
                carousel.Position = position;
                Rebuild();
                OpenHarmonyBridge.RequestRedraw();
            }
        };
        return view;
    }

    public override Size GetDesiredSize(double widthConstraint, double heightConstraint)
        => new(Math.Min(widthConstraint, widthConstraint), ResolveHeight(heightConstraint));

    public override void PlatformArrange(Rect frame)
    {
        base.PlatformArrange(frame);
        Rebuild();
    }

    private void Rebuild()
    {
        PlatformView.ViewChildren.Clear();
        if (VirtualView is not { } carousel || Count(carousel) == 0)
        {
            _current = null;
            return;
        }
        int position = Math.Clamp(carousel.Position, 0, Count(carousel) - 1);
        if (carousel.ItemsSource is not { } source)
        {
            _current = null;
            return;
        }
        object? item = source.Cast<object?>().ElementAtOrDefault(position);
        View? view = null;
        if (carousel.ItemTemplate?.CreateContent() is View templated)
        {
            view = templated;
        }
        else
        {
            view = new Label { Text = item?.ToString() ?? string.Empty, FontSize = 30, TextColor = Colors.White };
        }
        view.BindingContext = item;
        OpenHarmonyHandlerConnector.ConnectTree(view);
        Rect frame = PlatformView.Frame;
        view.Measure(frame.Width, frame.Height);
        view.Arrange(new Rect(frame.X, frame.Y, frame.Width, frame.Height));
        PlatformView.ViewChildren.Add(view);
        _current = view;
    }

    /// <summary>Infinite constraints arrive from stack layouts; fall back to HeightRequest.</summary>
    private double ResolveHeight(double heightConstraint)
    {
        if (VirtualView?.HeightRequest > 0)
        {
            return VirtualView.HeightRequest;
        }
        return double.IsFinite(heightConstraint) ? heightConstraint : 200;
    }

    private static int Count(CarouselView carousel)
        => carousel.ItemsSource?.Cast<object?>().Count() ?? 0;

    public static void MapItems(OpenHarmonyCarouselViewHandler handler, CarouselView carousel)
        => handler.Rebuild();
}
