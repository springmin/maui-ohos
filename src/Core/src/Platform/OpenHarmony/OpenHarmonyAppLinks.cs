// Deep links / activation for OpenHarmony: turns the want the ArkTS shell forwards (the
// onNewWant/create want uri, viewData actions, app:// link and https app links) into MAUI
// navigation.
//
// Contract:
//   * The cold-start want is captured by the ability's onCreate and reaches the host before
//     startApp; the host buffers it until the bridge registers, so it can arrive before the
//     application window exists. The warm path (onNewWant of the running instance) arrives on
//     the shell thread while the app is live.
//   * Requests are queued and applied one at a time (a later want cannot overtake a navigation
//     that is still running). The shell assigns a monotonic sequence per want; a re-delivered
//     or stale sequence is dropped. A request for which no Shell/NavigationPage exists yet
//     stays queued and is retried through OnHostReady (the app host calls it after the window
//     is created/activated and on every foreground).
//   * Routes go through Shell.GoToAsync, which is the existing Navigating approval chain: an
//     app-canceled navigation is recorded, never bypassed. app://host/path?query maps to
//     //host/path?query; an https link is accepted only when its host is in the app-link
//     allow-list (seeded from the packaging's OpenHarmonyAppLinkHosts through the activation
//     payload; an app can add hosts through AllowedHttpsHosts).
//   * Without a Shell the request falls back to a NavigationPage when the route is registered
//     with Routing.RegisterRoute and the live window's root is a NavigationPage; otherwise it is
//     ignored with a status line. A malformed URI, an unregistered route or a failing
//     navigation is logged and dropped, never thrown into the shell callback.
using System.Collections.ObjectModel;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Hosting;
using Microsoft.OpenHarmony.Hosting;

namespace Microsoft.Maui.Platform;

/// <summary>
/// Deep-link dispatcher for the OpenHarmony slice. The app host installs it; the hosting
/// bridge's activation events feed it (cold-start want and onNewWant alike) and
/// <see cref="Activate(string)"/> is the app-facing entry for an explicit link.
/// </summary>
public static class OpenHarmonyAppLinks
{
    /// <summary>Most queued activations kept when navigation has not reached a target yet.</summary>
    private const int PendingLimit = 8;

    /// <summary>Most characters one status line may quote from want-derived text.</summary>
    private const int MaxStatusTextChars = 512;

    private static readonly object s_sync = new();
    private static readonly Queue<Activation> s_queue = new();
    private static readonly SynchronizedHostList s_allowedHttpsHosts = new();
    private static IServiceProvider? s_services;
    private static long s_lastSequence;
    private static bool s_pumping;
    private static bool s_installed;

    /// <summary>
    /// https app-link hosts the managed side accepts. Seeded from the packaging's app-link
    /// allow-list (app.json linkHosts, forwarded with each activation) and extendable by the
    /// app before/while it runs; the list is read case-insensitively. An empty list refuses
    /// every https link while app:// links keep working.
    /// </summary>
    public static IList<string> AllowedHttpsHosts => s_allowedHttpsHosts;

    /// <summary>
    /// Activates an explicit deep link (the same semantics as a want forwarded by the shell):
    /// queued, de-duplicated by the internal sequence, and applied through the approval chain.
    /// </summary>
    public static void Activate(string uri)
    {
        if (string.IsNullOrWhiteSpace(uri))
        {
            return;
        }
        Enqueue(new Activation(uri, string.Empty, string.Empty, Array.Empty<string>(), NextLocalSequence()));
    }

    /// <summary>
    /// Subscribes to the bridge's activations and remembers the service provider the
    /// NavigationPage fallback resolves routes with. Called by <see cref="OpenHarmonyMauiAppHost"/>;
    /// repeated calls are no-ops.
    /// </summary>
    internal static void Install(IServiceProvider services)
    {
        lock (s_sync)
        {
            if (s_installed)
            {
                return;
            }
            s_installed = true;
            s_services = services;
        }
        OpenHarmonyBridge.Activation += OnBridgeActivation;
        WriteStatus("[maui] app links installed");
    }

