// IAppActions for OpenHarmony: the installed SDK (26.0.0.18) exposes no dynamic shortcut setter.
// Launcher shortcuts on OpenHarmony are static metadata in the HAP's module.json5 "shortcuts"
// and there is no counterpart to Android's ShortcutManager / iOS' UIApplicationShortcutItem
// (the launcher entry APIs - @ohos.bundle.launcherBundleManager, @ohos.app.ability.wantAgent -
// cannot install or update app shortcuts at runtime). The implementation therefore degrades
// safely instead of throwing: IsSupported is false, GetAsync returns an empty set, SetAsync is
// a documented no-op, and AppActionActivated never fires. It is registered in UseOpenHarmony so
// AppActions.Current resolves this class instead of the Essentials reference-assembly
// "not implemented" exception.
using Microsoft.Maui.ApplicationModel;

namespace Microsoft.Maui.Platform;

/// <summary>
/// App actions (runtime shortcuts) for OpenHarmony. The SDK has no dynamic shortcut setter, so
/// this implementation reports unsupported and answers empty rather than pretending to work.
/// </summary>
public sealed class OpenHarmonyAppActions : IAppActions
{
    /// <summary>False: no runtime shortcut API exists in this SDK (see the file header).</summary>
    public bool IsSupported => false;

    /// <summary>Empty: the shell owns shortcuts and the slice cannot read them back.</summary>
    public Task<IEnumerable<AppAction>> GetAsync()
        => Task.FromResult<IEnumerable<AppAction>>(Array.Empty<AppAction>());

    /// <summary>Documented no-op: no dynamic shortcut setter exists to receive the actions.</summary>
    public Task SetAsync(IEnumerable<AppAction> actions) => Task.CompletedTask;

    /// <summary>Never raised: nothing can activate a runtime shortcut without the setter.</summary>
    public event EventHandler<AppActionEventArgs>? AppActionActivated
    {
        add { }
        remove { }
    }
}
