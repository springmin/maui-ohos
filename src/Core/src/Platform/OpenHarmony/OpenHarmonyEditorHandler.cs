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
            [nameof(ITextInput.MaxLength)] = MapMaxLength,
            [nameof(ITextInput.IsReadOnly)] = MapIsReadOnly,
            [nameof(ITextInput.Keyboard)] = MapKeyboard,
            [nameof(IPlaceholder.PlaceholderColor)] = MapPlaceholderColor,
            [nameof(ITextAlignment.HorizontalTextAlignment)] = MapHorizontalTextAlignment,
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

    // MULTIWINDOW-L M4: the per-window port of this editor (see OpenHarmonyEntryHandler).
    private OpenHarmonyWindowTextPort? _windowTextPort;

    protected override void ConnectHandler(OpenHarmonyView platformView)
    {
        base.ConnectHandler(platformView);
        OpenHarmonyBridge.TextInput += OnGlobalTextInput;
        OpenHarmonyBridge.TextSubmitted += OnGlobalTextSubmitted;
        OpenHarmonyBridge.TextComposition += OnGlobalTextComposition;
        if (VirtualView is IView ownerView)
        {
            _windowTextPort = new OpenHarmonyWindowTextPort(
                ownerView, OnTextInput, OnTextComposition, OnTextSubmitted);
            OpenHarmonyWindowInputRouter.RegisterText(_windowTextPort);
        }
    }

    protected override void DisconnectHandler(OpenHarmonyView platformView)
    {
        OpenHarmonyBridge.TextInput -= OnGlobalTextInput;
        OpenHarmonyBridge.TextSubmitted -= OnGlobalTextSubmitted;
        OpenHarmonyBridge.TextComposition -= OnGlobalTextComposition;
        if (_windowTextPort is not null)
        {
            OpenHarmonyWindowInputRouter.UnregisterText(_windowTextPort);
            _windowTextPort = null;
        }
        base.DisconnectHandler(platformView);
    }

    // SEC-SCAN-5c: the global bridge belongs to the primary window; the tagged port above is a
    // secondary window's only text source (see OpenHarmonyEntryHandler).
    private bool IsPrimaryWindow => OpenHarmonyWindowInputRouter.IsPrimaryWindow(VirtualView as IView);

    private void OnGlobalTextSubmitted()
    {
        if (IsPrimaryWindow)
        {
            OnTextSubmitted();
        }
    }

    private void OnGlobalTextInput(string text)
    {
        if (IsPrimaryWindow)
        {
            OnTextInput(text);
        }
    }

    private void OnGlobalTextComposition(string value, int offset)
    {
        if (IsPrimaryWindow)
        {
            OnTextComposition(value, offset);
        }
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
        // Read-only editors focus but never open the soft keyboard (same rule as Entry).
        bool editable = !PlatformView.IsReadOnly;
        string windowId = OpenHarmonyMauiAppHost.ResolveWindowId(VirtualView as IView);
        if (windowId != OpenHarmonyWindowSurface.PrimaryWindowId)
        {
            // MULTIWINDOW-L M4: route the secondary window's focus/keyboard to its child page.
            if (focused && editable && PlatformView.CursorPosition < 0)
            {
                PlatformView.CursorPosition = PlatformView.Text?.Length ?? 0;
            }
            OpenHarmonySubWindow.RequestTextFocus(windowId, focused && editable,
                PlatformView.CursorPosition, PlatformView.Text);
        }
        else if (focused && editable)
        {
            OpenHarmonyBridge.SetKeyboardText(PlatformView.Text);
            if (PlatformView.CursorPosition < 0)
            {
                PlatformView.CursorPosition = PlatformView.Text?.Length ?? 0;
            }
            // Same caret/shell-input sync as the Entry handler (IME composition offset).
            OpenHarmonyBridge.SetKeyboardCaret(PlatformView.CursorPosition);
            OpenHarmonyBridge.RequestTextInput(true);
            OpenHarmonyFocusBridge.RequestTextInputFocus();
        }
        else
        {
            OpenHarmonyBridge.RequestTextInput(false);
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
        if (!PlatformView.IsFocused || PlatformView.IsReadOnly)
        {
            return;
        }
        text = OpenHarmonyView.ClampToMaxLength(
            OpenHarmonyView.ApplyKeyboardFilter(PlatformView.Keyboard, text), PlatformView.MaxLength);
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

    // InputView mapping (T1): the Editor half of the Entry mapper additions (an Editor has no
    // password/clear-button/return-type surface).

    public static void MapMaxLength(OpenHarmonyEditorHandler handler, IEditor editor)
    {
        handler.PlatformView.MaxLength = ((ITextInput)editor).MaxLength;
        OpenHarmonyBridge.RequestRedraw();
    }

    public static void MapIsReadOnly(OpenHarmonyEditorHandler handler, IEditor editor)
    {
        handler.PlatformView.IsReadOnly = ((ITextInput)editor).IsReadOnly;
        if (handler.PlatformView.IsReadOnly && handler.PlatformView.IsFocused)
        {
            handler.SetFocus(true);
        }
        OpenHarmonyBridge.RequestRedraw();
    }

    public static void MapKeyboard(OpenHarmonyEditorHandler handler, IEditor editor)
    {
        handler.PlatformView.Keyboard = ((ITextInput)editor).Keyboard;
        OpenHarmonyBridge.RequestRedraw();
    }

    public static void MapPlaceholderColor(OpenHarmonyEditorHandler handler, IEditor editor)
    {
        handler.PlatformView.PlaceholderColor = ((IPlaceholder)editor).PlaceholderColor;
        OpenHarmonyBridge.RequestRedraw();
    }

    public static void MapHorizontalTextAlignment(OpenHarmonyEditorHandler handler, IEditor editor)
    {
        handler.PlatformView.HorizontalTextAlignment = ((ITextAlignment)editor).HorizontalTextAlignment;
        OpenHarmonyBridge.RequestRedraw();
    }
}
