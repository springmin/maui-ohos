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

    /// <summary>
    /// Root of the packaged application files (the HAP payload/assets). On device the ArkTS
    /// shell extracts <c>resources/rawfile/dotnet.zip</c> into <c>FilesDir/dotnet</c> and
    /// publishes that directory as the context's <see cref="OpenHarmonyAppContext.AppDir"/>,
    /// so the published output (MauiAsset items included) lives there. Falls back to the data
    /// directory off-device or with a host that does not publish an AppDir.
    /// </summary>
    public static string AppPackageDirectory
    {
        get
        {
            string? appDir = OpenHarmonyBridge.Context?.AppDir;
            if (!string.IsNullOrEmpty(appDir))
            {
                return appDir;
            }
            return DataDirectory;
        }
    }
}
