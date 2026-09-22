// Remaining Essentials implementations for the platform slice.
//
// Clipboard is the system pasteboard (@ohos.pasteboard, request/result over the host bridge) and
// connectivity reads the host NDK path plus the shell's NetworkKit observer; both are defined in
// OpenHarmonyEssentialsBridges.cs. Launcher/Browser/Share live in OpenHarmonyAppLauncher.cs
// (startAbility bridge).
using Microsoft.Maui.ApplicationModel.DataTransfer;
using Microsoft.Maui.Networking;
using Microsoft.OpenHarmony.Hosting;

namespace Microsoft.Maui.Platform;

/// <summary>
/// Clipboard backed by the OpenHarmony system pasteboard through the host/ArkTS bridge (the
/// shell's registerClipboardSink handler, reading with ohos.permission.READ_PASTEBOARD on
/// demand). MAUI's <see cref="HasText"/> is synchronous and the pasteboard read is a user-grant
/// prompt, so it answers from the last known snapshot instead of prompting: every get/set
/// updates it, and the pasteboard 'update' push refreshes it without prompting. The shell
/// answers the has op without a prompt and a denial is cached here, so only an explicit
/// <see cref="GetTextAsync"/> can raise the system prompt. <see cref="ClipboardContentChanged"/>
/// is raised for every push (false until the first successful get/set/refresh). Off-device (no
/// host library) the cache stays empty and every call degrades to false/null/"" without throwing.
/// </summary>
public sealed class OpenHarmonyClipboard : IClipboard
{
    private readonly object _sync = new();
    private bool _hasText;
    private int _refreshing;
    private bool _readDenied;

    public OpenHarmonyClipboard()
    {
        OpenHarmonyClipboardBridge.Changed += OnPlatformClipboardChanged;
    }

    /// <summary>Last known "the pasteboard holds text" (refreshed after get/set and on push).</summary>
    public bool HasText
    {
        get
        {
            lock (_sync)
            {
                return _hasText;
            }
        }
    }

    /// <summary>Raised for every pasteboard 'update' the shell reports.</summary>
    public event EventHandler<EventArgs>? ClipboardContentChanged;

    public async Task<string?> GetTextAsync()
    {
        (int rc, string text) = await OpenHarmonyClipboardBridge
            .RequestAsync(OpenHarmonyClipboardBridge.GetOp, null, OpenHarmonyClipboardBridge.RequestTimeout)
            .ConfigureAwait(false);
        if (rc != 0)
        {
            // The explicit read was denied/unavailable: cache the denial so the pasteboard
            // observer never asks again (only a later explicit GetTextAsync can prompt).
            StoreDenied();
            return null;
        }
        Store(text);
        return string.IsNullOrEmpty(text) ? null : text;
    }

    public async Task SetTextAsync(string? text)
    {
        (int rc, _) = await OpenHarmonyClipboardBridge
            .RequestAsync(OpenHarmonyClipboardBridge.SetOp, text ?? string.Empty, OpenHarmonyClipboardBridge.RequestTimeout)
            .ConfigureAwait(false);
        if (rc == 0)
        {
            Store(text ?? string.Empty);
        }
    }

    /// <summary>
    /// Publishes a MAUI data package. The OpenHarmony pasteboard bridge is the host's
    /// <c>ohos_host_clipboard_request(id, op, text)</c> protocol (op 0 has / 1 get / 2 set, a single
    /// text payload; the shell's registerClipboardSink answers with @ohos.pasteboard plain text), so
    /// text is published on the normal path and a package whose payload is an image or a custom
    /// property cannot be represented. That used to complete silently - pretending success - now it
    /// is reported once through the status channel instead.
    /// </summary>
    public async Task SetDataPackageAsync(DataPackage package)
    {
        if (package is null)
        {
            return;
        }
        if (package.Text is not null)
        {
            await SetTextAsync(package.Text).ConfigureAwait(false);
            return;
        }
        if (package.Image is not null || package.Properties?.Count > 0)
        {
            LogNonTextPackageOnce();
        }
    }

    public async Task<DataPackage?> GetDataPackageAsync()
    {
        // Same bridge boundary as the setter: the pasteboard answers text only, so a read package
        // never carries image or custom-property content.
        string? text = await GetTextAsync().ConfigureAwait(false);
        return text is null ? null : new DataPackage { Text = text };
    }

    private static bool _nonTextPackageLogged;

    private static void LogNonTextPackageOnce()
    {
        if (_nonTextPackageLogged)
        {
            return;
        }
        _nonTextPackageLogged = true;
        Microsoft.OpenHarmony.Hosting.OpenHarmonyBridge.WriteStatus(
            "[maui] clipboard: the OpenHarmony pasteboard bridge publishes plain text only " +
            "(ohos_host_clipboard_request ops 0/1/2); the image/custom payload of this DataPackage was not published");
    }

