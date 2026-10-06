// MULTIWINDOW-L M4: per-window input routing for the slice.
//
// The text handlers (Entry/Editor/SearchBar) historically subscribed to the process-global
// OpenHarmonyBridge.TextInput/TextComposition/TextSubmitted events, which the shell's single
// hidden TextInput drives. With two windows that is ambiguous: the child window has its own
// hidden input (pages/SubWindow.ets) and its text arrives tagged with the managed surface id on
// the shell's subwindow channel. This router keeps one text port per text view and resolves the
// view's window at dispatch time (the adoption that stamps the window id may run after the
// handler connected), so a child's text reaches only the child's focused input and the primary
// window's text keeps its original global path.
//
// Back keys arrive on the same channel; DispatchBack raises the per-window Back event for
// handlers that know their window (the platform has no synchronous answer for the shell's
// onBackPress, so consumption is advisory - see the M4 doc).
namespace Microsoft.Maui.Platform;

/// <summary>
/// One text-input port of a handler: the callbacks it registers while connected plus the view
/// whose window resolves the target. The handler itself re-checks IsFocused on every event
/// (exactly like the global path), so several inputs per window coexist and only the focused
/// one consumes.
/// </summary>
internal sealed class OpenHarmonyWindowTextPort
{
    public OpenHarmonyWindowTextPort(
        IView owner,
        Action<string> onText,
        Action<string, int> onComposition,
        Action onSubmitted)
    {
        Owner = owner;
        OnText = onText;
        OnComposition = onComposition;
        OnSubmitted = onSubmitted;
    }

    public IView Owner { get; }

    public Action<string> OnText { get; }

    public Action<string, int> OnComposition { get; }

    public Action OnSubmitted { get; }
}

/// <summary>Per-window input dispatch for the slice (MULTIWINDOW-L M4).</summary>
internal static class OpenHarmonyWindowInputRouter
{
    private static readonly object s_sync = new();
    private static readonly List<OpenHarmonyWindowTextPort> s_textPorts = new();

    /// <summary>Raised for <see cref="DispatchBack"/> with the window id that received Back.</summary>
    internal static event Action<string>? Back;

    /// <summary>Registers a text port (idempotent per port).</summary>
    internal static void RegisterText(OpenHarmonyWindowTextPort port)
    {
        ArgumentNullException.ThrowIfNull(port);
        lock (s_sync)
        {
            if (!s_textPorts.Contains(port))
            {
                s_textPorts.Add(port);
            }
        }
    }

    /// <summary>Removes a port (the handler disconnected).</summary>
    internal static void UnregisterText(OpenHarmonyWindowTextPort port)
    {
        ArgumentNullException.ThrowIfNull(port);
        lock (s_sync)
        {
            s_textPorts.Remove(port);
        }
    }

    /// <summary>Sends typed text to the ports of one window.</summary>
    internal static void DispatchText(string windowId, string text)
    {
        foreach (OpenHarmonyWindowTextPort port in Snapshot(windowId))
        {
            port.OnText(text);
        }
    }

    /// <summary>Sends IME preview text to the ports of one window.</summary>
    internal static void DispatchComposition(string windowId, string value, int offset)
    {
        foreach (OpenHarmonyWindowTextPort port in Snapshot(windowId))
        {
            port.OnComposition(value, offset);
        }
    }

    /// <summary>Sends the return-key completion to the ports of one window.</summary>
    internal static void DispatchSubmitted(string windowId, string? text)
    {
        _ = text;
        foreach (OpenHarmonyWindowTextPort port in Snapshot(windowId))
        {
            port.OnSubmitted();
        }
    }

    /// <summary>Raises the per-window Back event for a child window.</summary>
    internal static void DispatchBack(string windowId)
    {
        try
        {
            Back?.Invoke(windowId);
        }
        catch (Exception ex)
        {
            Microsoft.OpenHarmony.Hosting.OpenHarmonyBridge.WriteStatus(
                $"[maui] window back handler failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>Number of text ports whose view currently resolves to a window (off-device
    /// assertions; the resolution runs at call time, not at registration).</summary>
    internal static int PortCount(string windowId)
    {
        int count = 0;
        foreach (OpenHarmonyWindowTextPort port in AllSnapshot())
        {
            if (OpenHarmonyMauiAppHost.ResolveWindowId(port.Owner) == windowId)
            {
                count++;
            }
        }
        return count;
    }

    // The callbacks run outside the router lock: a handler may dispatch into the UI tree and must
    // never run under a lock the UI thread could take next (see the connector's lock ordering).
    private static OpenHarmonyWindowTextPort[] Snapshot(string windowId)
    {
        OpenHarmonyWindowTextPort[] all = AllSnapshot();
        if (all.Length == 0)
        {
            return all;
        }
        List<OpenHarmonyWindowTextPort> matched = new();
        foreach (OpenHarmonyWindowTextPort port in all)
        {
            if (OpenHarmonyMauiAppHost.ResolveWindowId(port.Owner) == windowId)
            {
                matched.Add(port);
            }
        }
        return matched.ToArray();
    }

    private static OpenHarmonyWindowTextPort[] AllSnapshot()
    {
        lock (s_sync)
        {
            return s_textPorts.ToArray();
        }
    }
}
