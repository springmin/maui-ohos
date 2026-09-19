// Remaining Essentials implementations for the platform slice.
//
// Clipboard is file-backed. Connectivity reports Unknown until the ArkTS connection manager is
// bridged. Launcher/Browser/Share live in OpenHarmonyAppLauncher.cs (startAbility bridge).
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

