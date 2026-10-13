// Loopback redirects for the OpenHarmony WebAuthenticator: an http callback on
// 127.0.0.1/localhost is served by the app's own one-shot listener, so the external browser
// hands the authorization response back over the loopback interface instead of the custom
// scheme. The RFC 8252 native-app redirect survives on this platform (the browser and the app
// share the device network namespace; sockets need only the INTERNET permission the HTTP flow
// already implies), and the custom scheme's "open with" chooser is avoided: the browser's GET
// reaches the app without any want dispatch and without a callback skill in module.json5.
//
// Semantics (aligned with the scheme route and the MAUI contract):
//  - Route match: the request must address the registered callback (scheme/host/port/path
//    through WebAuthenticator.CanHandleCallback); anything else is answered and ignored while
//    the listener keeps waiting (browser preconnects/HEAD/favicon noise).
//  - Single-use + anti-replay: exactly one matching request completes the flow; a duplicate or
//    racing delivery is answered but never completes a second time (the Request claims the
//    completion atomically).
//  - Trust boundary: any local process reaching the port can answer the route (RFC 8252's
//    loopback flows live with that), so the standard provider-side defenses apply: PKCE and the
//    app's own state check. The state parameter is carried through to the result untouched and
//    never interpreted here.
//  - Fragment responses cannot arrive over HTTP (browsers never send the fragment); loopback
//    carries query responses (the authorization-code flow). form_post cannot either (POST is
//    answered 405): both stay scheme-route limitations too.
//  - Timeouts: every connection read is bounded (a silent browser preconnect can never wedge
//    the listener) and the listener lives exactly as long as the AuthenticateAsync await: the
//    caller's CancellationToken (MAUI's timeout mechanism) tears the socket down and frees the
//    port for the next attempt.
//  - https is rejected: the app cannot present a browser-trusted certificate for 127.0.0.1, so
//    the redirect never reaches the listener; providers must allow the http loopback URL.
//  - The default port is rejected: an app cannot bind privileged ports, and the callback URL
//    must name the explicit port the provider redirects to.
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Microsoft.Maui.Platform;

/// <summary>
/// The loopback redirect support of <see cref="OpenHarmonyWebAuthenticator"/>: recognises the
/// http(s) callback routes the platform can serve locally, validates them, and provides the
/// one-shot listener that receives the browser's redirect.
/// </summary>
internal static class OpenHarmonyWebAuthenticatorLoopback
{
    /// <summary>The IPv4 loopback literal the redirect should use (RFC 8252 §7.3).</summary>
    internal const string LoopbackHost = "127.0.0.1";

    /// <summary>The host name browsers commonly resolve to the same loopback interface.</summary>
    internal const string LocalhostHost = "localhost";

