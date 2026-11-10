using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System;
using System.Collections.Generic;

namespace Microsoft.Maui.Platform;

/// <summary>Shared one-time status reporting for the platform views' honest degradations.</summary>
internal static class OpenHarmonyStatus
{
    private static readonly object s_sync = new();
    private static readonly HashSet<string> s_logged = new(StringComparer.Ordinal);

    /// <summary>Writes a status line once per key (the slice's off-device visible log channel).</summary>
    public static void Once(string key, string message)
    {
        lock (s_sync)
        {
            if (!s_logged.Add(key))
            {
                return;
            }
        }
        Microsoft.OpenHarmony.Hosting.OpenHarmonyBridge.WriteStatus("[maui] " + message);
    }

    /// <summary>
    /// Reports an exception that was caught at a native -> managed callback boundary. An
    /// exception escaping such a callback unwinds into the host's native frame, where CoreCLR
    /// treats it as fatal (the a11y action callback's contract), so every boundary that can run
    /// application code catches and reports instead. Exception text can carry page- or
    /// native-controlled characters and is often attacker-influenced (a URL, a JSON snippet),
    /// so the line is flattened to one status line and capped; it is logged once per
    /// boundary/exception-type so a page that keeps triggering an app failure cannot flood
    /// dotnet-status.txt.
    /// </summary>
    public static void NativeCallbackFailed(string boundary, Exception error)
    {
        Once(
            "native-callback:" + boundary + ":" + error.GetType().Name,
            $"{boundary} callback failed: {error.GetType().Name}: {Flatten(error.Message)}");
    }

    /// <summary>One log-safe line: control characters become spaces and the text is capped (B7-style).</summary>
    private static string Flatten(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }
        const int MaxMessageLength = 600;
        int length = Math.Min(value.Length, MaxMessageLength);
        char[]? flattened = null;
        for (int i = 0; i < length; i++)
        {
            if (char.IsControl(value[i]))
            {
                flattened ??= value.ToCharArray();
                flattened[i] = ' ';
            }
        }
        string result = flattened is null ? value.Substring(0, length) : new string(flattened, 0, length);
        return value.Length > MaxMessageLength ? result + "..." : result;
    }
}
