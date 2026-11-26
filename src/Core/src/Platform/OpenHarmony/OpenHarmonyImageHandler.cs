// Image handler for OpenHarmony: resolves a FileImageSource to bytes and draws it on the canvas.
// FontImageSource glyphs are drawn through the compositor's text path (the slice has no offscreen
// text rasterizer) and their resolution is cached per unique source.
using Microsoft.Maui;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Graphics;
using Microsoft.Maui.Handlers;

namespace Microsoft.Maui.Platform;

public sealed class OpenHarmonyImageHandler : OpenHarmonyViewHandler<IImage>
{
    public static readonly IPropertyMapper<IImage, OpenHarmonyImageHandler> Mapper =
        new PropertyMapper<IImage, OpenHarmonyImageHandler>(ViewMapper)
        {
            [nameof(IImageSourcePart.Source)] = MapSource,
            [nameof(IImage.Aspect)] = MapAspect,
        };

    public OpenHarmonyImageHandler() : base(Mapper) { }

    protected override OpenHarmonyView CreatePlatformView() => new();

    public override Size GetDesiredSize(double widthConstraint, double heightConstraint)
    {
        if ((VirtualView as IImageSourcePart)?.Source is FontImageSource fontSource && !fontSource.IsEmpty)
        {
            Size glyph = OpenHarmonyFontImageSource.Measure(fontSource);
            return new Size(Math.Min(glyph.Width, widthConstraint), Math.Min(glyph.Height, heightConstraint));
        }
        return new(Math.Min(200, widthConstraint), Math.Min(200, heightConstraint));
    }

    public static void MapSource(OpenHarmonyImageHandler handler, IImage image)
        => OpenHarmonyImageSourceRenderer.Apply(
            image as IImageSourcePart, handler.PlatformView, handler.MauiContext, handler.VirtualView as IView);

    public static void MapAspect(OpenHarmonyImageHandler handler, IImage image)
        => handler.PlatformView.ImageAspect = image.Aspect;
}

/// <summary>
/// Materialises the source of an image-like view onto its platform view. File/stream/URI sources
/// keep the existing byte-based behaviour; a <see cref="FontImageSource"/> is handed to the
/// compositor's text path through <see cref="OpenHarmonyFontImageSource"/>.
/// </summary>
internal static class OpenHarmonyImageSourceRenderer
{
    public static void Apply(IImageSourcePart? part, OpenHarmonyView view, IMauiContext? context, IView? virtualView)
    {
        // Both rendering paths are reset so a source change never leaves the previous one visible.
        view.ImageBytes = null;
        view.Text = null;
        if (part?.Source is not IImageSource source || source.IsEmpty)
        {
            return;
        }
        if (source is FileImageSource file)
        {
            try
            {
                string path = file.File ?? string.Empty;
                if (File.Exists(path))
                {
                    view.ImageBytes = File.ReadAllBytes(path);
                }
                else
                {
                    // The path is already query/fragment-free in practice; the B7 helper only
                    // caps it and drops anything after a '?'/'#' that slipped into the name.
                    Microsoft.OpenHarmony.Hosting.OpenHarmonyBridge.WriteStatus($"[maui] image file not found: {OpenHarmonyWebViewHandler.SanitizeUrlForLog(path)}");
                }
            }
            catch (Exception ex)
            {
                Microsoft.OpenHarmony.Hosting.OpenHarmonyBridge.WriteStatus($"[maui] image load failed: {ex.Message}");
            }
        }
        else if (source is StreamImageSource streamSource)
        {
            _ = LoadStreamAsync(view, virtualView, streamSource);
        }
        else if (source is UriImageSource uriSource)
        {
            _ = LoadUriAsync(view, virtualView, uriSource);
        }
        else if (source is FontImageSource fontSource)
        {
            OpenHarmonyFontImageSource.Apply(view, fontSource, context);
        }
        else
        {
            Microsoft.OpenHarmony.Hosting.OpenHarmonyBridge.WriteStatus($"[maui] image source {source.GetType().Name} not supported yet");
        }
    }

    /// <summary>Loads an ImageSource stream asynchronously and redraws when it arrives.</summary>
    private static async Task LoadStreamAsync(OpenHarmonyView view, IView? virtualView, StreamImageSource source)
    {
        try
        {
            using Stream stream = await source.Stream(CancellationToken.None);
            byte[] bytes = await ReadAllAsync(stream);
            Apply(view, virtualView, bytes);
        }
        catch (Exception ex)
        {
            Microsoft.OpenHarmony.Hosting.OpenHarmonyBridge.WriteStatus($"[maui] image stream failed: {ex.GetType().Name}");
        }
    }

