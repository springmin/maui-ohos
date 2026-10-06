// SearchBar handler for OpenHarmony: an entry-shaped control with a search icon; text flows
// through the same soft-keyboard bridge as Entry.
using Microsoft.Maui.Graphics;
using Microsoft.Maui.Handlers;
using Microsoft.OpenHarmony.Hosting;

namespace Microsoft.Maui.Platform;

public sealed class OpenHarmonySearchBarHandler : OpenHarmonyViewHandler<ISearchBar>
{
    public static readonly IPropertyMapper<ISearchBar, OpenHarmonySearchBarHandler> Mapper =
        new PropertyMapper<ISearchBar, OpenHarmonySearchBarHandler>(ViewMapper)
        {
            [nameof(IText.Text)] = MapText,
            [nameof(ITextStyle.TextColor)] = MapTextColor,
            [nameof(ITextStyle.Font)] = MapFont,
            [nameof(IPlaceholder.Placeholder)] = MapPlaceholder,
            [nameof(ITextInput.MaxLength)] = MapMaxLength,
            [nameof(ITextInput.IsReadOnly)] = MapIsReadOnly,
            [nameof(ITextInput.Keyboard)] = MapKeyboard,
            [nameof(ISearchBar.ReturnType)] = MapReturnType,
            [nameof(IPlaceholder.PlaceholderColor)] = MapPlaceholderColor,
            [nameof(ITextAlignment.HorizontalTextAlignment)] = MapHorizontalTextAlignment,
        };

    public OpenHarmonySearchBarHandler() : base(Mapper) { }

    protected override OpenHarmonyView CreatePlatformView()
    {
        var view = new OpenHarmonyView { IsTextEntry = true, Background = Colors.DimGray, Placeholder = "Search" };
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
        if (VirtualView is IView ownerView)
        {
            _windowTextPort = new OpenHarmonyWindowTextPort(
                ownerView, OnTextInput, OnSearchComposition, OnTextSubmitted);
            OpenHarmonyWindowInputRouter.RegisterText(_windowTextPort);
        }
    }

    // MULTIWINDOW-L M4: the per-window port of this search bar (see OpenHarmonyEntryHandler).
    private OpenHarmonyWindowTextPort? _windowTextPort;

    protected override void DisconnectHandler(OpenHarmonyView platformView)
    {
        OpenHarmonyBridge.TextInput -= OnTextInput;
        OpenHarmonyBridge.TextSubmitted -= OnTextSubmitted;
        if (_windowTextPort is not null)
        {
            OpenHarmonyWindowInputRouter.UnregisterText(_windowTextPort);
            _windowTextPort = null;
        }
        base.DisconnectHandler(platformView);
    }

    // The search bar has no composition state of its own (the shared keyboard bridge handles
    // it for the primary window); the port keeps the contract without drawing a preedit.
    private void OnSearchComposition(string value, int offset)
    {
        _ = value;
        _ = offset;
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
        // Read-only search bars focus but never open the soft keyboard (same rule as Entry).
        bool editable = !PlatformView.IsReadOnly;
        string windowId = OpenHarmonyMauiAppHost.ResolveWindowId(VirtualView as IView);
        if (windowId != OpenHarmonyWindowSurface.PrimaryWindowId)
        {
            // MULTIWINDOW-L M4: route the secondary window's focus/keyboard to its child page.
            OpenHarmonySubWindow.RequestTextFocus(windowId, focused && editable,
                PlatformView.CursorPosition, PlatformView.Text);
        }
        else
        {
            OpenHarmonyBridge.RequestTextInput(focused && editable);
            // Same ArkUI focus naming as the Entry handler (see OpenHarmonyFocusBridge).
            if (focused && editable)
            {
                OpenHarmonyFocusBridge.RequestTextInputFocus();
            }
            else
            {
                OpenHarmonyFocusBridge.RequestSurfaceFocus();
            }
        }
        if (VirtualView is Microsoft.Maui.Controls.VisualElement element && element.IsFocused != focused)
        {
            element.SetValue(Microsoft.Maui.Controls.VisualElement.IsFocusedPropertyKey, focused);
        }
    }

    private void OnTextSubmitted()
    {
        if (PlatformView.IsFocused && VirtualView is ISearchBar searchBar)
        {
            searchBar.SearchButtonPressed();
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
        if (VirtualView is Microsoft.Maui.Controls.SearchBar searchBar)
        {
            searchBar.Text = text;
        }
    }

    public override Size GetDesiredSize(double widthConstraint, double heightConstraint)
        => new(Math.Min(widthConstraint, widthConstraint), Math.Min(48, heightConstraint));

    public static void MapText(OpenHarmonySearchBarHandler handler, ISearchBar searchBar)
        => handler.PlatformView.Text = ((IText)searchBar).Text;

    public static void MapTextColor(OpenHarmonySearchBarHandler handler, ISearchBar searchBar)
        => handler.PlatformView.TextColor = (searchBar as ITextStyle)?.TextColor ?? Colors.White;

    public static void MapFont(OpenHarmonySearchBarHandler handler, ISearchBar searchBar)
        => handler.PlatformView.FontSize = (float)((searchBar as ITextStyle) is { } style ? style.Font.Size : 14.0);

    public static void MapPlaceholder(OpenHarmonySearchBarHandler handler, ISearchBar searchBar)
        => handler.PlatformView.Placeholder = ((IPlaceholder)searchBar).Placeholder;

    // InputView mapping (T1): the SearchBar half of the Entry mapper additions (a SearchBar has
    // no password/clear-button surface; ReturnType drives the shell search key).

    public static void MapMaxLength(OpenHarmonySearchBarHandler handler, ISearchBar searchBar)
    {
        handler.PlatformView.MaxLength = ((ITextInput)searchBar).MaxLength;
        OpenHarmonyBridge.RequestRedraw();
    }

    public static void MapIsReadOnly(OpenHarmonySearchBarHandler handler, ISearchBar searchBar)
    {
        handler.PlatformView.IsReadOnly = ((ITextInput)searchBar).IsReadOnly;
        if (handler.PlatformView.IsReadOnly && handler.PlatformView.IsFocused)
        {
            handler.SetFocus(true);
        }
        OpenHarmonyBridge.RequestRedraw();
    }

    public static void MapKeyboard(OpenHarmonySearchBarHandler handler, ISearchBar searchBar)
    {
        handler.PlatformView.Keyboard = ((ITextInput)searchBar).Keyboard;
        OpenHarmonyBridge.RequestRedraw();
    }

    public static void MapReturnType(OpenHarmonySearchBarHandler handler, ISearchBar searchBar)
    {
        handler.PlatformView.ReturnType = searchBar.ReturnType;
        OpenHarmonyBridge.RequestRedraw();
    }

    public static void MapPlaceholderColor(OpenHarmonySearchBarHandler handler, ISearchBar searchBar)
    {
        handler.PlatformView.PlaceholderColor = ((IPlaceholder)searchBar).PlaceholderColor;
        OpenHarmonyBridge.RequestRedraw();
    }

    public static void MapHorizontalTextAlignment(OpenHarmonySearchBarHandler handler, ISearchBar searchBar)
    {
        handler.PlatformView.HorizontalTextAlignment = ((ITextAlignment)searchBar).HorizontalTextAlignment;
        OpenHarmonyBridge.RequestRedraw();
    }
}
