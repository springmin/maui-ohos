// IFileSystem for OpenHarmony: app data/cache directories from the ability context, packaged
// files from the extracted HAP payload (the shell publishes FilesDir/dotnet as AppDir).
using Microsoft.Maui.Storage;

namespace Microsoft.Maui.Platform;

public sealed class OpenHarmonyFileSystem : IFileSystem
{
    public string AppDataDirectory => OpenHarmonyPaths.DataDirectory;

    public string CacheDirectory => OpenHarmonyPaths.CacheDirectory;

    public Task<Stream> OpenAppPackageFileAsync(string filename)
    {
        ArgumentException.ThrowIfNullOrEmpty(filename);
        // Packaged files are the published output the shell extracted from dotnet.zip into the
        // context's AppDir (FilesDir/dotnet). Raw HAP resources outside the payload
        // (resources/rawfile/**) are not files in the sandbox; reading those needs a
        // resourceManager bridge (ohos_host_read_raw_file) that the host does not expose yet.
        string path = Path.Combine(OpenHarmonyPaths.AppPackageDirectory, filename);
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"App package file '{filename}' was not found.", path);
        }
        return Task.FromResult<Stream>(File.OpenRead(path));
    }

    public Task<bool> AppPackageFileExistsAsync(string filename)
    {
        ArgumentException.ThrowIfNullOrEmpty(filename);
        return Task.FromResult(File.Exists(Path.Combine(OpenHarmonyPaths.AppPackageDirectory, filename)));
    }
}