    private static async Task LoadUriAsync(OpenHarmonyView view, IView? virtualView, UriImageSource source)
    {
        try
        {
            if (source.Uri is not { } uri)
            {
                return;
            }
            using var client = new HttpClient();
            byte[] bytes = await client.GetByteArrayAsync(uri);
            Apply(view, virtualView, bytes);
        }
        catch (Exception ex)
        {
            Microsoft.OpenHarmony.Hosting.OpenHarmonyBridge.WriteStatus($"[maui] image uri failed: {ex.GetType().Name}");
        }
    }

    private static async Task<byte[]> ReadAllAsync(Stream stream)
    {
        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer);
        return buffer.ToArray();
    }

    private static void Apply(OpenHarmonyView view, IView? virtualView, byte[] bytes)
    {
        void Set()
        {
            view.ImageBytes = bytes;
            Microsoft.OpenHarmony.Hosting.OpenHarmonyBridge.RequestRedraw();
        }
        var dispatcher = (virtualView as Microsoft.Maui.Controls.VisualElement)?.Dispatcher;
        if (dispatcher is not null && !dispatcher.IsDispatchRequired)
        {
            Set();
        }
        else if (dispatcher is not null)
        {
            dispatcher.Dispatch(Set);
        }
        else
        {
            Set();
        }
    }
}

/// <summary>
/// FontImageSource support for the slice. There is no offscreen text rasterizer on this platform,
/// so the glyph is not encoded into <see cref="OpenHarmonyView.ImageBytes"/>; it is drawn by the
/// compositor's canvas text path (<see cref="OpenHarmonyView.Draw"/> draws Text with the view's
/// font size/colour). Resolution (scaled size, colour, metrics, font family) is cached per unique
/// source so repeated maps and layout passes reuse it.
/// </summary>
internal static class OpenHarmonyFontImageSource
{
    private readonly record struct GlyphKey(
        string Glyph, string? Family, double Size, Color Color, double Scale, bool AutoScaling);

    internal sealed class Glyph
    {
        public string Text { get; init; } = string.Empty;
        public float FontSize { get; init; }
        public Color Color { get; init; } = Colors.White;
        public float Width { get; init; }
        public float Height { get; init; }
    }

    private static readonly Dictionary<GlyphKey, Glyph> s_cache = new();

    /// <summary>The cached render state of the glyph (one entry per unique source).</summary>
    public static Glyph Resolve(FontImageSource source)
    {
        string text = source.Glyph ?? string.Empty;
        Color color = source.Color ?? Colors.White;
        double scale = ScaleFor(source);
        var key = new GlyphKey(text, source.FontFamily, source.Size, color, scale, source.FontAutoScalingEnabled);
        lock (s_cache)
        {
            if (!s_cache.TryGetValue(key, out Glyph? glyph))
            {
                float fontSize = (float)(Math.Max(1, source.Size) * scale);
                (float width, float height) = OpenHarmonyLabelHandler.MeasureText(text, fontSize);
                glyph = new Glyph
                {
                    Text = text,
                    FontSize = fontSize,
                    Color = color,
                    Width = width,
                    Height = height,
                };
                s_cache[key] = glyph;
            }
            return glyph;
        }
    }

    /// <summary>The glyph's desired size (already scaled).</summary>
    public static Size Measure(FontImageSource source)
    {
        Glyph glyph = Resolve(source);
        return new Size(glyph.Width, glyph.Height);
    }

    /// <summary>Points the view at the glyph; the compositor draws it on the next frame.</summary>
    public static void Apply(OpenHarmonyView view, FontImageSource source, IMauiContext? context)
    {
        Glyph glyph = Resolve(source);
        ApplyFontFamily(context, source.FontFamily, glyph.FontSize);
        view.ImageBytes = null;
        view.Text = glyph.Text;
        view.FontSize = glyph.FontSize;
        view.TextColor = glyph.Color;
    }

    /// <summary>Auto-scaling follows the display density (like MAUI's font scaling).</summary>
    private static double ScaleFor(FontImageSource source)
    {
        if (!source.FontAutoScalingEnabled)
        {
            return 1;
        }
        try
        {
            double density = Microsoft.Maui.Devices.DeviceDisplay.Current.MainDisplayInfo.Density;
            return density > 0 ? density : 1;
        }
        catch
        {
            // Off-device (no display implementation): keep the logical size.
            return 1;
        }
    }

    private static void ApplyFontFamily(IMauiContext? context, string? family, double size)
    {
        if (string.IsNullOrEmpty(family))
        {
            return;
        }
        // The native text engine uses one selected typeface; the font manager maps a family to its
        // registered (or conventional Assets/Fonts) file, the same mechanism entries/labels use.
        if (context?.Services.GetService(typeof(IFontManager)) is OpenHarmonyFontManager fontManager)
        {
            fontManager.GetFont(Microsoft.Maui.Font.OfSize(family, size));
        }
    }
}
