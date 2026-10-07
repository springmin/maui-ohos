// Alert overlay state: the alert manager stores what should be shown and the renderer draws it
// (scrim + dialog box + buttons) and routes button taps back through the completion callbacks.
using Microsoft.Maui.Graphics;

namespace Microsoft.Maui.Platform;

internal enum OpenHarmonyAlertKind
{
    Alert,
    ActionSheet,
    Prompt,
}

internal sealed class OpenHarmonyAlertState
{
    public required string? Title { get; init; }
    public required string? Message { get; init; }
    public required string? Accept { get; init; }
    public required string? Cancel { get; init; }
    public required Action<bool> Complete { get; init; }
    public OpenHarmonyAlertKind Kind { get; init; } = OpenHarmonyAlertKind.Alert;
    /// <summary>Action sheet entries (both option buttons and the cancel title).</summary>
    public IReadOnlyList<string> Options { get; init; } = Array.Empty<string>();
    public Action<string>? CompleteOption { get; init; }
    public Action<string?>? CompleteText { get; init; }
    public string PromptText { get; set; } = string.Empty;
    /// <summary>MULTIWINDOW-L M4: the host window id this alert belongs to. Only that window
    /// draws/hit-tests the dialog; the other window's frame never shows a foreign alert.
    /// MULTIWINDOW-L3 M3: each window keeps its own alert, so a dialog opened in one window
    /// neither replaces nor gates a dialog opened in another.</summary>
    public string WindowId { get; set; } = OpenHarmonyWindowSurface.PrimaryWindowId;
    /// <summary>M3: the owner window's surface size the dialog is laid out in. Captured from
    /// the per-window surface table at Show/SetSurface time, so two open dialogs keep their own
    /// geometry instead of sharing one global rectangle.</summary>
    public double SurfaceWidth { get; internal set; }
    public double SurfaceHeight { get; internal set; }

    /// <summary>Dialog geometry of this alert's own surface (shared by drawing and hit testing).</summary>
    public RectF BoxRect => OpenHarmonyAlertHost.BoxRectIn(SurfaceWidth, SurfaceHeight);

    /// <summary>Title row of the dialog (shared by drawing and the accessibility shadow tree).</summary>
    public RectF TitleRect
    {
        get
        {
            RectF box = BoxRect;
            return new RectF(box.X + 20, box.Y + 8, box.Width - 40, 48);
        }
    }

    /// <summary>Message body of the dialog (drawing and the accessibility shadow tree).</summary>
    public RectF MessageRect
    {
        get
        {
            RectF box = BoxRect;
            return new RectF(box.X + 20, box.Y + 60, box.Width - 40, box.Height - 140);
        }
    }

    /// <summary>Prompt text field (drawing and the accessibility shadow tree).</summary>
    public RectF PromptRect
    {
        get
        {
            RectF box = BoxRect;
            return new RectF(box.X + 20, box.Y + 110, box.Width - 40, 56);
        }
    }

    /// <summary>Row rects of an action sheet (options then cancel).</summary>
    public RectF OptionRect(int index) => OpenHarmonyAlertHost.OptionRectIn(BoxRect, index);

    public int OptionIndexAt(float x, float y)
    {
        if (Kind != OpenHarmonyAlertKind.ActionSheet)
        {
            return -1;
        }
        for (int i = 0; i < Options.Count; i++)
        {
            if (OptionRect(i).Contains(x, y))
            {
                return i;
            }
        }
        return -1;
    }

    public RectF AcceptRect => OpenHarmonyAlertHost.AcceptRectIn(BoxRect);

    public RectF CancelRect => OpenHarmonyAlertHost.CancelRectIn(BoxRect);
}

internal static class OpenHarmonyAlertHost
{
    // MULTIWINDOW-L3 M3: one alert per window. The primary window keeps the historical single
    // slot (s_primary, the `Current` property and the static rect helpers keep their old meaning
    // for every legacy caller); a secondary window's alert lives in s_windowAlerts keyed by the
    // window id, and its surface size in s_windowSurfaces. A dialog opened in one window can no
    // longer replace or be replaced by another window's dialog.
    private static readonly object s_sync = new();
    private static OpenHarmonyAlertState? s_primary;
    private static readonly Dictionary<string, OpenHarmonyAlertState> s_windowAlerts = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, (double Width, double Height)> s_windowSurfaces = new(StringComparer.Ordinal);
    private static double s_primaryWidth;
    private static double s_primaryHeight;

