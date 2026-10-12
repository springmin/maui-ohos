// MediaElement playback for OpenHarmony: the ArkWeb HTML5 path around the missing Media Kit
// (MEDIAELEMENT-ARKWEB A-M1; see docs/plans/2026-10-10-ohos-mediaelement-arkweb*.md).
//
// The platform has no usable Media Kit in the target image (T20's AVPlayer bridge answers
// Unavailable, 2026-10-10), but ArkWeb plays H.264/AAC through the Chromium media stack
// (MEDIA-Q1 device probe, Q1 green). This surface is the managed half of that fallback: one
// media element owns one shell overlay slot, the shell serves the HTML5 media page on that slot
// and the registered rawfile/file source under the page origin, and the page's <video> element
// is driven through the existing eval channel. Every page event travels back through the shell's
// trusted dotnetHost envelope ("__OHORIGIN|<url>|<document id>\n__OHMEDIA|s<slot>|<kind>|...").
//
// Mapping table:
//   Source        URL -> the page's video.src; rawfile/file -> MEDIA_ORIGIN + "__src__" served
//                  by the shell (the e2e source normalization lives in NormalizeSource).
//   Play/Pause/Stop/Seek/Volume/Muted -> evals of window.__ohMedia.<op>; Stop pauses, resets to
//                  0 and drops the source. Aspect (fit/fill) -> object-fit.
//   State/Position/Duration/Size and the events MediaOpened/Ended/Failed/PositionChanged breathe
//                  through the page's own events; the state enum is the T20 AVPlayer-shaped
//                  OpenHarmonyMediaPlaybackState so both media bridges speak one type.
//
// Degradation: no host library (off-device), no slot (the element's window pool is full) and a
// shell without the media origin all answer Unavailable/Failed without throwing; the surface must
// be explicitly attached, so the off-device suite can drive it with a stub slot owner.
//
// MULTIWINDOW (B-M1): the window-aware transport reaches secondary windows too. A media element
// in a subwindow claims that window's child ArkWeb slot (OpenHarmonyChildWeb), the shell's child
// page serves the shared media origin, and the page's events travel back through the child
// dotnetHost proxy carrying the media document id - the same trusted route as the primary
// window's (see OpenHarmonyMediaElementSurface.ReleaseWindowOverlays and the child "media" op in
// pages/SubWindow.ets).
//
// AOT: Utf8 LibraryImport only through the shared eval/command transports; JSON goes through
// the source-generated OpenHarmonySliceJsonContext, and the page payload parser uses JsonDocument
// (no reflection).
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Maui.Graphics;
using Microsoft.OpenHarmony.Hosting;

namespace Microsoft.Maui.Platform;

/// <summary>Video sizing the media page applies (object-fit: contain/cover).</summary>
public enum OpenHarmonyMediaAspect
{
    /// <summary>The whole frame is visible inside the element (object-fit: contain).</summary>
    AspectFit = 0,

    /// <summary>The frame fills the element, cropping the overflow (object-fit: cover).</summary>
    AspectFill = 1,
}

/// <summary>
/// The slice-local media element contract (the CommunityToolkit.Maui MediaElement adapter is
/// the intended consumer; the interface is slice-local because the toolkit package is not a
/// dependency of this assembly). A control implements it and the slice registers
/// <see cref="OpenHarmonyMediaElementHandler"/> for it, or a custom handler drives an
/// <see cref="OpenHarmonyMediaElementSurface"/> directly.
/// </summary>
public interface IOpenHarmonyMediaElementView : IView
{
    /// <summary>The media source (URL, HAP rawfile name or sandbox file path).</summary>
    OpenHarmonyMediaSource Source { get; }

    /// <summary>How the video frame is fitted into the element.</summary>
    OpenHarmonyMediaAspect Aspect { get; }

    /// <summary>Playback volume in [0, 1].</summary>
    double Volume { get; }

    /// <summary>True while the element plays muted.</summary>
    bool Muted { get; }
}

/// <summary>Payload of <see cref="OpenHarmonyMediaElementSurface.MediaOpened"/>.</summary>
public sealed class OpenHarmonyMediaOpenedEventArgs : EventArgs
{
    internal OpenHarmonyMediaOpenedEventArgs(TimeSpan duration, int width, int height)
    {
        Duration = duration;
        Width = width;
        Height = height;
    }

    /// <summary>The stream duration the page reported (zero when unknown).</summary>
    public TimeSpan Duration { get; }

    /// <summary>The decoded video width in pixels (zero for audio-only).</summary>
    public int Width { get; }

    /// <summary>The decoded video height in pixels (zero for audio-only).</summary>
    public int Height { get; }
}

/// <summary>Payload of <see cref="OpenHarmonyMediaElementSurface.Ended"/>.</summary>
public sealed class OpenHarmonyMediaEndedEventArgs : EventArgs
{
    internal OpenHarmonyMediaEndedEventArgs(TimeSpan position, TimeSpan duration)
    {
        Position = position;
        Duration = duration;
    }

