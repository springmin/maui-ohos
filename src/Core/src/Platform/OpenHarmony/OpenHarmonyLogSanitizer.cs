// One-line status-log URL sanitizer (B7), shared by the launcher/browser (Core) and the WebView
// handler (Controls). It lives in Core because the Essentials launchers only depend on Core
// contracts; the WebView handler keeps its historical internal entry points as wrappers.
using System;

namespace Microsoft.Maui.Platform;

/// <summary>URL sanitizing for the one-line status channel.</summary>
internal static class OpenHarmonyLogSanitizer
{
    /// <summary>Upper bound for one logged URL (B7).</summary>
    internal const int MaxLoggedUrlLength = 2048;

    /// <summary>
    /// Makes a URL safe for the one-line status log (B7): drops the query and the fragment so
    /// tokens a page put there do not land in dotnet-status.txt, flattens control characters so
    /// one event cannot forge extra lines, and truncates to
    /// <see cref="MaxLoggedUrlLength"/> characters.
    /// </summary>
    internal static string SanitizeUrl(string url)
    {
        if (string.IsNullOrEmpty(url))
        {
            return string.Empty;
        }
        int cut = url.Length;
        int query = url.IndexOf('?', StringComparison.Ordinal);
        int fragment = url.IndexOf('#', StringComparison.Ordinal);
        if (query >= 0 && query < cut)
        {
            cut = query;
        }
        if (fragment >= 0 && fragment < cut)
        {
            cut = fragment;
        }
        string trimmed = cut == url.Length ? url : url.Substring(0, cut);
        if (trimmed.Length > MaxLoggedUrlLength)
        {
            trimmed = string.Concat(trimmed.AsSpan(0, MaxLoggedUrlLength - 3), "...");
        }
        char[]? flattened = null;
        for (int i = 0; i < trimmed.Length; i++)
        {
            if (char.IsControl(trimmed[i]))
            {
                flattened ??= trimmed.ToCharArray();
                flattened[i] = ' ';
            }
        }
        return flattened is null ? trimmed : new string(flattened);
    }
}