    /// <summary>
    /// Retries queued requests that found no navigation target yet. Called by the app host when
    /// the window/lifecycle makes a target possible (window created/activated, foreground).
    /// </summary>
    internal static void OnHostReady() => Pump();

    private static void OnBridgeActivation(OpenHarmonyActivationEventArgs activation)
        => Enqueue(new Activation(activation.Uri, activation.Action, activation.Parameters,
                                  activation.LinkHosts, activation.Sequence));

    private static long NextLocalSequence()
    {
        lock (s_sync)
        {
            // The value must be strictly above the last seen sequence for Enqueue to accept it;
            // Enqueue owns the assignment so both paths share one monotonic source.
            return s_lastSequence + 1;
        }
    }

    private static void Enqueue(Activation activation)
    {
        bool duplicate;
        lock (s_sync)
        {
            // 0 = the shell did not report a sequence (an old shell or an explicit Activate is
            // not possible here); only positive sequences participate in de-duplication.
            duplicate = activation.Sequence > 0 && activation.Sequence <= s_lastSequence;
            if (!duplicate)
            {
                if (activation.Sequence > s_lastSequence)
                {
                    s_lastSequence = activation.Sequence;
                }
                if (s_queue.Count >= PendingLimit)
                {
                    s_queue.Dequeue();
                }
                s_queue.Enqueue(activation);
            }
            foreach (string host in activation.LinkHosts)
            {
                if (!string.IsNullOrWhiteSpace(host) && !HostListContains(host))
                {
                    s_allowedHttpsHosts.Add(host);
                }
            }
        }
        if (duplicate)
        {
            WriteStatus($"[maui] deep link dropped (sequence {activation.Sequence} already seen)");
            return;
        }
        WriteStatus($"[maui] deep link queued: seq={activation.Sequence} uri='{Flatten(activation.Uri)}'");
        Pump();
    }

    /// <summary>Starts the serial pump when idle; the queued order is preserved.</summary>
    private static void Pump()
    {
        lock (s_sync)
        {
            if (s_pumping || s_queue.Count == 0)
            {
                return;
            }
            s_pumping = true;
        }
        _ = PumpAsync();
    }

    private static async Task PumpAsync()
    {
        try
        {
            while (true)
            {
                Activation activation;
                lock (s_sync)
                {
                    if (s_queue.Count == 0)
                    {
                        return;
                    }
                    activation = s_queue.Peek();
                }

                bool applied = await TryApplyAsync(activation).ConfigureAwait(false);
                lock (s_sync)
                {
                    if (applied && s_queue.Count > 0 && ReferenceEquals(s_queue.Peek(), activation))
                    {
                        s_queue.Dequeue();
                        continue;
                    }
                }
                // No target yet (or a newer head replaced this one): stop until the host is ready.
                return;
            }
        }
        finally
        {
            lock (s_sync)
            {
                s_pumping = false;
            }
        }
    }

    /// <summary>
    /// Applies one activation. Returns false when no navigation target exists yet and the
    /// request must stay queued; every other outcome is terminal (the failure is logged).
    /// </summary>
    private static async Task<bool> TryApplyAsync(Activation activation)
    {
        if (activation.Uri.Length == 0)
        {
            WriteStatus(activation.Action.Length > 0
                ? $"[maui] activation ignored: no uri (action '{Flatten(activation.Action)}')"
                : "[maui] activation ignored: no uri");
            return true;
        }
        if (!TryBuildRoute(activation, out string route, out string rejection))
        {
            WriteStatus($"[maui] deep link ignored: {rejection} (uri='{Flatten(activation.Uri)}')");
            return true;
        }

        Shell? shell = FindShell();
        if (shell is not null)
        {
            await NavigateShellAsync(shell, activation, route);
            return true;
        }

        Microsoft.Maui.Controls.Window? window = FindWindow();
        if (window?.Page is NavigationPage navigationPage)
        {
            await NavigatePageAsync(navigationPage, activation, route);
            return true;
        }

        WriteStatus($"[maui] deep link pending: no Shell or NavigationPage is live yet (route '{route}')");
        return false;
    }

