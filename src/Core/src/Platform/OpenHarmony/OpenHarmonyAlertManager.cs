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
        // The prompt edits text through the same keyboard bridge as Entry/Editor.
        OpenHarmonyAlertHost.Show(new OpenHarmonyAlertState
        {
            Kind = OpenHarmonyAlertKind.Prompt,
            Title = arguments.Title,
            Message = arguments.Message,
            Accept = arguments.Accept,
            Cancel = arguments.Cancel,
            Complete = accepted =>
            {
                arguments.SetResult(accepted ? OpenHarmonyAlertHost.Current?.PromptText ?? string.Empty : null);
                OpenHarmonyBridge.RequestRedraw();
            },
            CompleteText = value => arguments.SetResult(value),
        });
        OpenHarmonyBridge.SetKeyboardText(string.Empty);
        OpenHarmonyBridge.RequestTextInput(true);
    }
}