    /// <summary>The primary window's open alert (the historical single slot).</summary>
    public static OpenHarmonyAlertState? Current => s_primary;

    /// <summary>MULTIWINDOW-L M4: the open alert when it belongs to <paramref name="windowId"/>,
    /// null otherwise. Drawing, hit-testing and the accessibility modal nodes go through this
    /// accessor so two windows never render (or route touches into) the same dialog.</summary>
    public static OpenHarmonyAlertState? CurrentFor(string windowId)
    {
        if (windowId == OpenHarmonyWindowSurface.PrimaryWindowId)
        {
            return s_primary;
        }
        lock (s_sync)
        {
            return s_windowAlerts.TryGetValue(windowId, out OpenHarmonyAlertState? alert) ? alert : null;
        }
    }

    public static bool IsVisible => s_primary is not null;

    /// <summary>M3: whether <paramref name="windowId"/> has an open alert.</summary>
    public static bool IsVisibleFor(string windowId) => CurrentFor(windowId) is not null;

    /// <summary>The primary window's surface width (legacy static readers).</summary>
    public static double Width => s_primaryWidth;

    /// <summary>The primary window's surface height (legacy static readers).</summary>
    public static double Height => s_primaryHeight;

    public static event Action? Changed;

    /// <summary>M3: raised with the window whose alert state changed (opened/closed/edited), so
    /// the owning window's renderer can repaint without repainting the others. The legacy
    /// <see cref="Changed"/> event stays the primary-window signal.</summary>
    public static event Action<string>? ChangedFor;

    public static void Show(OpenHarmonyAlertState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        string windowId = string.IsNullOrEmpty(state.WindowId)
            ? OpenHarmonyWindowSurface.PrimaryWindowId
            : state.WindowId;
        if (windowId == OpenHarmonyWindowSurface.PrimaryWindowId)
        {
            state.SurfaceWidth = s_primaryWidth;
            state.SurfaceHeight = s_primaryHeight;
            s_primary = state;
        }
        else
        {
            lock (s_sync)
            {
                if (s_windowSurfaces.TryGetValue(windowId, out (double Width, double Height) surface))
                {
                    state.SurfaceWidth = surface.Width;
                    state.SurfaceHeight = surface.Height;
                }
                s_windowAlerts[windowId] = state;
            }
        }
        Changed?.Invoke();
        ChangedFor?.Invoke(windowId);
    }

    /// <summary>Closes every open alert (the legacy call). Per-window callers use
    /// <see cref="Hide(string)"/> so a submit in one window cannot dismiss another's dialog.</summary>
    public static void Hide()
    {
        List<string> hidden = new();
        lock (s_sync)
        {
            if (s_primary is not null)
            {
                hidden.Add(OpenHarmonyWindowSurface.PrimaryWindowId);
            }
            hidden.AddRange(s_windowAlerts.Keys);
            s_primary = null;
            s_windowAlerts.Clear();
        }
        if (hidden.Count == 0)
        {
            return;
        }
        Changed?.Invoke();
        foreach (string windowId in hidden)
        {
            ChangedFor?.Invoke(windowId);
        }
    }

    /// <summary>M3: closes exactly one window's alert; a no-op when that window has none.</summary>
    public static void Hide(string windowId)
    {
        if (windowId == OpenHarmonyWindowSurface.PrimaryWindowId)
        {
            if (s_primary is null)
            {
                return;
            }
            s_primary = null;
        }
        else
        {
            lock (s_sync)
            {
                if (!s_windowAlerts.Remove(windowId))
                {
                    return;
                }
            }
        }
        Changed?.Invoke();
        ChangedFor?.Invoke(windowId);
    }