    /// <summary>The position the playback ended at (normally the duration).</summary>
    public TimeSpan Position { get; }

    /// <summary>The stream duration at the end of playback.</summary>
    public TimeSpan Duration { get; }
}

/// <summary>Payload of <see cref="OpenHarmonyMediaElementSurface.SizeChanged"/>.</summary>
public sealed class OpenHarmonyMediaSizeChangedEventArgs : EventArgs
{
    internal OpenHarmonyMediaSizeChangedEventArgs(int width, int height)
    {
        Width = width;
        Height = height;
    }

    /// <summary>The decoded video width in pixels.</summary>
    public int Width { get; }

    /// <summary>The decoded video height in pixels.</summary>
    public int Height { get; }
}

/// <summary>
/// One media element on one ArkWeb overlay slot. Public so a toolkit adapter (or any custom
/// handler) can own it; <see cref="OpenHarmonyMediaElementHandler"/> is the ready-made handler
/// over <see cref="IOpenHarmonyMediaElementView"/>. Never throws: a missing host library, slot
/// or page answers Unavailable/Failed instead.
/// </summary>
public sealed class OpenHarmonyMediaElementSurface : IOpenHarmonyOverlaySlotOwner
{
    /// <summary>Origin the shell serves the media page and the registered source from.</summary>
    internal const string MediaOrigin = "https://media.local/";

    /// <summary>The source URL that makes the shell serve the registered rawfile/file bytes.</summary>
    internal const string MediaSourcePath = "__src__";

    /// <summary>Prefix of the page's dotnetHost payloads ("__OHMEDIA|s&lt;slot&gt;|...").</summary>
    internal const string EventPrefix = "__OHMEDIA|";

    /// <summary>The shell loads the page with the slot as its query parameter.</summary>
    internal const string SlotQueryName = "s";

    private static readonly object s_sync = new();
    private static readonly List<OpenHarmonyMediaElementSurface> s_surfaces = new();

    private readonly Func<string>? _windowIdResolver;
    private readonly string _pageId = Guid.NewGuid().ToString("N");
    private readonly object _stateSync = new();

    private int _slot = -1;
    private string _windowId = OpenHarmonyWindowSurface.PrimaryWindowId;
    private bool _windowClosed;
    private bool _preempted;
    private bool _detached;
    private bool _engaged;
    private bool _attached;
    private bool _pageReady;
    private bool _playRequested;
    private string? _registeredKey;
    private OpenHarmonyMediaSourceKind _sourceKind = OpenHarmonyMediaSourceKind.Url;
    private string _sourceLocation = string.Empty;
    private bool _hasSource;
    private volatile int _state;
    private long _positionMs;
    private long _durationMs;
    private int _videoWidth;
    private int _videoHeight;
    private OpenHarmonyMediaAspect _aspect = OpenHarmonyMediaAspect.AspectFit;
    private double _volume = 1;
    private bool _muted;

    /// <summary>
    /// Creates a surface. <paramref name="windowIdResolver"/> answers the managed window the
    /// owning control belongs to (the handler passes <c>OpenHarmonyMauiAppHost.ResolveWindowId</c>);
    /// absent/null means the primary window.
    /// </summary>
    public OpenHarmonyMediaElementSurface(Func<string>? windowIdResolver = null)
    {
        _windowIdResolver = windowIdResolver;
        lock (s_sync)
        {
            s_surfaces.Add(this);
        }
    }

    /// <summary>The slot this element currently owns; -1 while suspended or unsupported.</summary>
    internal int OverlaySlot => _slot;

    /// <summary>The per-registration document id the shell stamps into this element's page.</summary>
    internal string PageDocumentId => _pageId;

    /// <summary>True while the shell can be driven (attached, a slot is held, not failed).</summary>
    public bool IsSupported => _attached && !_windowClosed && _slot >= 0;

    /// <summary>The last playback state the page reported (Unknown until the first event).</summary>
    public OpenHarmonyMediaPlaybackState State => (OpenHarmonyMediaPlaybackState)_state;

    /// <summary>The last playback position the page reported.</summary>
    public TimeSpan Position => TimeSpan.FromMilliseconds(Interlocked.Read(ref _positionMs));

    /// <summary>The last stream duration the page reported.</summary>
    public TimeSpan Duration => TimeSpan.FromMilliseconds(Interlocked.Read(ref _durationMs));

    /// <summary>The decoded video width in pixels (zero for audio-only/until opened).</summary>
    public int VideoWidth => _videoWidth;

    /// <summary>The decoded video height in pixels (zero for audio-only/until opened).</summary>
    public int VideoHeight => _videoHeight;

    /// <summary>Raised when the page reported the stream metadata (duration/size).</summary>
    public event EventHandler<OpenHarmonyMediaOpenedEventArgs>? MediaOpened;

    /// <summary>Raised on every state transition the page reported.</summary>
    public event EventHandler<OpenHarmonyMediaStateChangedEventArgs>? StateChanged;

