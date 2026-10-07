// IAlertManager for OpenHarmony: DisplayAlert/DisplayActionSheet/DisplayPromptAsync are shown by
// the compositor as an overlay (see OpenHarmonyAlertHost) instead of a native dialog.
using Microsoft.Maui.Controls;
using Microsoft.Maui.Controls.Internals;
using Microsoft.Maui.Controls.Platform;
using Microsoft.OpenHarmony.Hosting;

namespace Microsoft.Maui.Platform;

public sealed class OpenHarmonyAlertManager : IAlertManager
{
    public void Subscribe()
    {
    }

    public void Unsubscribe()
    {
    }

    public void RequestPageBusy(Page page, bool isBusy)
    {
    }

    public void RequestAlert(Page page, AlertArguments arguments)
    {
        OpenHarmonyAlertHost.Show(new OpenHarmonyAlertState
        {
            // MULTIWINDOW-L M4: the alert belongs to the window that asked; only that window
            // draws and hit-tests the dialog. MULTIWINDOW-L3 M3: each window keeps its own
            // alert, so a dialog opened while another window's is open neither replaces nor
            // gates it.
            WindowId = OpenHarmonyMauiAppHost.ResolveWindowId(page),
            Title = arguments.Title,
            Message = arguments.Message,
            Accept = arguments.Accept,
            Cancel = arguments.Cancel,
            Complete = accepted =>
            {
                arguments.SetResult(accepted);
                OpenHarmonyBridge.RequestRedraw();
            },
        });
    }

    public void RequestActionSheet(Page page, ActionSheetArguments arguments)
    {
        var options = new List<string>();
        if (arguments.Buttons is { } buttons)
        {
            options.AddRange(buttons);
        }
        if (!string.IsNullOrEmpty(arguments.Cancel))
        {
            options.Add(arguments.Cancel);
        }
        OpenHarmonyAlertHost.Show(new OpenHarmonyAlertState
        {
            WindowId = OpenHarmonyMauiAppHost.ResolveWindowId(page),
            Kind = OpenHarmonyAlertKind.ActionSheet,
            Title = arguments.Title,
            Message = null,
            Accept = null,
            Cancel = null,
            Options = options,
            Complete = _ => { },
            CompleteOption = chosen =>
            {
                arguments.SetResult(chosen);
                OpenHarmonyBridge.RequestRedraw();
            },
        });
    }

    public void RequestPrompt(Page page, PromptArguments arguments)
    {
        // The prompt edits text through the same keyboard bridge as Entry/Editor. M3: the
        // keyboard is raised in the prompt's own window - the primary window's global hidden
        // input keeps the historical path, a secondary window's hidden input is focused through
        // the per-window text-focus command (page side: pages/SubWindow.ets).
        string windowId = OpenHarmonyMauiAppHost.ResolveWindowId(page);
        OpenHarmonyAlertState? state = null;
        state = new OpenHarmonyAlertState
        {
            WindowId = windowId,
            Kind = OpenHarmonyAlertKind.Prompt,
            Title = arguments.Title,
            Message = arguments.Message,
            Accept = arguments.Accept,
            Cancel = arguments.Cancel,
            Complete = accepted =>
            {
                // The completion reads this alert's own text (the M3 per-window slot), never the
                // primary window's Current: a dialog in another window can no longer supply it.
                arguments.SetResult(accepted ? state!.PromptText : null);
                OpenHarmonyBridge.RequestRedraw();
                if (windowId != OpenHarmonyWindowSurface.PrimaryWindowId)
                {
                    // The prompt is done; drop the child window's IME with it.
                    OpenHarmonySubWindow.RequestTextFocus(windowId, false, 0);
                }
            },
            CompleteText = value => arguments.SetResult(value),
        };
        OpenHarmonyAlertHost.Show(state);
        if (windowId == OpenHarmonyWindowSurface.PrimaryWindowId)
        {
            OpenHarmonyBridge.SetKeyboardText(string.Empty);
            OpenHarmonyBridge.RequestTextInput(true);
        }
        else
        {
            // Focus the child's hidden input (show=1) so the soft keyboard follows the prompt's
            // window; a shell without the child session answers false and the dialog degrades to
            // buttons-only, exactly like the primary path without a keyboard bridge.
            OpenHarmonySubWindow.RequestTextFocus(windowId, true, 0, string.Empty);
        }
    }
}