    /// <summary>M3: records a window's surface size for its own dialog. The primary window keeps
    /// the historical static Width/Height (the legacy readers); a secondary window's size is
    /// stored per id and updates its open alert's geometry.</summary>
    public static void SetSurface(string windowId, double width, double height)
    {
        if (windowId == OpenHarmonyWindowSurface.PrimaryWindowId)
        {
            s_primaryWidth = width;
            s_primaryHeight = height;
            if (s_primary is not null)
            {
                s_primary.SurfaceWidth = width;
                s_primary.SurfaceHeight = height;
            }
            return;
        }
        lock (s_sync)
        {
            s_windowSurfaces[windowId] = (width, height);
            if (s_windowAlerts.TryGetValue(windowId, out OpenHarmonyAlertState? alert))
            {
                alert.SurfaceWidth = width;
                alert.SurfaceHeight = height;
            }
        }
    }

    // The primary window's legacy static rects (the dialog currently open in the primary window,
    // or the last primary surface when none is open). A secondary window's callers use the
    // OpenHarmonyAlertState instance rects above.
    public static RectF BoxRect => s_primary?.BoxRect ?? BoxRectIn(s_primaryWidth, s_primaryHeight);

    public static RectF TitleRect => s_primary?.TitleRect ?? TitleRectIn(BoxRect);

    public static RectF MessageRect => s_primary?.MessageRect ?? MessageRectIn(BoxRect);

    public static RectF PromptRect => s_primary?.PromptRect ?? PromptRectIn(BoxRect);

    public static RectF OptionRect(int index) => OptionRectIn(BoxRect, index);

    public static int OptionIndexAt(float x, float y) => s_primary?.OptionIndexAt(x, y) ?? -1;

    public static RectF PromptAcceptRect => AcceptRect;

    public static RectF AcceptRect => s_primary?.AcceptRect ?? AcceptRectIn(BoxRect);

    public static RectF CancelRect => s_primary?.CancelRect ?? CancelRectIn(BoxRect);

    public static void PromptAppend(string text)
        => PromptAppend(OpenHarmonyWindowSurface.PrimaryWindowId, text);

    /// <summary>M3: appends typed text to one window's open prompt only; the keyboard bridge of
    /// another window must not edit this dialog.</summary>
    public static void PromptAppend(string windowId, string text)
    {
        if (CurrentFor(windowId) is { Kind: OpenHarmonyAlertKind.Prompt } prompt)
        {
            prompt.PromptText += text;
            Changed?.Invoke();
            ChangedFor?.Invoke(windowId);
        }
    }

    public static void PromptBackspace()
        => PromptBackspace(OpenHarmonyWindowSurface.PrimaryWindowId);

    public static void PromptBackspace(string windowId)
    {
        if (CurrentFor(windowId) is { Kind: OpenHarmonyAlertKind.Prompt } prompt && prompt.PromptText.Length > 0)
        {
            prompt.PromptText = prompt.PromptText[..^1];
            Changed?.Invoke();
            ChangedFor?.Invoke(windowId);
        }
    }

    // Geometry builders shared by the instance rects and the primary legacy statics.
    internal static RectF BoxRectIn(double surfaceWidth, double surfaceHeight)
    {
        float width = (float)Math.Min(640, Math.Max(320, surfaceWidth - 160));
        float height = 260;
        return new RectF((float)((surfaceWidth - width) / 2), (float)((surfaceHeight - height) / 2), width, height);
    }

    internal static RectF TitleRectIn(RectF box)
        => new(box.X + 20, box.Y + 8, box.Width - 40, 48);

    internal static RectF MessageRectIn(RectF box)
        => new(box.X + 20, box.Y + 60, box.Width - 40, box.Height - 140);

    internal static RectF PromptRectIn(RectF box)
        => new(box.X + 20, box.Y + 110, box.Width - 40, 56);

    internal static RectF OptionRectIn(RectF box, int index)
    {
        float rowHeight = 64;
        return new RectF(box.X, box.Y + 56 + index * rowHeight, box.Width, rowHeight);
    }

    internal static RectF AcceptRectIn(RectF box)
    {
        float buttonWidth = 160;
        return new RectF(box.Right - buttonWidth - 16, box.Bottom - 64, buttonWidth, 48);
    }

    internal static RectF CancelRectIn(RectF box)
    {
        RectF accept = AcceptRectIn(box);
        return new RectF(accept.X - 176, accept.Y, 160, 48);
    }
}
