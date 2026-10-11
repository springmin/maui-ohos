// MediaElement handler for OpenHarmony: the MAUI handler half of the ArkWeb HTML5 media path
// (see OpenHarmonyMediaElement.cs for the surface and the full mapping table). The handler owns
// one OpenHarmonyMediaElementSurface, forwards the element's layout to the surface (the shell
// overlay frame), maps Source/Aspect/Volume/Muted and exposes the transport commands plus the
// surface itself, so a CommunityToolkit.Maui adapter (or any custom control) can reuse it.
//
// The handler is registered in MauiOpenHarmonyExtensions.SliceHandlers for
// IOpenHarmonyMediaElementView, and it participates in the visual-tree overlay lifetime watch
// (AUTODISCONNECT) and the LRU pool exactly like the WebView/HybridWebView/Blazor handlers.
using Microsoft.Maui.Graphics;
using Microsoft.OpenHarmony.Hosting;

namespace Microsoft.Maui.Platform;

public sealed class OpenHarmonyMediaElementHandler : OpenHarmonyViewHandler<IOpenHarmonyMediaElementView>, IOpenHarmonyOverlaySlotOwner, IOpenHarmonyOverlaySlotLifetime
{
    public static readonly IPropertyMapper<IOpenHarmonyMediaElementView, OpenHarmonyMediaElementHandler> Mapper =
        new PropertyMapper<IOpenHarmonyMediaElementView, OpenHarmonyMediaElementHandler>(ViewMapper)
        {
            [nameof(IOpenHarmonyMediaElementView.Source)] = MapSource,
            [nameof(IOpenHarmonyMediaElementView.Aspect)] = MapAspect,
            [nameof(IOpenHarmonyMediaElementView.Volume)] = MapVolume,
            [nameof(IOpenHarmonyMediaElementView.Muted)] = MapMuted,
        };

    public OpenHarmonyMediaElementHandler() : base(Mapper)
    {
        Surface = new OpenHarmonyMediaElementSurface(() => OpenHarmonyMauiAppHost.ResolveWindowId(VirtualView as IView));
    }

    /// <summary>The playback surface of this handler (commands and events).</summary>
    public OpenHarmonyMediaElementSurface Surface { get; }

    /// <summary>Starts playback (see <see cref="OpenHarmonyMediaElementSurface.PlayAsync"/>).</summary>
    public Task<OpenHarmonyMediaResult> PlayAsync() => Surface.PlayAsync();

    /// <summary>Pauses playback.</summary>
    public Task<OpenHarmonyMediaResult> PauseAsync() => Surface.PauseAsync();

    /// <summary>Stops playback and drops the current source.</summary>
    public Task<OpenHarmonyMediaResult> StopAsync() => Surface.StopAsync();

    /// <summary>Seeks to <paramref name="position"/>.</summary>
    public Task<OpenHarmonyMediaResult> SeekAsync(TimeSpan position) => Surface.SeekAsync(position);

    /// <summary>Sets the volume in [0, 1].</summary>
    public Task<OpenHarmonyMediaResult> SetVolumeAsync(double volume) => Surface.SetVolumeAsync(volume);

    /// <summary>Sets the muted flag.</summary>
    public Task<OpenHarmonyMediaResult> SetMutedAsync(bool muted) => Surface.SetMutedAsync(muted);

    protected override OpenHarmonyView CreatePlatformView() => new() { Background = Colors.Black };

    protected override void ConnectHandler(OpenHarmonyView platformView)
    {
        base.ConnectHandler(platformView);
        Surface.Attach();
    }

    protected override void DisconnectHandler(OpenHarmonyView platformView)
    {
        Surface.Detach();
        base.DisconnectHandler(platformView);
    }

    public override Size GetDesiredSize(double widthConstraint, double heightConstraint)
        => new(Math.Min(widthConstraint, widthConstraint), Math.Min(400, heightConstraint));

    public override void PlatformArrange(Rect frame)
    {
        base.PlatformArrange(frame);
        Surface.Arrange(frame);
    }

    /// <summary>Source mapper: normalizes and registers the media source with the shell.</summary>
    public static void MapSource(OpenHarmonyMediaElementHandler handler, IOpenHarmonyMediaElementView view)
        => handler.Surface.SetSource(view.Source);

    /// <summary>Aspect mapper: applies fit/fill to the page's video element.</summary>
    public static void MapAspect(OpenHarmonyMediaElementHandler handler, IOpenHarmonyMediaElementView view)
        => handler.Surface.SetAspect(view.Aspect);

    /// <summary>Volume mapper (fire-and-forget; the command surface reports failures).</summary>
    public static void MapVolume(OpenHarmonyMediaElementHandler handler, IOpenHarmonyMediaElementView view)
        => _ = handler.Surface.SetVolumeAsync(view.Volume);

    /// <summary>Muted mapper (fire-and-forget; the command surface reports failures).</summary>
    public static void MapMuted(OpenHarmonyMediaElementHandler handler, IOpenHarmonyMediaElementView view)
        => _ = handler.Surface.SetMutedAsync(view.Muted);

    /// <summary>LRU pool forwarding: the surface owns the claim.</summary>
    void IOpenHarmonyOverlaySlotOwner.OnOverlaySlotPreempted(int slot) => Surface.OnOverlaySlotPreempted(slot);

    /// <summary>LRU pool forwarding: the surface owns the claim.</summary>
    bool IOpenHarmonyOverlaySlotOwner.IsOverlaySlotEngaged => Surface.IsOverlaySlotEngaged;

    /// <summary>AUTODISCONNECT forwarding: the surface hides and releases the slot.</summary>
    void IOpenHarmonyOverlaySlotLifetime.OnOverlaySlotDetached() => Surface.DetachOverlay();

    /// <summary>AUTODISCONNECT forwarding: the surface re-claims and replays.</summary>
    void IOpenHarmonyOverlaySlotLifetime.OnOverlaySlotAttached() => Surface.AttachOverlay();
}
