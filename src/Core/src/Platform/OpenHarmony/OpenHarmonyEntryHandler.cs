// Entry handler for OpenHarmony: displays text/placeholder and focuses on tap. Text input
// itself needs a soft keyboard, which the ArkTS shell must provide (next step).
using Microsoft.Maui.Graphics;
using Microsoft.Maui.Handlers;
using Microsoft.Maui;
using Microsoft.OpenHarmony.Hosting;

namespace Microsoft.Maui.Platform;

public sealed class OpenHarmonyEntryHandler : OpenHarmonyViewHandler<IEntry>
{
    public static readonly IPropertyMapper<IEntry, OpenHarmonyEntryHandler> Mapper =
        new PropertyMapper<IEntry, OpenHarmonyEntryHandler>(ViewMapper)
        {
            [nameof(IText.Text)] = MapText,
            [nameof(ITextStyle.TextColor)] = MapTextColor,
            [nameof(ITextStyle.Font)] = MapFont,
            [nameof(IPlaceholder.Placeholder)] = MapPlaceholder,
            [nameof(ITextInput.CursorPosition)] = MapCursor,
            [nameof(ITextInput.SelectionLength)] = MapCursor,
        };

    public OpenHarmonyEntryHandler() : base(Mapper) { }

    protected override OpenHarmonyView CreatePlatformView()
    {
        var view = new OpenHarmonyView { IsTextEntry = true, Background = Colors.DimGray };
        view.Tap = () =>
        {
            SetFocus(true);
            OpenHarmonyBridge.RequestTextInput(true);
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

    private void OnTextSubmitted()
    {
        // The return key completes the focused entry (raises User's Completed handler).
        if (PlatformView.IsFocused && VirtualView is IEntry entry)
        {
            entry.Completed();
        }
    }

    private void OnTextInput(string text)
    {
        // Only the focused entry consumes the shell's text. IText.Text is read-only, so the
        // assignment goes through the Controls types, which raises TextChanged/Completed.
        if (!PlatformView.IsFocused)
        {
            return;
        }
        PlatformView.Text = text;
        switch (VirtualView)
        {
            case Microsoft.Maui.Controls.Entry entry:
                entry.Text = text;
                break;
            case Microsoft.Maui.Controls.Editor editor:
                editor.Text = text;
                break;
        }
    }

    // The platform view is the source of truth; the virtual view is updated through the
    // read-only IsFocused bindable key so app code sees IsFocused/Focused/Unfocused.
    private void SetFocus(bool focused)
    {
        PlatformView.IsFocused = focused;
        OpenHarmonyBridge.RequestTextInput(focused);
        if (VirtualView is Microsoft.Maui.Controls.VisualElement element &&
            element.IsFocused != focused)
        {
            element.SetValue(Microsoft.Maui.Controls.VisualElement.IsFocusedPropertyKey, focused);
        }
    }

    public override Size GetDesiredSize(double widthConstraint, double heightConstraint)
    {
        (float width, float height) = OpenHarmonyLabelHandler.MeasureText(
            ((IText)VirtualView!).Text ?? ((IPlaceholder)VirtualView!).Placeholder ?? string.Empty,
            PlatformView?.FontSize > 0 ? PlatformView.FontSize : 14f);
        return new Size(Math.Min(width + 24, widthConstraint), height + 20);
    }

    // MAUI's VisualElement.Focus() invokes a platform command expecting a result.
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

    public static void MapText(OpenHarmonyEntryHandler handler, IEntry entry)
        => handler.PlatformView.Text = ((IText)entry).Text;

    public static void MapTextColor(OpenHarmonyEntryHandler handler, IEntry entry)
        => handler.PlatformView.TextColor = (entry as ITextStyle)?.TextColor ?? Colors.White;

    public static void MapFont(OpenHarmonyEntryHandler handler, IEntry entry)
        => handler.PlatformView.FontSize = (float)((entry as ITextStyle) is { } style ? style.Font.Size : 14.0);

    public static void MapPlaceholder(OpenHarmonyEntryHandler handler, IEntry entry)
        => handler.PlatformView.Placeholder = ((IPlaceholder)entry).Placeholder;

    public static void MapCursor(OpenHarmonyEntryHandler handler, IEntry entry)
    {
        handler.PlatformView.CursorPosition = ((ITextInput)entry).CursorPosition;
        handler.PlatformView.SelectionLength = ((ITextInput)entry).SelectionLength;
    }
}