    /// <summary>Whether the callback route is an http(s) URL (as opposed to a custom scheme).</summary>
    public static bool IsHttpCallback(Uri callbackUrl) =>
        callbackUrl.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) ||
        callbackUrl.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Validates a callback route <see cref="IsHttpCallback"/> accepted; throws the platform
    /// contract's <see cref="InvalidOperationException"/> for the forms that cannot run here.
    /// Custom-scheme routes pass through untouched (the shipped want route).
    /// </summary>
    public static void ValidateCallbackUrl(Uri callbackUrl)
    {
        if (!IsLoopbackHost(callbackUrl))
        {
            throw new InvalidOperationException(
                "http(s) callback URLs run only on the device loopback interface: use " +
                "http://127.0.0.1:<port>/path (or http://localhost:<port>/path), or a custom " +
                "scheme like myapp://callback. The platform cannot dispatch an arbitrary http(s) " +
                "redirect back to the app.");
        }
        if (callbackUrl.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "https loopback callbacks are not supported: the app cannot present a " +
                "browser-trusted certificate for 127.0.0.1, so the browser cannot reach the " +
                "listener. Register an http://127.0.0.1:<port>/... redirect instead.");
        }
        if (callbackUrl.IsDefaultPort || callbackUrl.Port < 1024)
        {
            throw new InvalidOperationException(
                "The loopback callback URL must carry an explicit non-privileged port (for " +
                "example http://127.0.0.1:53123/callback): the app cannot bind the scheme's " +
                "default port or a port below 1024, and the provider redirects to the exact " +
                "registered URL.");
        }
    }

    private static bool IsLoopbackHost(Uri callbackUrl) =>
        callbackUrl.Host.Equals(LoopbackHost, StringComparison.Ordinal) ||
        callbackUrl.Host.Equals(LocalhostHost, StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// The one-shot HTTP listener behind a loopback callback route. Bind happens synchronously
/// before the browser opens (<see cref="Start"/> throws <see cref="InvalidOperationException"/>
/// when the port is taken or the socket cannot be created); the accept loops run through
/// <see cref="RunAsync(Func{Uri, bool}, CancellationToken)"/> and end on completion, disposal
/// or cancellation. Every accepted connection is handled independently, so a silent browser
/// preconnect cannot delay the real redirect.
/// </summary>
internal sealed class OpenHarmonyLoopbackListener : IDisposable
{
    /// <summary>Read bound for one request head; silent preconnects end here.</summary>
    internal const int ReadTimeoutMs = 3000;

    /// <summary>Cap for the request head a client may send before the connection is dropped.</summary>
    private const int MaxRequestHeadBytes = 8192;

    private const string SuccessPage =
        "<h2>Authentication complete</h2><p>You can close this window and return to the app.</p>";

    private readonly List<Socket> _sockets = new();
    private readonly Uri _baseUri;
    private readonly object _sync = new();
    private bool _disposed;

    private OpenHarmonyLoopbackListener(Uri callbackUrl)
    {
        // The request line's origin-form target resolves against the registered route, so the
        // parsed request URI carries exactly the expected scheme/host/port.
        _baseUri = new UriBuilder(callbackUrl.Scheme, callbackUrl.Host, callbackUrl.Port).Uri;
    }

    /// <summary>
    /// Binds the listener for <paramref name="callbackUrl"/> (validated by
    /// <see cref="OpenHarmonyWebAuthenticatorLoopback.ValidateCallbackUrl"/>). A
    /// <c>localhost</c> route binds IPv4 and, best-effort, IPv6 loopback so whichever address
    /// the browser resolves reaches the app; a failure of the required IPv4 bind surfaces as
    /// <see cref="InvalidOperationException"/> (the platform contract for a flow that cannot
    /// start), like the browser-launch failure.
    /// </summary>
    public static OpenHarmonyLoopbackListener Start(Uri callbackUrl)
    {
        var listener = new OpenHarmonyLoopbackListener(callbackUrl);
        try
        {
            if (callbackUrl.Host.Equals(OpenHarmonyWebAuthenticatorLoopback.LocalhostHost, StringComparison.OrdinalIgnoreCase))
            {
                listener.Bind(IPAddress.Loopback);
                listener.TryBind(IPAddress.IPv6Loopback);
            }
            else
            {
                listener.Bind(IPAddress.Parse(OpenHarmonyWebAuthenticatorLoopback.LoopbackHost));
            }
            return listener;
        }
        catch (Exception ex)
        {
            listener.Dispose();
            throw new InvalidOperationException(
                $"Failed to start the loopback listener on {callbackUrl.Scheme}://{callbackUrl.Host}:" +
                $"{callbackUrl.Port}: bind the port the redirect targets and make sure no other " +
                "authentication attempt (or process) holds it.", ex);
        }
    }

    /// <summary>
    /// Serves connections until one of them completes the flow: <paramref name="onCallback"/>
    /// returns true when the candidate callback URI claimed the completion (the listener then
    /// answers the success page and stops) and false to answer the request as not-the-route
    /// and keep waiting. Returns when the flow completes, the listener is disposed or
    /// <paramref name="cancellationToken"/> fires.
    /// </summary>
    public Task RunAsync(Func<Uri, bool> onCallback, CancellationToken cancellationToken)
    {
        var loops = new List<Task>(_sockets.Count);
        foreach (Socket socket in _sockets)
        {
            loops.Add(RunLoopAsync(socket, onCallback, cancellationToken));
        }
        return Task.WhenAll(loops);
    }

    private async Task RunLoopAsync(Socket socket, Func<Uri, bool> onCallback, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            Socket client;
            try
            {
                client = await socket.AcceptAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // Cancellation or disposal ends the loop; the request completes through the
                // caller's token, never through a listener fault.
                return;
            }
            _ = HandleConnectionAsync(client, onCallback, cancellationToken);
        }
    }

    private async Task HandleConnectionAsync(Socket client, Func<Uri, bool> onCallback, CancellationToken cancellationToken)
    {
        try
        {
            using (client)
            using (var stream = new NetworkStream(client, ownsSocket: false))
            {
                string? head;
                using (var readTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
                {
                    readTimeout.CancelAfter(ReadTimeoutMs);
                    try
                    {
                        head = await ReadHeadAsync(stream, readTimeout.Token).ConfigureAwait(false);
                    }
                    catch (Exception)
                    {
                        // A silent/slow/aborted speculative connection is not the flow.
                        return;
                    }
                }
                if (head is null)
                {
                    return;
                }
                bool parsed = TryParseRequest(head, out Uri? callbackUri, out bool isGet);
                if (!parsed)
                {
                    await RespondAsync(stream, 400, "Bad Request", "<h2>Bad request</h2>").ConfigureAwait(false);
                    return;
                }
                if (!isGet || callbackUri is null)
                {
                    await RespondAsync(stream, 405, "Method Not Allowed", "<h2>Method not allowed</h2>").ConfigureAwait(false);
                    return;
                }
                bool completed = false;
                try
                {
                    completed = onCallback(callbackUri);
                }
                catch (Exception)
                {
                    // The completion path never throws into the connection handler.
                }
                await RespondAsync(
                    stream,
                    completed ? 200 : 404,
                    completed ? "OK" : "Not Found",
                    completed ? SuccessPage : "<h2>Not found</h2>").ConfigureAwait(false);
                if (completed)
                {
                    // The single matching request ends the listener: the port is free again and
                    // any later delivery is refused, not completed.
                    Dispose();
                }
            }
        }
        catch (Exception)
        {
            // A broken connection is never a flow failure.
        }
    }

    private void Bind(IPAddress address)
    {
        // The socket is built by hand so SO_REUSEADDR is set before bind: a listener created
        // through the endpoint constructor binds first, and the option must be in place for
        // bind to re-admit the port when a previous attempt's last connection is still in
        // TIME_WAIT (a live listener is never shared on Linux without SO_REUSEPORT).
        var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
        try
        {
            socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            socket.Bind(new IPEndPoint(address, Port));
            socket.Listen(backlog: 8);
        }
        catch (Exception)
        {
            socket.Dispose();
            throw;
        }
        _sockets.Add(socket);
    }

    private int Port => _baseUri.Port;

    private void TryBind(IPAddress address)
    {
        try
        {
            Bind(address);
        }
        catch (SocketException)
        {
            // IPv6 loopback is best-effort: a v4-only resolve still reaches the IPv4 listener.
        }
    }

    /// <summary>
    /// Reads the request head (bounded by <see cref="MaxRequestHeadBytes"/>); returns null when
    /// the peer sent nothing. The stream read is the caller's to time out (the linked token).
    /// </summary>
    private static async Task<string?> ReadHeadAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        byte[] buffer = new byte[MaxRequestHeadBytes];
        int size = 0;
        while (size < buffer.Length)
        {
            int read = await stream.ReadAsync(buffer.AsMemory(size), cancellationToken).ConfigureAwait(false);
            if (read <= 0)
            {
                break;
            }
            size += read;
            if (HeadComplete(buffer, size))
            {
                break;
            }
        }
        return size == 0 ? null : Encoding.ASCII.GetString(buffer, 0, size);
    }

    /// <summary>Whether the buffered bytes contain the end of the request head (CRLF CRLF).</summary>
    private static bool HeadComplete(byte[] buffer, int size)
    {
        for (int i = 0; i < size; i++)
        {
            if (buffer[i] != (byte)'\n')
            {
                continue;
            }
            if (i >= 3 && buffer[i - 1] == (byte)'\r' && buffer[i - 2] == (byte)'\n' && buffer[i - 3] == (byte)'\r')
            {
                return true;
            }
            if (i >= 1 && buffer[i - 1] == (byte)'\n')
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Parses the request line: the method gates GET-only, and the origin-form target resolves
    /// against the registered base URI (an absolute-form target is parsed as-is); both then go
    /// through the same route match as the scheme callback.
    /// </summary>
    private bool TryParseRequest(string head, out Uri? callbackUri, out bool isGet)
    {
        callbackUri = null;
        isGet = false;
        int lineEnd = head.IndexOf('\n');
        string requestLine = (lineEnd < 0 ? head : head.Substring(0, lineEnd)).TrimEnd('\r');
        int methodEnd = requestLine.IndexOf(' ');
        if (methodEnd <= 0)
        {
            return false;
        }
        int targetEnd = requestLine.IndexOf(' ', methodEnd + 1);
        if (targetEnd <= methodEnd + 1)
        {
            return false;
        }
        string method = requestLine.Substring(0, methodEnd);
        string target = requestLine.Substring(methodEnd + 1, targetEnd - methodEnd - 1);
        if (!method.Equals("GET", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }
        isGet = true;
        if (target.StartsWith('/'))
        {
            return Uri.TryCreate(_baseUri, target, out callbackUri);
        }
        return Uri.TryCreate(target, UriKind.Absolute, out callbackUri);
    }

    private static async Task RespondAsync(NetworkStream stream, int status, string reason, string body)
    {
        byte[] bodyBytes = Encoding.UTF8.GetBytes(
            "<!DOCTYPE html><html><head><meta charset=\"utf-8\"><title>" + reason +
            "</title></head><body style=\"font-family:sans-serif;padding:4rem;text-align:center\">" +
            body + "</body></html>");
        string head =
            "HTTP/1.1 " + status + " " + reason + "\r\n" +
            "Content-Type: text/html; charset=utf-8\r\n" +
            "Content-Length: " + bodyBytes.Length + "\r\n" +
            "Cache-Control: no-store\r\n" +
            "Connection: close\r\n\r\n";
        try
        {
            await stream.WriteAsync(Encoding.ASCII.GetBytes(head)).ConfigureAwait(false);
            await stream.WriteAsync(bodyBytes).ConfigureAwait(false);
            await stream.FlushAsync().ConfigureAwait(false);
        }
        catch (Exception)
        {
            // The browser hung up first; the completion (if any) already happened.
        }
    }

    /// <summary>Stops every accept loop; repeated calls are no-ops.</summary>
    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
        }
        foreach (Socket socket in _sockets)
        {
            try
            {
                socket.Dispose();
            }
            catch (Exception)
            {
                // Dispose is best-effort teardown.
            }
        }
    }
}
