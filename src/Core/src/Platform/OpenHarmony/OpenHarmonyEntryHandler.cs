// Entry handler for OpenHarmony: displays text/placeholder and focuses on tap. Text input
// itself needs a soft keyboard, which the ArkTS shell provides through the text-input sink.
//
// Focus/keyboard contract of this slice:
// - Text views (Entry/Editor/SearchBar/alert prompts) own the focus bridge: a tap or a MAUI
//   Focus()/Unfocus() sets the platform IsFocused and asks OpenHarmonyBridge.RequestTextInput,
//   which on device drives the input-method NDK and otherwise reaches the shell's
//   registerTextInputSink handler (focusControl.requestFocus('ohos_dotnet_input') /
//   ('ohos_dotnet_surface') plus caretPosition(0)). The dedicated focus bridge below
//   (OpenHarmonyFocusBridge -> ohos_host_request_focus -> the shell's registerFocusSink handler
//   -> focusControl.requestFocus(id)) additionally names the ArkUI target, so the ArkUI focus
//   follows the managed Focus()/Unfocus() even when the NDK keyboard path handled the request
//   and the shell's text-input sink never ran.
// - Non-text views have no equivalent: the shell's focus sink is reachable through
//   OpenHarmonyFocusBridge, but a Button/Label VisualElement.Focus() reaches its own handler and
//   OpenHarmonyViewHandler does not override Invoke, so nothing routes it to the bridge. A
//   shared Invoke("Focus"/"Unfocus") override keyed on the platform view (requesting focus for
//   'ohos_dotnet_surface') is the missing piece; until then a non-text focus is not expressible.
// - Hardware key events: the shell's XComponent onKeyEvent forwards
//   host.keyEvent(keyCode, eventType) -> the host's ohos_host_key_event, which reaches the
//   callback registered with ohos_host_register_key_event (void (*)(int keyCode, int eventType);
//   0 = down, 1 = up, the ArkUI KeyType encoding). MAUI rc.1 exposes no key surface (no
//   IKeyListener/KeyDown/KeyUp), so the hosting bridge keeps that callback as its documented
//   internal surface and no slice handler consumes keys yet.
using Microsoft.Maui.Graphics;
using Microsoft.Maui.Handlers;
using Microsoft.Maui;
using Microsoft.OpenHarmony.Hosting;
using System.Runtime.InteropServices;

namespace Microsoft.Maui.Platform;

/// <summary>
/// Focus requests for this slice: the ArkTS shell's registerFocusSink handler hands ArkUI focus
/// to an element id (focusControl.requestFocus). The text handlers call it on focus/unfocus so
/// the shell's input control (or the .NET surface) owns the ArkUI focus even when the input
/// method NDK path handled the keyboard request and the shell's text-input sink never ran.
/// Off-device (no host library) and with an older host library every call answers false.
/// </summary>
internal static partial class OpenHarmonyFocusBridge
{
    private const string HostLibrary = "libopenharmonyhost.so";

    /// <summary>The shell's hidden text input (see the shell's TextInput id).</summary>
    private const string TextInputId = "ohos_dotnet_input";

    /// <summary>The XComponent the managed content is rendered into.</summary>
    private const string SurfaceId = "ohos_dotnet_surface";

    [LibraryImport(HostLibrary, EntryPoint = "ohos_host_request_focus", StringMarshalling = StringMarshalling.Utf8)]
    private static partial int RequestFocusNative(string targetId);

    private static bool s_available = true;

    /// <summary>Hands ArkUI focus to the shell's text input; false when the bridge is absent.</summary>
    public static bool RequestTextInputFocus() => Request(TextInputId);

    /// <summary>Hands ArkUI focus back to the managed surface; false when the bridge is absent.</summary>
    public static bool RequestSurfaceFocus() => Request(SurfaceId);

