// Image handler for OpenHarmony: resolves a FileImageSource to bytes and draws it on the canvas.
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
        => new(Math.Min(200, widthConstraint), Math.Min(200, heightConstraint));

    public static void MapSource(OpenHarmonyImageHandler handler, IImage image)
    {
        handler.PlatformView.ImageBytes = null;
        if ((image as IImageSourcePart)?.Source is not IImageSource source || source.IsEmpty)
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
                    handler.PlatformView.ImageBytes = File.ReadAllBytes(path);
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
            _ = LoadStreamAsync(handler, streamSource);
        }
        else if (source is UriImageSource uriSource)
        {
            _ = LoadUriAsync(handler, uriSource);
        }
        else
        {
            Microsoft.OpenHarmony.Hosting.OpenHarmonyBridge.WriteStatus($"[maui] image source {source.GetType().Name} not supported yet");
        }
    }

    public static void MapAspect(OpenHarmonyImageHandler handler, IImage image)
        => handler.PlatformView.ImageAspect = image.Aspect;

    /// <summary>Loads an ImageSource stream asynchronously and redraws when it arrives.</summary>
    private static async Task LoadStreamAsync(OpenHarmonyImageHandler handler, StreamImageSource source)
    {
        try
        {
            using Stream stream = await source.Stream(CancellationToken.None);
            byte[] bytes = await ReadAllAsync(stream);
            Apply(handler, bytes);
        }
        catch (Exception ex)
        {
            Microsoft.OpenHarmony.Hosting.OpenHarmonyBridge.WriteStatus($"[maui] image stream failed: {ex.GetType().Name}");
        }
    }

    private static async Task LoadUriAsync(OpenHarmonyImageHandler handler, UriImageSource source)
    {
        try
        {
            if (source.Uri is not { } uri)
            {
                return;
            }
            using var client = new HttpClient();
            byte[] bytes = await client.GetByteArrayAsync(uri);
            Apply(handler, bytes);
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

    private static void Apply(OpenHarmonyImageHandler handler, byte[] bytes)
    {
        void Set()
        {
            handler.PlatformView.ImageBytes = bytes;
            Microsoft.OpenHarmony.Hosting.OpenHarmonyBridge.RequestRedraw();
        }
        var dispatcher = (handler.VirtualView as Microsoft.Maui.Controls.VisualElement)?.Dispatcher;
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
