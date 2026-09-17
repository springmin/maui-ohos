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
                    Microsoft.OpenHarmony.Hosting.OpenHarmonyBridge.WriteStatus($"[maui] image file not found: {path}");
                }
            }
            catch (Exception ex)
            {
                Microsoft.OpenHarmony.Hosting.OpenHarmonyBridge.WriteStatus($"[maui] image load failed: {ex.Message}");
            }
        }
        else
        {
            // Stream/Uri sources need the platform image service (async); tracked as next step.
            Microsoft.OpenHarmony.Hosting.OpenHarmonyBridge.WriteStatus($"[maui] image source {source.GetType().Name} not supported yet");
        }
    }

    public static void MapAspect(OpenHarmonyImageHandler handler, IImage image)
        => handler.PlatformView.ImageAspect = image.Aspect;
}