    /// <summary>Raised for every playback-position tick (and after a seek).</summary>
    public event EventHandler<OpenHarmonyMediaPositionChangedEventArgs>? PositionChanged;

    /// <summary>Raised when the page reported the stream duration.</summary>
    public event EventHandler<OpenHarmonyMediaDurationChangedEventArgs>? DurationChanged;

    /// <summary>Raised when the page reported a media error or a rejected play().</summary>
    public event EventHandler<OpenHarmonyMediaFailedEventArgs>? Failed;

    /// <summary>Raised when the decoded video size became known/changed.</summary>
    public event EventHandler<OpenHarmonyMediaSizeChangedEventArgs>? SizeChanged;

    /// <summary>Raised when playback reached the end of the stream.</summary>
    public event EventHandler<OpenHarmonyMediaEndedEventArgs>? Ended;

    /// <summary>
    /// Claims the overlay slot and arms the shell bridge. Called by the handler when the control
    /// connects; idempotent. Without a slot (a full window pool) the element stays Failed.
    /// </summary>
    public void Attach()
    {
        _attached = true;
        _windowClosed = false;
        EnsureSlot();
        if (_slot >= 0 && _hasSource)
        {
            Register();
        }
    }

    /// <summary>
    /// Releases the overlay slot (hide + pool release) and stops driving the page. Called by the
    /// handler when the control disconnects; idempotent.
    /// </summary>
    public void Detach()
    {
        _attached = false;
        DetachOverlay();
    }

    /// <summary>
    /// AUTODISCONNECT: the control left the visual tree while its handler stayed connected. The
    /// slot is hidden and released; a later <see cref="AttachOverlay"/> re-claims and replays.
    /// </summary>
    public void DetachOverlay()
    {
        ResetPage();
        if (_slot < 0)
        {
            return;
        }
        int slot = _slot;
        _slot = -1;
        _engaged = false;
        _detached = true;
        OpenHarmonyChildWeb.CommandForWindow(_windowId, "hide", OpenHarmonyOverlays.Tag(slot));
        OpenHarmonyChildWeb.ReleaseForWindow(_windowId, slot, this);
        OpenHarmonyBridge.WriteStatus($"[maui] media overlay detached: slot {slot}");
    }

    /// <summary>
    /// AUTODISCONNECT: the control re-entered the visual tree. A detached/preempted element
    /// re-claims a slot and replays its registration; a live claim is only touched (LRU).
    /// </summary>
    public void AttachOverlay()
    {
        if (_slot >= 0)
        {
            _detached = false;
            OpenHarmonyChildWeb.TouchForWindow(_windowId, _slot, this);
            return;
        }
        if (!_detached && !_preempted)
        {
            return;
        }
        if (EnsureSlot() && _hasSource)
        {
            Register();
        }
    }

    /// <summary>
    /// Places the element's overlay on its arranged frame (MAUI DIP applied as ArkUI vp). A
    /// suspended element without a slot ignores the frame; its next use restores it.
    /// </summary>
    public void Arrange(Rect frame)
    {
        RefreshWindow();
        if (_slot < 0 && (_detached || _preempted) && EnsureSlot())
        {
            if (_hasSource)
            {
                Register();
            }
        }
        if (_slot >= 0)
        {
            _engaged = true;
            OpenHarmonyWebViewHandler.SendPlatformFrame(_windowId, frame, _slot);
        }
    }

    /// <summary>
    /// Sets the media source. The source is normalized (see <see cref="NormalizeSource"/>), the
    /// shell re-loads the media page on this element's slot and the page loads the effective URL
    /// once it is ready; an invalid source fails the call and the element stays without media.
    /// </summary>
    public void SetSource(OpenHarmonyMediaSource source)
    {
        if (!NormalizeSource(source, out OpenHarmonyMediaSourceKind kind, out string location, out string reason))
        {
            _hasSource = false;
            OpenHarmonyBridge.WriteStatus($"[maui] media source rejected: {reason}");
            Fail(0, reason);
            return;
        }
        _sourceKind = kind;
        _sourceLocation = location;
        _hasSource = true;
        OpenHarmonyBridge.WriteStatus($"[maui] media source: kind={(int)kind} location={LocationForLog(kind, location)}");
        EnsureSlot();
        if (_slot < 0)
        {
            Fail(0, "no overlay slot is available for this media element");
            return;
        }
        Register();
    }

    /// <summary>Applies fit/fill to the page's video element (best effort; never throws).</summary>
    public void SetAspect(OpenHarmonyMediaAspect aspect)
    {
        _aspect = aspect;
        _ = RunCommandAsync($"window.__ohMedia.setAspect(\"{(aspect == OpenHarmonyMediaAspect.AspectFill ? "fill" : "fit")}\")");
    }