    private static async Task NavigateShellAsync(Shell shell, Activation activation, string route)
    {
        Page? before = shell.CurrentPage;
        try
        {
            await shell.GoToAsync(route);
            if (ReferenceEquals(before, shell.CurrentPage))
            {
                // MAUI completes GoToAsync without throwing when the Navigating chain cancels
                // the request, so the observable signal is that the current page did not move:
                // record it as not applied instead of claiming a navigation happened.
                WriteStatus($"[maui] deep link not applied (canceled by the Navigating chain or already current): route '{route}'");
                return;
            }
            WriteStatus($"[maui] deep link '{Flatten(activation.Uri)}' -> route '{route}'");
        }
        catch (OperationCanceledException)
        {
            // Some MAUI paths surface the cancellation as a canceled task instead.
            WriteStatus($"[maui] deep link canceled by the Navigating chain: route '{route}'");
        }
        catch (Exception ex)
        {
            WriteStatus($"[maui] deep link failed (route '{route}'): {ex.GetType().Name}: {Flatten(ex.Message)}");
        }
    }

    /// <summary>
    /// NavigationPage fallback: a route registered through Routing.RegisterRoute is realized and
    /// pushed; the push still raises the page's Navigating chain. An unregistered route (or a
    /// non-page registration) is reported and ignored.
    /// </summary>
    private static async Task NavigatePageAsync(NavigationPage navigationPage, Activation activation, string route)
    {
        if (s_services is null)
        {
            WriteStatus($"[maui] deep link ignored: no services to resolve route '{route}' for the NavigationPage");
            return;
        }
        string lookup = route.TrimStart('/');
        int query = lookup.IndexOf('?');
        if (query >= 0)
        {
            lookup = lookup[..query];
        }
        try
        {
            Element? content = Routing.GetOrCreateContent(lookup, s_services);
            if (content is Page page)
            {
                await navigationPage.PushAsync(page);
                WriteStatus($"[maui] deep link '{Flatten(activation.Uri)}' -> NavigationPage route '{lookup}'");
                return;
            }
            // GetOrCreateContent is silent for an unknown route (it produces a non-page element
            // or null), so the honest message is about the missing page, not a registration.
            WriteStatus($"[maui] deep link ignored: no registered NavigationPage page for route '{lookup}'");
        }
        catch (Exception ex)
        {
            WriteStatus($"[maui] deep link ignored: no NavigationPage route '{lookup}' ({ex.GetType().Name})");
        }
    }

    /// <summary>
    /// The navigation targets in preference order: the current Shell, a Shell that is the live
    /// window's page, then the window's page for the NavigationPage fallback.
    /// </summary>
    private static Shell? FindShell()
    {
        if (Shell.Current is { } current)
        {
            return current;
        }
        if (FindWindow()?.Page is Shell shell)
        {
            return shell;
        }
        return null;
    }

    private static Microsoft.Maui.Controls.Window? FindWindow()
    {
        IApplication? application = IPlatformApplication.Current?.Application ?? Application.Current;
        if (application is null)
        {
            return null;
        }
        foreach (IWindow window in application.Windows)
        {
            if (window is Microsoft.Maui.Controls.Window target)
            {
                return target;
            }
        }
        return null;
    }

    /// <summary>
    /// Maps the want URI onto a Shell route: app://host/path?query becomes //host/path?query;
    /// an https app link contributes its path (the host must be allowed); everything else is
    /// rejected with a reason. The fragment is dropped (Shell routes do not consume it).
    /// </summary>
    private static bool TryBuildRoute(Activation activation, out string route, out string rejection)
    {
        route = string.Empty;
        rejection = string.Empty;
        if (!Uri.TryCreate(activation.Uri, UriKind.Absolute, out Uri? uri))
        {
            rejection = "the uri is malformed";
            return false;
        }

        string path = uri.AbsolutePath.Trim('/');
        if (uri.Scheme.Equals("app", StringComparison.OrdinalIgnoreCase))
        {
            string host = uri.Host.Trim('/');
            string segments = (host.Length > 0 ? host + "/" + path : path).Trim('/');
            if (segments.Length == 0)
            {
                rejection = "the app link carries no route";
                return false;
            }
            route = "//" + segments;
        }
        else if (uri.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase))
        {
            if (!IsHttpsHostAllowed(uri.Host))
            {
                rejection = $"https host '{Flatten(uri.Host)}' is not in the app-link allow-list";
                return false;
            }
            if (path.Length == 0)
            {
                rejection = "the https app link carries no route";
                return false;
            }
            route = "//" + path;
        }
        else
        {
            rejection = $"the scheme '{uri.Scheme}' is not supported (app:// and allow-listed https:// only)";
            return false;
        }

