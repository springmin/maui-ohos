// Notification Kit for OpenHarmony: the runtime forwards a publish request to the ArkTS shell
// (which owns the @ohos.notificationManager call); Show returns false instead of throwing when
// the host library or the shell sink is unavailable.
using System.Runtime.InteropServices;

namespace Microsoft.Maui.Platform;

public static partial class OpenHarmonyNotifications
{
    private const string HostLibrary = "libopenharmonyhost.so";

    [LibraryImport(HostLibrary, EntryPoint = "ohos_host_notification_show", StringMarshalling = StringMarshalling.Utf8)]
    private static partial int NotificationShow(int id, string title, string text);

    private static bool _available = true;

    /// <summary>Publishes a basic-text notification; returns false when it could not be sent.</summary>
    public static bool Show(string title, string text, int id = 0)
    {
        if (!_available || string.IsNullOrEmpty(title))
        {
            return false;
        }
        try
        {
            return NotificationShow(id, title, text ?? string.Empty) == 0;
        }
        catch (DllNotFoundException)
        {
            _available = false;
            return false;
        }
        catch (EntryPointNotFoundException)
        {
            _available = false;
            return false;
        }
    }
}
