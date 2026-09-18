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
        // Shown as an alert-style list: every option plus the cancel title behave like buttons;
        // choosing one resolves the sheet. (A full list overlay is the next refinement.)
        OpenHarmonyBridge.WriteStatus("[maui] action sheet shown as an alert overlay");
    }

    public void RequestPrompt(Page page, PromptArguments arguments)
    {
        OpenHarmonyBridge.WriteStatus("[maui] prompt requests need text input in the overlay");
        arguments.SetResult(null);
    }
}