        if (uri.Query.Length > 0)
        {
            route += uri.Query;
        }
        return true;
    }

    private static bool IsHttpsHostAllowed(string host)
    {
        if (string.IsNullOrEmpty(host))
        {
            return false;
        }
        lock (s_sync)
        {
            foreach (string allowed in s_allowedHttpsHosts)
            {
                if (string.Equals(allowed, host, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }
        return false;
    }

    /// <summary>Case-insensitive membership for the seed list (callers hold <see cref="s_sync"/>).</summary>
    private static bool HostListContains(string host)
    {
        lock (s_sync)
        {
            for (int i = 0; i < s_allowedHttpsHosts.Count; i++)
            {
                if (string.Equals(s_allowedHttpsHosts[i], host, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }
        return false;
    }

    private static void WriteStatus(string message)
    {
        try
        {
            OpenHarmonyBridge.WriteStatus(message);
        }
        catch
        {
            // Diagnostics only: the deep-link path must never fail because logging did.
        }
    }

    /// <summary>
    /// One-line, bounded text for status lines. A want uri is external input: control
    /// characters (including newlines the JSON transport decodes back) are replaced with
    /// spaces so a malicious link cannot inject extra lines into dotnet-status.txt, and a
    /// surrogate pair is never cut in half.
    /// </summary>
    private static string Flatten(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }
        int length = Math.Min(value.Length, MaxStatusTextChars);
        char[]? flattened = null;
        for (int i = 0; i < length; i++)
        {
            char c = value[i];
            if (char.IsControl(c) || c == '\u2028' || c == '\u2029')
            {
                flattened ??= value.ToCharArray();
                flattened[i] = ' ';
            }
        }
        if (length > 0 && char.IsHighSurrogate(value[length - 1]))
        {
            length--;
        }
        string result = flattened is null ? value[..length] : new string(flattened, 0, length);
        return value.Length > MaxStatusTextChars ? result + "..." : result;
    }

    /// <summary>
    /// The app-facing https allow-list: every mutation takes the dispatcher lock, so an app
    /// extending the list while the activation pump reads it cannot corrupt the backing array
    /// or make an enumeration throw out of a navigation attempt (the property type stays
    /// <see cref="IList{T}"/>).
    /// </summary>
    private sealed class SynchronizedHostList : Collection<string>
    {
        protected override void InsertItem(int index, string item)
        {
            lock (s_sync)
            {
                base.InsertItem(index, item);
            }
        }

        protected override void SetItem(int index, string item)
        {
            lock (s_sync)
            {
                base.SetItem(index, item);
            }
        }

        protected override void RemoveItem(int index)
        {
            lock (s_sync)
            {
                base.RemoveItem(index);
            }
        }

        protected override void ClearItems()
        {
            lock (s_sync)
            {
                base.ClearItems();
            }
        }
    }

    /// <summary>One want-derived activation request (shell payload or explicit Activate).</summary>
    private sealed class Activation
    {
        public Activation(string uri, string action, string parameters, string[] linkHosts, long sequence)
        {
            Uri = uri ?? string.Empty;
            Action = action ?? string.Empty;
            Parameters = parameters ?? string.Empty;
            LinkHosts = linkHosts ?? Array.Empty<string>();
            Sequence = sequence;
        }

        public string Uri { get; }
        public string Action { get; }
        public string Parameters { get; }
        public string[] LinkHosts { get; }
        public long Sequence { get; }
    }
}