    private void Store(string text)
    {
        lock (_sync)
        {
            _hasText = !string.IsNullOrEmpty(text);
            _readDenied = false;
        }
    }

    /// <summary>Caches a denied/unavailable read; HasText stays false until an explicit get succeeds.</summary>
    private void StoreDenied()
    {
        lock (_sync)
        {
            _hasText = false;
            _readDenied = true;
        }
    }

    /// <summary>
    /// Pasteboard changed (any app): raise the MAUI event and refresh the cached HasText. The
    /// refresh never prompts: once a denial is cached no has request is sent at all, and the
    /// shell answers the has op without a prompt (only GetTextAsync may prompt). The refresh is
    /// one at a time; a push that races is covered by the next.
    /// </summary>
    private void OnPlatformClipboardChanged()
    {
        _ = RefreshHasTextAsync();
        ClipboardContentChanged?.Invoke(this, EventArgs.Empty);
    }

    private async Task RefreshHasTextAsync()
    {
        lock (_sync)
        {
            if (_readDenied)
            {
                return;
            }
        }
        if (Interlocked.CompareExchange(ref _refreshing, 1, 0) != 0)
        {
            return;
        }
        try
        {
            (int rc, string text) = await OpenHarmonyClipboardBridge
                .RequestAsync(OpenHarmonyClipboardBridge.HasOp, null, OpenHarmonyClipboardBridge.RequestTimeout)
                .ConfigureAwait(false);
            if (rc == 0)
            {
                lock (_sync)
                {
                    _hasText = text == "1";
                    _readDenied = false;
                }
            }
            else
            {
                StoreDenied();
            }
        }
        finally
        {
            Interlocked.Exchange(ref _refreshing, 0);
        }
    }
}

/// <summary>
/// Connectivity for OpenHarmony: <see cref="NetworkAccess"/> maps the host's NDK level
/// (0 unknown, 1 none, 2 local, 3 internet) and the shell's NetworkKit observer (netAvailable /
/// netLost / netCapabilitiesChange / netUnavailable) pushes <see cref="ConnectivityChanged"/>
/// with the level the host re-read. Off-device the read fails and stays
/// <see cref="NetworkAccess.Unknown"/>.
///
/// <see cref="ConnectionProfiles"/> stays empty because the transport is not on this bridge: the
/// shell's observer receives a NetCapabilityInfo but discards it and pushes a bare
/// host.notifyNetworkAccess(), and the host callback (ohos_host_network_access_register)
/// forwards only the 0/1/2/3 level. The change that would populate the profiles: the shell
/// forwards the capability bits (for example a CSV/JSON of the NetCapabilityInfo.networkCap
/// transports: ethernet/wifi/cellular/bluetooth) with the push, the host's network-access
/// callback grows a capabilities argument (or a companion getter) and this class maps the bits
/// onto ConnectionProfile values (Ethernet/WiFi/Cellular/Bluetooth). The first network change
/// reports the gap once instead of silently answering an empty set forever.
/// </summary>
public sealed class OpenHarmonyConnectivity : IConnectivity
{
    public OpenHarmonyConnectivity()
    {
        OpenHarmonyConnectivityBridge.Changed += OnPlatformNetworkAccessChanged;
    }

    private static bool s_profilesGapLogged;

    /// <summary>Live network state read through ohos_host_network_access.</summary>
    public NetworkAccess NetworkAccess => MapNetworkAccess(OpenHarmonyConnectivityBridge.ReadNetworkAccess());

    /// <summary>
    /// No transport details over this bridge yet (see the type remark): empty is the honest
    /// answer until the shell forwards the capability bits.
    /// </summary>
    public IEnumerable<ConnectionProfile> ConnectionProfiles => Array.Empty<ConnectionProfile>();

    /// <summary>Raised for every network change the shell reports.</summary>
    public event EventHandler<ConnectivityChangedEventArgs>? ConnectivityChanged;

    /// <summary>Maps the host's 0/1/2/3 level to MAUI's enum (anything else is Unknown).</summary>
    internal static NetworkAccess MapNetworkAccess(int level) => level switch
    {
        1 => NetworkAccess.None,
        2 => NetworkAccess.Local,
        3 => NetworkAccess.Internet,
        _ => NetworkAccess.Unknown,
    };

    private void OnPlatformNetworkAccessChanged(int level)
    {
        if (!s_profilesGapLogged)
        {
            s_profilesGapLogged = true;
            OpenHarmonyBridge.WriteStatus(
                "[maui] connectivity: ConnectionProfiles stay empty; the shell's NetworkKit observer discards NetCapabilityInfo and pushes only a level (host.notifyNetworkAccess()), so the transport never reaches this bridge");
        }
        ConnectivityChanged?.Invoke(this, new ConnectivityChangedEventArgs(MapNetworkAccess(level), ConnectionProfiles));
    }
}