    /// <summary>
    /// Starts playback. The page must have a loaded source (see SetSource). A cold start races
    /// the page: SetSource registers it and the page publishes window.__ohMedia a moment later,
    /// so a play issued into that window used to answer Unavailable and was replayed by
    /// HandleReady. The bounded wait keeps the first answer the page's own; a bridge that never
    /// delivers the page still answers Unavailable (the off-device contract is unchanged).
    /// </summary>
    public async Task<OpenHarmonyMediaResult> PlayAsync()
    {
        _playRequested = true;
        await WaitForPageReadyAsync().ConfigureAwait(false);
        return await RunCommandAsync("window.__ohMedia.play()").ConfigureAwait(false);
    }

    /// <summary>Pauses playback; the current position is kept.</summary>
    public Task<OpenHarmonyMediaResult> PauseAsync()
    {
        _playRequested = false;
        return RunCommandAsync("window.__ohMedia.pause()");
    }

    /// <summary>Stops playback: pauses, resets the position to zero and drops the source.</summary>
    public Task<OpenHarmonyMediaResult> StopAsync()
    {
        _playRequested = false;
        _hasSource = false;
        return RunCommandAsync("window.__ohMedia.stop()");
    }

    /// <summary>Seeks to <paramref name="position"/> (the page clamps to the stream bounds).</summary>
    public Task<OpenHarmonyMediaResult> SeekAsync(TimeSpan position)
    {
        if (position < TimeSpan.Zero)
        {
            return Task.FromResult(FailedResult("seek: the position must not be negative"));
        }
        return RunCommandAsync(
            "window.__ohMedia.seek(" + ((long)position.TotalMilliseconds / 1000.0).ToString("0.###", CultureInfo.InvariantCulture) + ")");
    }

    /// <summary>Sets the playback volume (clamped to [0, 1] by the page).</summary>
    public Task<OpenHarmonyMediaResult> SetVolumeAsync(double volume)
    {
        _volume = Math.Clamp(volume, 0, 1);
        return RunCommandAsync(
            "window.__ohMedia.setVolume(" + _volume.ToString("0.###", CultureInfo.InvariantCulture) + ")");
    }

    /// <summary>Sets the muted flag.</summary>
    public Task<OpenHarmonyMediaResult> SetMutedAsync(bool muted)
    {
        _muted = muted;
        return RunCommandAsync("window.__ohMedia.setMuted(" + (muted ? "true" : "false") + ")");
    }

    /// <summary>
    /// Normalizes one source into the wire kind/location pair the shell serves: web URLs and
    /// data/blob/about stay URL sources; "file:///x" and absolute paths become sandbox files;
    /// everything else must be a safe rawfile name. False (with a reason) for empty locations,
    /// traversal shapes and unknown URL spellings.
    /// </summary>
    internal static bool NormalizeSource(OpenHarmonyMediaSource source, out OpenHarmonyMediaSourceKind kind, out string location, out string reason)
    {
        kind = source.Kind;
        location = source.Location ?? string.Empty;
        reason = string.Empty;
        if (string.IsNullOrEmpty(location))
        {
            reason = "the source location is empty";
            return false;
        }
        if (location.IndexOfAny(s_fieldSeparators) >= 0)
        {
            reason = "the source location carries a control character";
            return false;
        }
        switch (source.Kind)
        {
            case OpenHarmonyMediaSourceKind.Url:
                if (location.StartsWith("file://", StringComparison.OrdinalIgnoreCase))
                {
                    // A file URL is not loadable from the served https page: serve it as a file.
                    kind = OpenHarmonyMediaSourceKind.File;
                    location = FileUrlToPath(location);
                    return IsSafeFilePath(location, out reason);
                }
                if (location.StartsWith('/'))
                {
                    kind = OpenHarmonyMediaSourceKind.File;
                    return IsSafeFilePath(location, out reason);
                }
                if (IsWebUrl(location))
                {
                    return true;
                }
                // A bare "media/intro.mp4" is a rawfile name; anything with an unknown scheme is
                // refused instead of letting the page resolve it.
                kind = OpenHarmonyMediaSourceKind.RawFile;
                return IsSafeRawFileName(location, out reason);
            case OpenHarmonyMediaSourceKind.RawFile:
                return IsSafeRawFileName(location, out reason);
            case OpenHarmonyMediaSourceKind.File:
                return IsSafeFilePath(location, out reason);
            default:
                reason = "unknown source kind";
                return false;
        }
    }

