// IFileSystem for OpenHarmony: app data/cache directories from the ability context.
using Microsoft.Maui.Storage;

namespace Microsoft.Maui.Platform;

public sealed class OpenHarmonyFileSystem : IFileSystem
{
    public string AppDataDirectory => OpenHarmonyPaths.DataDirectory;

    public string CacheDirectory => OpenHarmonyPaths.CacheDirectory;

    public Task<Stream> OpenAppPackageFileAsync(string filename)
    {
        ArgumentException.ThrowIfNullOrEmpty(filename);
        // Packaged files live next to the application on device; the app directory is the root
        // for now (a rawfile/resources mapping needs the ArkTS asset manager).
        string path = Path.Combine(OpenHarmonyPaths.DataDirectory, filename);
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"App package file '{filename}' was not found.", path);
        }
        return Task.FromResult<Stream>(File.OpenRead(path));
    }

    public Task<bool> AppPackageFileExistsAsync(string filename)
    {
        ArgumentException.ThrowIfNullOrEmpty(filename);
        return Task.FromResult(File.Exists(Path.Combine(OpenHarmonyPaths.DataDirectory, filename)));
    }
}
