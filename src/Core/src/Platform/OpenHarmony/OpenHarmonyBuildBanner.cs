// Startup build banner for the OpenHarmony platform slice. The slice sources are compiled into
// the application assembly, so that assembly's informational version identifies the hap build
// (it carries the source-revision metadata); the ABI comes from the running process. The line is
// written once through the host status channel ([maui] lines in <filesDir>/dotnet-status.txt)
// after OpenHarmonyBridge.Attach has read the app context, and stays silent off-device, where
// there is no context and WriteStatus is a no-op.
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Microsoft.Maui.Platform;

/// <summary>Writes the "[maui] openharmony build ..." startup line once per process.</summary>
internal static class OpenHarmonyBuildBanner
{
    private const string HostLibrary = "libopenharmonyhost.so";

    // Same probe OpenHarmonyAccessibility reads for its own "accessibility provider status=N"
    // line; kept local because that slice does not expose the value. 0 = not attached yet,
    // 1 = attached, 2/3/4 = the failure states the native host reports.
    [DllImport(HostLibrary, EntryPoint = "ohos_host_accessibility_provider_status")]
    private static extern int ProviderStatusNative();

    private static int s_logged;

    [ModuleInitializer]
    internal static void Initialize()
    {
        try
        {
            // Raised when the host context is available (also for late subscribers), so the
            // banner lands after "bridge attached" and never races the context handover.
            Microsoft.OpenHarmony.Hosting.OpenHarmonyBridge.Initialized += _ => LogOnce();
        }
        catch (Exception)
        {
            // The hosting assembly (or the host library) is absent: nothing to log into.
        }
    }

    /// <summary>Writes the banner once per process; safe to call again and never throws.</summary>
    internal static void LogOnce()
    {
        if (Interlocked.Exchange(ref s_logged, 1) != 0)
        {
            return;
        }
        try
        {
            Assembly assembly = typeof(OpenHarmonyBuildBanner).Assembly;
            string version = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                ?? assembly.GetName().Version?.ToString()
                ?? "unknown";
            string abi = RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant();
            Microsoft.OpenHarmony.Hosting.OpenHarmonyBridge.WriteStatus(
                $"[maui] openharmony build {version} abi={abi} provider={ProviderStatus()}");
        }
        catch (Exception)
        {
            // Diagnostics must never take the application down.
        }
    }

    private static string ProviderStatus()
    {
        try
        {
            return ProviderStatusNative().ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
        catch (Exception)
        {
            // No host library: report it in the line instead of failing the banner.
            return "n/a";
        }
    }
}
