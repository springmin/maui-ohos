// Directories the platform slice uses: the ability's files/cache dirs on device, temp outside.
using Microsoft.OpenHarmony.Hosting;

namespace Microsoft.Maui.Platform;

internal static class OpenHarmonyPaths
{
    public static string DataDirectory
    {
        get
        {
            string? files = OpenHarmonyBridge.Context?.FilesDir;
            if (!string.IsNullOrEmpty(files))
            {
                Directory.CreateDirectory(files);
                return files;
            }
            string fallback = Path.Combine(Path.GetTempPath(), "openharmony-app");
            Directory.CreateDirectory(fallback);
            return fallback;
        }
    }

    public static string CacheDirectory
    {
        get
        {
            string? cache = OpenHarmonyBridge.Context?.CacheDir;
            if (!string.IsNullOrEmpty(cache))
            {
                Directory.CreateDirectory(cache);
                return cache;
            }
            return DataDirectory;
        }
    }
}
