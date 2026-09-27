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
            [nameof(ITextInput.CursorPosition)] = MapCursor,
            [nameof(ITextInput.SelectionLength)] = MapCursor,
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
        OpenHarmonyBridge.TextComposition += OnTextComposition;
    }

    protected override void DisconnectHandler(OpenHarmonyView platformView)
    {
        OpenHarmonyBridge.TextInput -= OnTextInput;
        OpenHarmonyBridge.TextSubmitted -= OnTextSubmitted;
        OpenHarmonyBridge.TextComposition -= OnTextComposition;
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
            if (PlatformView.CursorPosition < 0)
            {
                PlatformView.CursorPosition = PlatformView.Text?.Length ?? 0;
            }
            // Same caret/shell-input sync as the Entry handler (IME composition offset).
            OpenHarmonyBridge.SetKeyboardCaret(PlatformView.CursorPosition);
        }
        OpenHarmonyBridge.RequestTextInput(focused);
        // Same ArkUI focus naming as the Entry handler (see OpenHarmonyFocusBridge).
        if (focused)
        {
            OpenHarmonyFocusBridge.RequestTextInputFocus();
        }
        else
        {
            OpenHarmonyFocusBridge.RequestSurfaceFocus();
        }
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

    private int _compositionLength;

    /// <summary>
    /// IME preedit (PreviewText.value/offset): drawn at the caret until the shell commits the
    /// text (empty composition + the new text through <see cref="OnTextInput"/>).
    /// </summary>
    private void OnTextComposition(string value, int offset)
    {
        if (!PlatformView.IsFocused)
        {
            return;
        }
        PlatformView.CompositionText = value;
        PlatformView.CompositionOffset = offset;
        if (string.IsNullOrEmpty(value))
        {
            int caret = offset + _compositionLength;
            _compositionLength = 0;
            PlatformView.CursorPosition = caret;
            PlatformView.SelectionLength = 0;
            if (VirtualView is Microsoft.Maui.Controls.InputView input)
            {
                input.CursorPosition = caret;
                input.SelectionLength = 0;
            }
            OpenHarmonyBridge.SetKeyboardCaret(caret);
        }
        else
        {
            _compositionLength = value.Length;
        }
        OpenHarmonyBridge.RequestRedraw();
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

    public static void MapCursor(OpenHarmonyEditorHandler handler, IEditor editor)
    {
        handler.PlatformView.CursorPosition = ((ITextInput)editor).CursorPosition;
        handler.PlatformView.SelectionLength = ((ITextInput)editor).SelectionLength;
        if (handler.PlatformView.IsFocused)
        {
            OpenHarmonyBridge.SetKeyboardCaret(handler.PlatformView.CursorPosition);
        }
    }
}
