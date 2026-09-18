// Remaining Essentials implementations for the platform slice.
//
// Clipboard is file-backed. Connectivity reports Unknown and the intent-based APIs
// (launcher/browser/share) report "not supported": all of them need ArkTS module bridges
// (connection manager, wantAgent/ability start, share service) that the platform contract does
// not expose yet - see docs/plans for the integration plan.
using Microsoft.Maui.ApplicationModel;
using Microsoft.Maui.ApplicationModel.Communication;
using Microsoft.Maui.ApplicationModel.DataTransfer;
using Microsoft.Maui.Networking;
using Microsoft.OpenHarmony.Hosting;

namespace Microsoft.Maui.Platform;

public sealed class OpenHarmonyClipboard : IClipboard
{
    private readonly string _path;

    public OpenHarmonyClipboard(string? path = null)
    {
        _path = path ?? Path.Combine(OpenHarmonyPaths.DataDirectory, "clipboard.txt");
    }

    public bool HasText => File.Exists(_path) && new FileInfo(_path).Length > 0;

    public event EventHandler<EventArgs>? ClipboardContentChanged
    {
        add { }
        remove { }
    }

    public Task<string?> GetTextAsync()
    {
        try
        {
            return Task.FromResult<string?>(File.Exists(_path) ? File.ReadAllText(_path) : null);
        }
        catch
        {
            return Task.FromResult<string?>(null);
        }
    }

    public Task SetTextAsync(string? text)
    {
        try
        {
            if (string.IsNullOrEmpty(text))
            {
                if (File.Exists(_path))
                {
                    File.Delete(_path);
                }
            }
            else
            {
                Directory.CreateDirectory(OpenHarmonyPaths.DataDirectory);
                File.WriteAllText(_path, text);
            }
        }
        catch (Exception ex)
        {
            OpenHarmonyBridge.WriteStatus($"[maui] clipboard write failed: {ex.GetType().Name}");
        }
        return Task.CompletedTask;
    }

    public Task SetDataPackageAsync(DataPackage package)
    {
        if (package.Text is not null)
        {
            return SetTextAsync(package.Text);
        }
        return Task.CompletedTask;
    }

    public Task<DataPackage?> GetDataPackageAsync()
        => Task.FromResult<DataPackage?>(null);
}

public sealed class OpenHarmonyConnectivity : IConnectivity
{
    /// <summary>Real network state needs the ArkTS connection manager; report Unknown.</summary>
    public NetworkAccess NetworkAccess => NetworkAccess.Unknown;

    public IEnumerable<ConnectionProfile> ConnectionProfiles => Array.Empty<ConnectionProfile>();

    public event EventHandler<ConnectivityChangedEventArgs>? ConnectivityChanged
    {
        add { }
        remove { }
    }
}

public sealed class OpenHarmonyLauncher : ILauncher
{
    public Task<bool> CanOpenAsync(Uri uri) => Task.FromResult(false);

    public Task<bool> OpenAsync(Uri uri)
    {
        OpenHarmonyBridge.WriteStatus($"[maui] launcher is not supported yet (uri={uri})");
        return Task.FromResult(false);
    }

    public Task<bool> TryOpenAsync(Uri uri) => OpenAsync(uri);

    public Task<bool> OpenAsync(OpenFileRequest request)
    {
        OpenHarmonyBridge.WriteStatus("[maui] launcher file requests are not supported yet");
        return Task.FromResult(false);
    }

    public Task<bool> TryOpenAsync(OpenFileRequest request) => OpenAsync(request);
}

public sealed class OpenHarmonyBrowser : IBrowser
{
    public Task<bool> OpenAsync(string uri, BrowserLaunchMode launchMode)
    {
        OpenHarmonyBridge.WriteStatus($"[maui] browser is not supported yet (uri={uri})");
        return Task.FromResult(false);
    }

    public Task<bool> OpenAsync(Uri uri, BrowserLaunchMode launchMode) => OpenAsync(uri.ToString(), launchMode);

    public Task<bool> OpenAsync(string uri) => OpenAsync(uri, BrowserLaunchMode.SystemPreferred);

    public Task<bool> OpenAsync(Uri uri) => OpenAsync(uri.ToString(), BrowserLaunchMode.SystemPreferred);

    public Task<bool> OpenAsync(Uri uri, BrowserLaunchOptions options) => OpenAsync(uri.ToString(), BrowserLaunchMode.SystemPreferred);
}

public sealed class OpenHarmonyShare : IShare
{
    public Task RequestAsync(ShareTextRequest request)
    {
        OpenHarmonyBridge.WriteStatus($"[maui] share is not supported yet (text={request.Text})");
        return Task.CompletedTask;
    }

    public Task RequestAsync(ShareFileRequest request)
    {
        OpenHarmonyBridge.WriteStatus("[maui] share file requests are not supported yet");
        return Task.CompletedTask;
    }

    public Task RequestAsync(ShareMultipleFilesRequest request)
    {
        OpenHarmonyBridge.WriteStatus("[maui] share file requests are not supported yet");
        return Task.CompletedTask;
    }
}