    private static bool Request(string targetId)
    {
        if (!s_available)
        {
            return false;
        }
        try
        {
            return RequestFocusNative(targetId) == 0;
        }
        catch (DllNotFoundException)
        {
            // No native host (tests, desktop): focus requests are a no-op.
            s_available = false;
            return false;
        }
        catch (EntryPointNotFoundException)
        {
            // An older host library without the focus export.
            s_available = false;
            return false;
        }
    }
}

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
            [nameof(ITextInput.MaxLength)] = MapMaxLength,
            [nameof(ITextInput.IsReadOnly)] = MapIsReadOnly,
            [nameof(ITextInput.Keyboard)] = MapKeyboard,
            [nameof(IEntry.IsPassword)] = MapIsPassword,
            [nameof(IEntry.ReturnType)] = MapReturnType,
            [nameof(IEntry.ClearButtonVisibility)] = MapClearButtonVisibility,
            [nameof(IPlaceholder.PlaceholderColor)] = MapPlaceholderColor,
            [nameof(ITextAlignment.HorizontalTextAlignment)] = MapHorizontalTextAlignment,
        };

    public OpenHarmonyEntryHandler() : base(Mapper) { }

    protected override OpenHarmonyView CreatePlatformView()
    {
        var view = new OpenHarmonyView { IsTextEntry = true, Background = Colors.DimGray };
        view.Tap = () =>
        {
            SetFocus(true);
        };
        view.ClearText = () => ClearEntryText(view);
        return view;
    }

    /// <summary>Clears the entry through the virtual view (the same path typing uses).</summary>
    private void ClearEntryText(OpenHarmonyView view)
    {
        view.Text = string.Empty;
        view.CursorPosition = 0;
        view.SelectionLength = 0;
        if (VirtualView is Microsoft.Maui.Controls.Entry entry)
        {
            entry.Text = string.Empty;
        }
        OpenHarmonyBridge.SetKeyboardText(string.Empty);
        OpenHarmonyBridge.SetKeyboardCaret(0);
        OpenHarmonyBridge.RequestRedraw();
    }

    // MULTIWINDOW-L M4: the per-window port this entry registers while connected. The global
    // subscriptions below stay (the primary window's single-input path is unchanged); the port
    // carries the child window's own text from the shell's tagged subwindow channel.
    private OpenHarmonyWindowTextPort? _windowTextPort;

    protected override void ConnectHandler(OpenHarmonyView platformView)
    {
        base.ConnectHandler(platformView);
        OpenHarmonyBridge.TextInput += OnTextInput;
        OpenHarmonyBridge.TextSubmitted += OnTextSubmitted;
        OpenHarmonyBridge.TextComposition += OnTextComposition;
        if (VirtualView is IView ownerView)
        {
            _windowTextPort = new OpenHarmonyWindowTextPort(
                ownerView, OnTextInput, OnTextComposition, OnTextSubmitted);
            OpenHarmonyWindowInputRouter.RegisterText(_windowTextPort);
        }
    }

    protected override void DisconnectHandler(OpenHarmonyView platformView)
    {
        OpenHarmonyBridge.TextInput -= OnTextInput;
        OpenHarmonyBridge.TextSubmitted -= OnTextSubmitted;
        OpenHarmonyBridge.TextComposition -= OnTextComposition;
        if (_windowTextPort is not null)
        {
            OpenHarmonyWindowInputRouter.UnregisterText(_windowTextPort);
            _windowTextPort = null;
        }
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
        if (!PlatformView.IsFocused || PlatformView.IsReadOnly)
        {
            return;
        }
        text = ApplyInputRules(text);
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

    /// <summary>
    /// The input rules the mapped properties impose: the Keyboard kind's character set and
    /// MaxLength. The shell always delivers the whole text, so both rules are applied to the
    /// incoming value before it reaches the platform/virtual views.
    /// </summary>
    private string ApplyInputRules(string text)
    {
        text = OpenHarmonyView.ApplyKeyboardFilter(PlatformView.Keyboard, text);
        return OpenHarmonyView.ClampToMaxLength(text, PlatformView.MaxLength);
    }

    private int _compositionLength;

    /// <summary>
    /// IME preedit from the shell's input (PreviewText.value/offset): the platform view draws it
    /// at the caret (underline/highlight) until the commit arrives as an empty composition plus
    /// the new text through <see cref="OnTextInput"/>.
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
            // Compose committed or cancelled: the caret lands after the committed text.
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

    // The platform view is the source of truth; the virtual view is updated through the
    // read-only IsFocused bindable key so app code sees IsFocused/Focused/Unfocused.
    private void SetFocus(bool focused)
    {
        PlatformView.IsFocused = focused;
        // A read-only entry focuses (focus ring, selection) but never opens the soft keyboard;
        // ArkUI focus stays on the managed surface so typing cannot reach the shell's input.
        bool editable = !PlatformView.IsReadOnly;
        string windowId = OpenHarmonyMauiAppHost.ResolveWindowId(VirtualView as IView);
        if (windowId != OpenHarmonyWindowSurface.PrimaryWindowId)
        {
            // MULTIWINDOW-L M4: a secondary window's input lives in the child page
            // (pages/SubWindow.ets), so the process-global keyboard/focus exports would target
            // the primary window's input. Ask the child page to focus its own input and seed it
            // with the managed text (focus) or return focus to its surface (unfocus).
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
            // The shell's input caret is what the IME composes at; keep it on the managed caret
            // (the mapper refreshes it on every CursorPosition change).
            OpenHarmonyBridge.SetKeyboardCaret(PlatformView.CursorPosition);
            OpenHarmonyBridge.RequestTextInput(true);
            // Name the ArkUI target as well (the shell's registerFocusSink handler): with the
            // input method NDK path RequestTextInput returns before the shell's text-input sink
            // runs, so this is what hands ArkUI focus to the input.
            OpenHarmonyFocusBridge.RequestTextInputFocus();
        }
        else if (windowId == OpenHarmonyWindowSurface.PrimaryWindowId)
        {
            OpenHarmonyBridge.RequestTextInput(false);
            // Name the ArkUI target as well: this is what hands ArkUI focus back to the surface.
            OpenHarmonyFocusBridge.RequestSurfaceFocus();
        }
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
        // M4: only the primary window's hidden input tracks the managed caret; a secondary
        // window's caret stays in its child page (seeded on focus through RequestTextFocus).
        if (handler.PlatformView.IsFocused &&
            OpenHarmonyMauiAppHost.ResolveWindowId(entry as IView) == OpenHarmonyWindowSurface.PrimaryWindowId)
        {
            OpenHarmonyBridge.SetKeyboardCaret(handler.PlatformView.CursorPosition);
        }
    }

    // InputView mapping (T1) --------------------------------------------------------------
    // Every mapper writes the platform-view state and requests a frame: the properties change
    // the drawing (password bullets, placeholder colour, alignment, clear button), the focus
    // path (read-only) or the accepted input (max length, keyboard kind).

    public static void MapMaxLength(OpenHarmonyEntryHandler handler, IEntry entry)
    {
        handler.PlatformView.MaxLength = ((ITextInput)entry).MaxLength;
        OpenHarmonyBridge.RequestRedraw();
    }

    public static void MapIsReadOnly(OpenHarmonyEntryHandler handler, IEntry entry)
    {
        handler.PlatformView.IsReadOnly = ((ITextInput)entry).IsReadOnly;
        if (handler.PlatformView.IsReadOnly && handler.PlatformView.IsFocused)
        {
            // Losing editability closes the keyboard the entry may already have open.
            handler.SetFocus(true);
        }
        OpenHarmonyBridge.RequestRedraw();
    }

    public static void MapKeyboard(OpenHarmonyEntryHandler handler, IEntry entry)
    {
        handler.PlatformView.Keyboard = ((ITextInput)entry).Keyboard;
        OpenHarmonyBridge.RequestRedraw();
    }

    public static void MapIsPassword(OpenHarmonyEntryHandler handler, IEntry entry)
    {
        handler.PlatformView.IsPassword = ((IEntry)entry).IsPassword;
        OpenHarmonyBridge.RequestRedraw();
    }

    public static void MapReturnType(OpenHarmonyEntryHandler handler, IEntry entry)
    {
        handler.PlatformView.ReturnType = ((IEntry)entry).ReturnType;
        OpenHarmonyBridge.RequestRedraw();
    }

    public static void MapClearButtonVisibility(OpenHarmonyEntryHandler handler, IEntry entry)
    {
        handler.PlatformView.ClearButtonVisibility = ((IEntry)entry).ClearButtonVisibility;
        OpenHarmonyBridge.RequestRedraw();
    }

    public static void MapPlaceholderColor(OpenHarmonyEntryHandler handler, IEntry entry)
    {
        handler.PlatformView.PlaceholderColor = ((IPlaceholder)entry).PlaceholderColor;
        OpenHarmonyBridge.RequestRedraw();
    }

    public static void MapHorizontalTextAlignment(OpenHarmonyEntryHandler handler, IEntry entry)
    {
        handler.PlatformView.HorizontalTextAlignment = ((ITextAlignment)entry).HorizontalTextAlignment;
        OpenHarmonyBridge.RequestRedraw();
    }
}
