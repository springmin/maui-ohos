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
        if (focused)
        {
            OpenHarmonyBridge.SetKeyboardText(PlatformView.Text);
        }
        OpenHarmonyBridge.RequestTextInput(focused);
        // Name the ArkUI target as well (the shell's registerFocusSink handler): with the input
        // method NDK path RequestTextInput returns before the shell's text-input sink runs, so
        // this is what hands ArkUI focus to the input (focus) or back to the surface (unfocus).
        if (focused)
        {
            OpenHarmonyFocusBridge.RequestTextInputFocus();
        }
        else
        {
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
    }
}