    /// <summary>True for the URL spellings the media page may load itself.</summary>
    internal static bool IsWebUrl(string location)
        => location.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
           || location.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
           || location.StartsWith("data:", StringComparison.OrdinalIgnoreCase)
           || location.StartsWith("blob:", StringComparison.OrdinalIgnoreCase)
           || location.StartsWith("about:", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// True for an ordinary relative rawfile name ("media/intro.mp4"): no leading '/', no '\',
    /// no empty/'.'/'..' segment, no control character. Mirrors the shell's guard.
    /// </summary>
    internal static bool IsSafeRawFileName(string name, out string reason)
    {
        reason = string.Empty;
        if (name.Length == 0 || name.StartsWith('/') || name.IndexOf('\\') >= 0)
        {
            reason = "a rawfile name must be a relative path";
            return false;
        }
        foreach (string segment in name.Split('/'))
        {
            if (segment.Length == 0 || segment == "." || segment == "..")
            {
                reason = "a rawfile name must not carry empty, '.' or '..' segments";
                return false;
            }
        }
        return true;
    }

    /// <summary>
    /// True for an absolute sandbox path without traversal: the shell reads it and answers the
    /// bytes; a relative path or a '..'/'\' shape could escape the sandbox.
    /// </summary>
    internal static bool IsSafeFilePath(string path, out string reason)
    {
        reason = string.Empty;
        if (!path.StartsWith('/') || path.IndexOf("..", StringComparison.Ordinal) >= 0 || path.IndexOf('\\') >= 0)
        {
            reason = "a file source must be an absolute sandbox path without '..' or '\\'";
            return false;
        }
        return true;
    }

    /// <summary>The served URL the page loads for one normalized source kind.</summary>
    internal static string EffectiveUrl(OpenHarmonyMediaSourceKind kind, string location)
        => kind == OpenHarmonyMediaSourceKind.Url ? location : MediaOrigin + MediaSourcePath;

    /// <summary>
    /// The shell's dotnetHost payload router. Called from
    /// <see cref="OpenHarmonyWebViewHandler.HandleJsMessage"/> for every page message; accepts
    /// only the shell's own envelope for this origin, a document id of a live surface and the
    /// matching slot. A malformed/foreign payload is ignored.
    /// </summary>
    internal static void OnJsMessage(string payload)
    {
        if (!payload.StartsWith("__OHORIGIN|", StringComparison.Ordinal) ||
            !OpenHarmonyHybridWebViewHandler.TryParseOriginEnvelope(payload, out Uri? origin, out string documentId, out string message))
        {
            return;
        }
        if (!IsMediaOrigin(origin) || !message.StartsWith(EventPrefix, StringComparison.Ordinal) || documentId.Length == 0)
        {
            return;
        }
        string body = message.Substring(EventPrefix.Length);
        int separator = body.IndexOf('|', StringComparison.Ordinal);
        if (separator <= 0)
        {
            return;
        }
        string slotTag = body.Substring(0, separator);
        int eventSlot = -1;
        if (slotTag.Length >= 2 && slotTag[0] == OpenHarmonyOverlays.SlotPrefix &&
            int.TryParse(slotTag.Substring(1), NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed))
        {
            eventSlot = parsed;
        }
        string rest = body.Substring(separator + 1);
        int kindSeparator = rest.IndexOf('|', StringComparison.Ordinal);
        string eventKind = kindSeparator >= 0 ? rest.Substring(0, kindSeparator) : rest;
        string json = kindSeparator >= 0 ? rest.Substring(kindSeparator + 1) : "{}";
        OpenHarmonyMediaElementSurface? surface = ResolveSurface(documentId, eventSlot);
        if (surface is null)
        {
            OpenHarmonyBridge.WriteStatus($"[maui] media event rejected: unknown document (slot {eventSlot})");
            return;
        }
        surface.HandlePageEvent(eventKind, json);
    }

    private static bool IsMediaOrigin(Uri? origin)
        => origin is { IsAbsoluteUri: true } && origin.Scheme == Uri.UriSchemeHttps
           && string.Equals(origin.Host, "media.local", StringComparison.OrdinalIgnoreCase);

    private static OpenHarmonyMediaElementSurface? ResolveSurface(string documentId, int slot)
    {
        lock (s_sync)
        {
            foreach (OpenHarmonyMediaElementSurface surface in s_surfaces)
            {
                if (surface._pageId == documentId && (slot < 0 || surface._slot == slot))
                {
                    return surface;
                }
            }
        }
        return null;
    }

    /// <summary>Drops the slot claim of an ended window (called with the window host reset; the
    /// child page destroys the released slot, so no media page survives its window).</summary>
    internal static void ReleaseWindowOverlays(string windowId)
    {
        if (string.IsNullOrEmpty(windowId) || windowId == OpenHarmonyWindowSurface.PrimaryWindowId)
        {
            return;
        }
        List<OpenHarmonyMediaElementSurface> surfaces;
        lock (s_sync)
        {
            surfaces = new List<OpenHarmonyMediaElementSurface>(s_surfaces);
        }
        foreach (OpenHarmonyMediaElementSurface surface in surfaces)
        {
            if (string.Equals(surface._windowId, windowId, StringComparison.Ordinal) && surface._slot >= 0)
            {
                OpenHarmonyChildWeb.ReleaseForWindow(windowId, surface._slot, surface);
                surface._slot = -1;
                surface._engaged = false;
                surface._windowClosed = true;
            }
        }
    }

    /// <summary>The off-device suite's seam: forgets every registered surface.</summary>
    internal static void ResetForTests()
    {
        lock (s_sync)
        {
            s_surfaces.Clear();
        }
    }

    /// <summary>LRU preemption hook (IOpenHarmonyOverlaySlotOwner): drop the claim, stay suspended until reused.</summary>
    public void OnOverlaySlotPreempted(int slot)
    {
        if (_slot != slot)
        {
            return;
        }
        _slot = -1;
        _preempted = true;
        _engaged = false;
        ResetPage();
        OpenHarmonyBridge.WriteStatus($"[maui] media overlay preempted: slot {slot}");
    }

    /// <summary>True while this element's overlay is showing (idle owners are preempted first).</summary>
    public bool IsOverlaySlotEngaged => _slot >= 0 && _engaged;

    private bool EnsureSlot()
    {
        if (_windowClosed)
        {
            return false;
        }
        // The owning control's window is the claim scope (B-M1): resolve it before claiming, so
        // a media element that connects inside a secondary window takes that window's child pool
        // instead of a primary overlay.
        RefreshWindow();
        if (_slot >= 0)
        {
            OpenHarmonyChildWeb.TouchForWindow(_windowId, _slot, this);
            return false;
        }
        int slot = OpenHarmonyChildWeb.AcquireForWindow(_windowId, this);
        if (slot < 0)
        {
            return false;
        }
        _slot = slot;
        bool replay = _preempted || _detached;
        _preempted = false;
        _detached = false;
        if (replay)
        {
            OpenHarmonyBridge.WriteStatus($"[maui] media overlay restored: slot {slot}");
        }
        return true;
    }

    /// <summary>
    /// Re-resolves the managed window (a late stamp after a deferred OpenWindow). A claim that
    /// belonged to the previous window is released with it; the element then re-claims through
    /// the same replay path AUTODISCONNECT/preemption use (the next AttachOverlay/Arrange), so a
    /// late window stamp can never leave the element pointing at the wrong window's pool.
    /// </summary>
    private void RefreshWindow()
    {
        if (_windowClosed || _windowIdResolver is null)
        {
            return;
        }
        string resolved = _windowIdResolver() ?? OpenHarmonyWindowSurface.PrimaryWindowId;
        if (string.Equals(resolved, _windowId, StringComparison.Ordinal))
        {
            return;
        }
        bool held = _slot >= 0;
        OpenHarmonyChildWeb.ReleaseForWindow(_windowId, _slot, this);
        _slot = -1;
        _windowId = resolved;
        if (held)
        {
            _detached = true;
        }
    }

    /// <summary>
    /// (Re)registers this element's slot with the shell. Every registration carries the current
    /// source and reloads the media page, which is the element's source reset; the page's "ready"
    /// event then triggers the load of the effective URL. A re-registration for the same
    /// slot+kind+location is skipped (an arrange/attach replay must not reload a live page).
    /// </summary>
    private void Register()
    {
        if (_slot < 0 || !_hasSource)
        {
            return;
        }
        string key = _slot.ToString(CultureInfo.InvariantCulture) + "|" + (int)_sourceKind + "|" + _sourceLocation;
        if (key == _registeredKey && _pageReady)
        {
            return;
        }
        _registeredKey = key;
        ResetPage();
        _engaged = true;
        string config = JsonSerializer.Serialize(new MediaAssetsConfig
        {
            Kind = (int)_sourceKind,
            Location = _sourceLocation,
            Id = _pageId,
            Slot = _slot,
        }, OpenHarmonySliceJsonContext.Default.MediaAssetsConfig);
        OpenHarmonyChildWeb.CommandForWindow(_windowId, "media", config);
    }

    private void ResetPage()
    {
        lock (_stateSync)
        {
            _pageReady = false;
        }
    }

    /// <summary>The page finished loading and published window.__ohMedia; load the source.</summary>
    private void HandleReady()
    {
        bool firstReady;
        lock (_stateSync)
        {
            firstReady = !_pageReady;
            _pageReady = true;
        }
        if (!firstReady || !_hasSource)
        {
            return;
        }
        string url = EffectiveUrl(_sourceKind, _sourceLocation);
        string script = "window.__ohMedia.load(" + JsonSerializer.Serialize(url, OpenHarmonySliceJsonContext.Default.String) +
            ", {\"aspect\":\"" + (_aspect == OpenHarmonyMediaAspect.AspectFill ? "fill" : "fit") + "\"" +
            ",\"muted\":" + (_muted ? "true" : "false") +
            ",\"volume\":" + _volume.ToString("0.###", CultureInfo.InvariantCulture) + "})";
        _ = RunCommandAsync(script);
        if (_playRequested)
        {
            _ = RunCommandAsync("window.__ohMedia.play()");
        }
    }

    /// <summary>Dispatches one page event; malformed payloads are ignored.</summary>
    private void HandlePageEvent(string eventKind, string json)
    {
        switch (eventKind)
        {
            case "ready":
                HandleReady();
                return;
            case "opened":
                ApplyOpened(json);
                return;
            case "duration":
                ApplyDuration(json);
                return;
            case "size":
                ApplySize(json);
                return;
            case "position":
                ApplyPosition(json);
                return;
            case "playing":
                ApplyState(OpenHarmonyMediaPlaybackState.Playing);
                return;
            case "paused":
                ApplyState(OpenHarmonyMediaPlaybackState.Paused);
                return;
            case "stopped":
                ApplyState(OpenHarmonyMediaPlaybackState.Stopped);
                return;
            case "seeked":
                ApplySeeked(json);
                return;
            case "ended":
                ApplyEnded(json);
                return;
            case "error":
            case "playfailed":
                ApplyError(json);
                return;
            case "hls":
                // HLS diagnostics (B-M1): manifest-parsed/recover-*/fallback-native/error
                // lines from the media page; the shell log carries them for the device round.
                OpenHarmonyBridge.WriteStatus($"[maui] media hls event: {json}");
                return;
            default:
                return;
        }
    }

    private void ApplyOpened(string json)
    {
        double duration = ReadNumber(json, "duration", 0);
        int width = (int)ReadNumber(json, "width", 0);
        int height = (int)ReadNumber(json, "height", 0);
        Interlocked.Exchange(ref _durationMs, Math.Max(0, (long)(duration * 1000)));
        lock (_stateSync)
        {
            _videoWidth = Math.Max(0, width);
            _videoHeight = Math.Max(0, height);
        }
        _state = (int)OpenHarmonyMediaPlaybackState.Prepared;
        StateChanged?.Invoke(this, new OpenHarmonyMediaStateChangedEventArgs(OpenHarmonyMediaPlaybackState.Prepared, "opened"));
        MediaOpened?.Invoke(this, new OpenHarmonyMediaOpenedEventArgs(Duration, _videoWidth, _videoHeight));
        SizeChanged?.Invoke(this, new OpenHarmonyMediaSizeChangedEventArgs(_videoWidth, _videoHeight));
        DurationChanged?.Invoke(this, new OpenHarmonyMediaDurationChangedEventArgs(Duration));
    }

    private void ApplyDuration(string json)
    {
        double duration = ReadNumber(json, "duration", 0);
        Interlocked.Exchange(ref _durationMs, Math.Max(0, (long)(duration * 1000)));
        DurationChanged?.Invoke(this, new OpenHarmonyMediaDurationChangedEventArgs(Duration));
    }

    private void ApplySize(string json)
    {
        int width = Math.Max(0, (int)ReadNumber(json, "width", 0));
        int height = Math.Max(0, (int)ReadNumber(json, "height", 0));
        lock (_stateSync)
        {
            _videoWidth = width;
            _videoHeight = height;
        }
        SizeChanged?.Invoke(this, new OpenHarmonyMediaSizeChangedEventArgs(width, height));
    }

    private void ApplyPosition(string json)
    {
        double position = ReadNumber(json, "position", 0);
        Interlocked.Exchange(ref _positionMs, Math.Max(0, (long)(position * 1000)));
        PositionChanged?.Invoke(this, new OpenHarmonyMediaPositionChangedEventArgs(Position));
    }

    private void ApplySeeked(string json)
    {
        ApplyPosition(json);
    }

    private void ApplyState(OpenHarmonyMediaPlaybackState state)
    {
        _state = (int)state;
        StateChanged?.Invoke(this, new OpenHarmonyMediaStateChangedEventArgs(state, string.Empty));
    }

    private void ApplyEnded(string json)
    {
        double position = ReadNumber(json, "position", 0);
        double duration = ReadNumber(json, "duration", 0);
        Interlocked.Exchange(ref _positionMs, Math.Max(0, (long)(position * 1000)));
        Interlocked.Exchange(ref _durationMs, Math.Max(0, (long)(duration * 1000)));
        _state = (int)OpenHarmonyMediaPlaybackState.Completed;
        StateChanged?.Invoke(this, new OpenHarmonyMediaStateChangedEventArgs(OpenHarmonyMediaPlaybackState.Completed, "ended"));
        Ended?.Invoke(this, new OpenHarmonyMediaEndedEventArgs(Position, Duration));
    }

    private void ApplyError(string json)
    {
        int code = (int)ReadNumber(json, "code", 0);
        string message = ReadString(json, "message");
        string name = ReadString(json, "name");
        string text = name.Length > 0 ? name + (message.Length > 0 ? ": " + message : string.Empty) : message;
        Fail(code, text.Length > 0 ? text : "the media page reported an error");
    }

    private void Fail(int code, string message)
    {
        _state = (int)OpenHarmonyMediaPlaybackState.Error;
        StateChanged?.Invoke(this, new OpenHarmonyMediaStateChangedEventArgs(OpenHarmonyMediaPlaybackState.Error, message));
        Failed?.Invoke(this, new OpenHarmonyMediaFailedEventArgs(code, message));
    }

    // The cold-start wait bound: the shell serves the media page from its rawfile in ~100 ms on
    // this device; 1.5 s covers a slow start and still answers Unavailable on a dead bridge.
    // The wait is armed only while a registration is actually in flight, so a source-less or
    // detached element (and the off-device stub) answers immediately.
    private const int PageReadyWaitMs = 1500;
    private const int PageReadyPollMs = 50;

    /// <summary>True while SetSource registered the page and its "ready" event has not arrived.</summary>
    private bool PendingPageLoad()
    {
        if (!IsSupported || !_hasSource || _registeredKey is null)
        {
            return false;
        }
        lock (_stateSync)
        {
            return !_pageReady;
        }
    }

    /// <summary>Waits (bounded) for a page load that is already in flight. Never throws.</summary>
    private async Task WaitForPageReadyAsync()
    {
        for (int waited = 0; waited < PageReadyWaitMs && PendingPageLoad(); waited += PageReadyPollMs)
        {
            await Task.Delay(PageReadyPollMs).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Runs one window.__ohMedia script over the slot-tagged eval channel. Null (no page/slot or
    /// no host library) answers Unavailable; an "ok":false ack answers Failed with the page's
    /// message. Never throws.
    /// </summary>
    private async Task<OpenHarmonyMediaResult> RunCommandAsync(string script)
    {
        if (!IsSupported || !_pageReady)
        {
            return new OpenHarmonyMediaResult(
                OpenHarmonyMediaStatus.Unavailable,
                _slot < 0 ? "no overlay slot is available for this media element" : "the media page is not ready");
        }
        string? result;
        try
        {
            result = await OpenHarmonyWebViewHandler.EvaluateJavaScriptAsyncCore(script, _slot, _windowId).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            OpenHarmonyBridge.WriteStatus($"[maui] media command failed: {ex.GetType().Name}");
            return new OpenHarmonyMediaResult(OpenHarmonyMediaStatus.Failed, ex.Message);
        }
        if (result is null)
        {
            return new OpenHarmonyMediaResult(OpenHarmonyMediaStatus.Unavailable, "the platform bridge is unavailable");
        }
        if (!TryParseAck(result, out bool ok, out string message))
        {
            // A page that answers something unexpected is still alive; treat it as accepted.
            return new OpenHarmonyMediaResult(OpenHarmonyMediaStatus.Success, string.Empty);
        }
        return ok
            ? new OpenHarmonyMediaResult(OpenHarmonyMediaStatus.Success, string.Empty)
            : new OpenHarmonyMediaResult(OpenHarmonyMediaStatus.Failed, message);
    }

    private static OpenHarmonyMediaResult FailedResult(string message)
        => new(OpenHarmonyMediaStatus.Failed, message);

    /// <summary>Parses the page's ack JSON ("{ok:true|false,message}").</summary>
    internal static bool TryParseAck(string ack, out bool ok, out string message)
    {
        ok = false;
        message = string.Empty;
        try
        {
            using JsonDocument document = JsonDocument.Parse(ack);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return false;
            }
            if (document.RootElement.TryGetProperty("ok", out JsonElement okElement))
            {
                ok = okElement.ValueKind == JsonValueKind.True;
            }
            if (document.RootElement.TryGetProperty("message", out JsonElement messageElement) &&
                messageElement.ValueKind == JsonValueKind.String)
            {
                message = messageElement.GetString() ?? string.Empty;
            }
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static double ReadNumber(string json, string property, double fallback)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind == JsonValueKind.Object &&
                document.RootElement.TryGetProperty(property, out JsonElement element) &&
                element.ValueKind == JsonValueKind.Number &&
                element.TryGetDouble(out double value) && double.IsFinite(value))
            {
                return value;
            }
        }
        catch (JsonException)
        {
            // A malformed payload keeps the default.
        }
        return fallback;
    }

    private static string ReadString(string json, string property)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind == JsonValueKind.Object &&
                document.RootElement.TryGetProperty(property, out JsonElement element) &&
                element.ValueKind == JsonValueKind.String)
            {
                return element.GetString() ?? string.Empty;
            }
        }
        catch (JsonException)
        {
            // A malformed payload answers empty.
        }
        return string.Empty;
    }

    /// <summary>Converts "file:///x%20y" into its absolute path ("/x y").</summary>
    internal static string FileUrlToPath(string fileUrl)
    {
        string path = fileUrl.Substring("file://".Length);
        try
        {
            path = Uri.UnescapeDataString(path);
        }
        catch (UriFormatException)
        {
            // Keep the raw text; the shell read fails and the element reports Failed.
        }
        return path;
    }

    private static string LocationForLog(OpenHarmonyMediaSourceKind kind, string location)
    {
        if (kind != OpenHarmonyMediaSourceKind.Url || location.Length <= 96)
        {
            return location;
        }
        return location.Substring(0, 93) + "...";
    }

    private static readonly char[] s_fieldSeparators = { '\t', '\n', '\r' };

    /// <summary>The "media" web command payload (mirrors the shell's MediaAssetsConfig).</summary>
    internal sealed class MediaAssetsConfig
    {
        [JsonPropertyName("kind")]
        public int Kind { get; init; }

        [JsonPropertyName("location")]
        public string Location { get; init; } = string.Empty;

        [JsonPropertyName("id")]
        public string Id { get; init; } = string.Empty;

        [JsonPropertyName("slot")]
        public int Slot { get; init; }
    }
}
