// SemanticScreenReader (MAUI Essentials) for OpenHarmony.
//
// Microsoft.Maui.Essentials ships SemanticScreenReaderImplementation compiled for this TFM as a
// portable stub: calling Announce throws NotImplementedInReferenceAssemblyException. The platform
// implementations are per-TFM partials (see src/Essentials/src/SemanticScreenReader
// /SemanticScreenReader.tizen.cs in this fork), so the slice installs its own ISemanticScreenReader
// and the static SemanticScreenReader.Default getter serves it.
//
// The install surface is the private static field SemanticScreenReader.defaultImplementation: the
// rc.1 SemanticScreenReader.SetDefault(ISemanticScreenReader) is internal, so it cannot be called
// from this assembly. That is the same reflection pattern OpenHarmonyFlashlight uses for
// Flashlight.Default. The field is also written by the Default getter's lazy
// `defaultImplementation ??= new SemanticScreenReaderImplementation()`, so the module initializer
// replaces it before any app code can resolve Default.
//
// The announcement itself is OpenHarmonyAccessibility.Announce: it goes out as the ArkUI
// ANNOUNCE_FOR_ACCESSIBILITY event through the existing host entry
// ohos_host_accessibility_send_event. That entry carries the event kind only - the text needs the
// host export int ohos_host_accessibility_announce(const char* text) documented on
// OpenHarmonyAccessibility.Announce (the managed side already keeps LastAnnouncement for it).
// Without libopenharmonyhost.so the call degrades silently, so Announce never throws.
using System.Reflection;
using System.Runtime.CompilerServices;
using Microsoft.Maui.Accessibility;

namespace Microsoft.Maui.Platform;

/// <summary>
/// The slice's <see cref="ISemanticScreenReader"/>: every announcement is routed through the
/// accessibility provider's event path (<see cref="OpenHarmonyAccessibility.Announce"/>).
/// </summary>
public sealed class OpenHarmonySemanticScreenReader : ISemanticScreenReader
{
    /// <summary>The singleton installed as <see cref="SemanticScreenReader.Default"/>.</summary>
    public static readonly OpenHarmonySemanticScreenReader Instance = new();

    private OpenHarmonySemanticScreenReader()
    {
    }

    /// <inheritdoc />
    public void Announce(string text) => OpenHarmonyAccessibility.Announce(text);

    private static FieldInfo? s_defaultField;

    /// <summary>True when <see cref="SemanticScreenReader.Default"/> serves this implementation.</summary>
    public static bool IsInstalled
    {
        get
        {
            try
            {
                return ReferenceEquals(s_defaultField?.GetValue(null), Instance);
            }
            catch (Exception)
            {
                return false;
            }
        }
    }

    /// <summary>Installs this implementation as the MAUI Essentials screen reader default.</summary>
    public static void InstallDefault()
    {
        try
        {
            const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
            FieldInfo? field = s_defaultField ??= typeof(SemanticScreenReader).GetField("defaultImplementation", flags);
            // Fallback: any settable static field that can hold the implementation (the entry
            // point is get-only, so the backing field is the only direct install surface; the
            // internal SetDefault is the method fallback below).
            field ??= Array.Find(typeof(SemanticScreenReader).GetFields(flags),
                candidate => candidate.FieldType.IsInstanceOfType(Instance) && !candidate.IsInitOnly);
            if (field is not null)
            {
                field.SetValue(null, Instance);
                return;
            }
            MethodInfo? setDefault = typeof(SemanticScreenReader).GetMethod("SetDefault", flags);
            setDefault?.Invoke(null, new object?[] { Instance });
        }
        catch (Exception)
        {
            // The reference default stays in place when the entry point cannot be replaced; the
            // announcement then throws NotImplementedInReferenceAssemblyException, exactly like
            // before the slice installed anything.
        }
    }

    [ModuleInitializer]
    internal static void Initialize() => InstallDefault();
}
