// ImageButton handler for OpenHarmony: draws the image/glyph like the image handler, adds the
// button chrome (corner radius, border) and routes taps plus pressed/released state.
using Microsoft.Maui;
using Microsoft.Maui.Graphics;
using Microsoft.Maui.Handlers;
using MauiCanvas = Microsoft.OpenHarmony.Maui.Graphics.OpenHarmonyCanvas;

namespace Microsoft.Maui.Platform;

public sealed class OpenHarmonyImageButtonHandler : OpenHarmonyViewHandler<IImageButton>, IImageButtonHandler
{
    public static readonly IPropertyMapper<IImageButton, OpenHarmonyImageButtonHandler> Mapper =
        new PropertyMapper<IImageButton, OpenHarmonyImageButtonHandler>(ViewMapper)
        {
            [nameof(IImageSourcePart.Source)] = MapSource,
            [nameof(IImage.Aspect)] = MapAspect,
            [nameof(Microsoft.Maui.Controls.ImageButton.CornerRadius)] = MapCornerRadius,
            [nameof(Microsoft.Maui.Controls.ImageButton.BorderColor)] = MapBorderColor,
            [nameof(Microsoft.Maui.Controls.ImageButton.BorderWidth)] = MapBorderWidth,
            [nameof(IView.Background)] = MapBackground,
        };

    private readonly ImageSourcePartLoader _sourceLoader;

    public OpenHarmonyImageButtonHandler() : base(Mapper)
    {
        _sourceLoader = new ImageSourcePartLoader(new SourcePartSetter(this));
    }

    protected override OpenHarmonyView CreatePlatformView()
    {
        var view = new PressTrackingView();
        view.PressedChanged = pressed =>
        {
            if (VirtualView is not { } imageButton)
            {
                return;
            }
            // Mirror the platform press onto MAUI so IsPressed and the visual states follow.
            if (pressed)
            {
                imageButton.Pressed();
            }
            else
            {
                imageButton.Released();
            }
        };
        view.Tap = () => VirtualView?.Clicked();
        return view;
    }

    public override Size GetDesiredSize(double widthConstraint, double heightConstraint)
    {
        if ((VirtualView as IImageSourcePart)?.Source is FontImageSource fontSource && !fontSource.IsEmpty)
        {
            Size glyph = OpenHarmonyFontImageSource.Measure(fontSource);
            return new Size(Math.Min(glyph.Width, widthConstraint), Math.Min(glyph.Height, heightConstraint));
        }
        return new(Math.Min(200, widthConstraint), Math.Min(200, heightConstraint));
    }

    public static void MapSource(OpenHarmonyImageButtonHandler handler, IImageButton imageButton)
        => OpenHarmonyImageSourceRenderer.Apply(
            imageButton as IImageSourcePart, handler.PlatformView, handler.MauiContext, handler.VirtualView as IView);

    public static void MapAspect(OpenHarmonyImageButtonHandler handler, IImageButton imageButton)
        => handler.PlatformView.ImageAspect = imageButton.Aspect;

    public static void MapCornerRadius(OpenHarmonyImageButtonHandler handler, IImageButton imageButton)
        => handler.PlatformView.CornerRadius = (float)imageButton.CornerRadius;

    public static void MapBackground(OpenHarmonyImageButtonHandler handler, IImageButton imageButton)
        => handler.PlatformView.Background = imageButton.Background is SolidPaint { Color: { } color } ? color : null;

    public static void MapBorderColor(OpenHarmonyImageButtonHandler handler, IImageButton imageButton)
        => handler.PlatformView.StrokeColor = (imageButton as Microsoft.Maui.Controls.ImageButton)?.BorderColor
            ?? (imageButton as IButtonStroke)?.StrokeColor;

    public static void MapBorderWidth(OpenHarmonyImageButtonHandler handler, IImageButton imageButton)
    {
        double width = (imageButton as Microsoft.Maui.Controls.ImageButton)?.BorderWidth
            ?? (imageButton as IButtonStroke)?.StrokeThickness
            ?? 0;
        handler.PlatformView.StrokeThickness = (float)width;
    }

    // IImageButtonHandler/IImageHandler surface: the slice renders through its own view and does
    // not use MAUI's image source services, so the loader is only kept for interface completeness.
    ImageSourcePartLoader IImageHandler.SourceLoader => _sourceLoader;

    IImage IImageHandler.VirtualView => VirtualView;

    object IImageHandler.PlatformView => PlatformView;

    object IImageButtonHandler.PlatformView => PlatformView;

    private sealed class SourcePartSetter : IImageSourcePartSetter
    {
        private readonly OpenHarmonyImageButtonHandler _handler;

        public SourcePartSetter(OpenHarmonyImageButtonHandler handler) => _handler = handler;

        public IElementHandler Handler => _handler;

        public IImageSourcePart ImageSourcePart => _handler.VirtualView;

        public void SetImageSource(object? imageSource)
        {
            // Sources are materialised by OpenHarmonyImageSourceRenderer; the service-produced
            // image the loader would hand over is not consumed by this handler.
        }
    }

    /// <summary>
    /// Platform view that reports its pressed transitions. The compositor sets
    /// <see cref="OpenHarmonyView.Pressed"/> on touch down/up (there is no separate press
    /// callback), so the transition is observed on the way into a frame.
    /// </summary>
    private sealed class PressTrackingView : OpenHarmonyView
    {
        private bool _lastPressed;

        public Action<bool>? PressedChanged { get; set; }

        public override void Draw(MauiCanvas canvas)
        {
            if (Pressed != _lastPressed)
            {
                _lastPressed = Pressed;
                PressedChanged?.Invoke(Pressed);
            }
            base.Draw(canvas);
        }
    }
}
