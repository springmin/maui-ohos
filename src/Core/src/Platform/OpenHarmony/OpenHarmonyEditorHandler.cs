// Editor handler for OpenHarmony: multi-line text entry (reuses the soft-keyboard bridge and the
// Entry drawing, clipped to the frame).
using Microsoft.Maui.Graphics;
using Microsoft.Maui.Handlers;
using Microsoft.OpenHarmony.Hosting;

namespace Microsoft.Maui.Platform;

public sealed class OpenHarmonyEditorHandler : OpenHarmonyViewHandler<IEditor>
{
    public static readonly IPropertyMapper<IEditor, OpenHarmonyEditorHandler> Mapper =
        new PropertyMapper<IEditor, OpenHarmonyEditorHandler>(ViewMapper)
        {
            [nameof(IText.Text)] = MapText,
            [nameof(ITextStyle.TextColor)] = MapTextColor,
            [nameof(ITextStyle.Font)] = MapFont,
            [nameof(IPlaceholder.Placeholder)] = MapPlaceholder,
        };

    public OpenHarmonyEditorHandler() : base(Mapper) { }

    protected override OpenHarmonyView CreatePlatformView()
    {
        var view = new OpenHarmonyView { IsTextEntry = true, Background = Colors.DimGray, FontSize = 22 };
        view.Tap = () =>
        {
            SetFocus(true);
        };
        return view;
    }

    protected override void ConnectHandler(OpenHarmonyView platformView)
    {
        base.ConnectHandler(platformView);
        OpenHarmonyBridge.TextInput += OnTextInput;
        OpenHarmonyBridge.TextSubmitted += OnTextSubmitted;
    }

    protected override void DisconnectHandler(OpenHarmonyView platformView)
    {
        OpenHarmonyBridge.TextInput -= OnTextInput;
        OpenHarmonyBridge.TextSubmitted -= OnTextSubmitted;
        base.DisconnectHandler(platformView);
    }

    public override void Invoke(string command, object? args)
    {
        switch (command)
        {
            case "Focus":
                SetFocus(true);
                if (args is RetrievePlatformValueRequest<bool> focusRequest)
                {
                    focusRequest.SetResult(true);
                }
                return;
            case "Unfocus":
                SetFocus(false);
                if (args is RetrievePlatformValueRequest<bool> unfocusRequest)
                {
                    unfocusRequest.SetResult(true);
                }
                return;
            default:
                base.Invoke(command, args);
                return;
        }
    }

    private void SetFocus(bool focused)
    {
        PlatformView.IsFocused = focused;
        if (focused)
        {
            OpenHarmonyBridge.SetKeyboardText(PlatformView.Text);
        }
        OpenHarmonyBridge.RequestTextInput(focused);
        if (VirtualView is Microsoft.Maui.Controls.VisualElement element && element.IsFocused != focused)
        {
            element.SetValue(Microsoft.Maui.Controls.VisualElement.IsFocusedPropertyKey, focused);
        }
    }

    private void OnTextSubmitted()
    {
        if (PlatformView.IsFocused && VirtualView is IEditor editor)
        {
            ((IEntry)editor).Completed();
        }
    }

    private void OnTextInput(string text)
    {
        if (!PlatformView.IsFocused)
        {
            return;
        }
        PlatformView.Text = text;
        if (VirtualView is Microsoft.Maui.Controls.Editor editor)
        {
            editor.Text = text;
        }
    }

    public override Size GetDesiredSize(double widthConstraint, double heightConstraint)
        => new(Math.Min(widthConstraint, widthConstraint), Math.Min(120, heightConstraint));

    public static void MapText(OpenHarmonyEditorHandler handler, IEditor editor)
        => handler.PlatformView.Text = ((IText)editor).Text;

    public static void MapTextColor(OpenHarmonyEditorHandler handler, IEditor editor)
        => handler.PlatformView.TextColor = (editor as ITextStyle)?.TextColor ?? Colors.White;

    public static void MapFont(OpenHarmonyEditorHandler handler, IEditor editor)
        => handler.PlatformView.FontSize = (float)((editor as ITextStyle) is { } style ? style.Font.Size : 22.0);

    public static void MapPlaceholder(OpenHarmonyEditorHandler handler, IEditor editor)
        => handler.PlatformView.Placeholder = ((IPlaceholder)editor).Placeholder;
}
