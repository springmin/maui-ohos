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
        OpenHarmonyBridge.RequestTextInput(focused);
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
        if (!PlatformView.IsFocused)
        {
            return;
        }
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
}
