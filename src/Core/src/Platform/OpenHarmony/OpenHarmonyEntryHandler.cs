// Entry handler for OpenHarmony: displays text/placeholder and focuses on tap. Text input
// itself needs a soft keyboard, which the ArkTS shell must provide (next step).
using Microsoft.Maui.Graphics;
using Microsoft.Maui.Handlers;
using Microsoft.Maui;

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
        };

    public OpenHarmonyEntryHandler() : base(Mapper) { }

    protected override OpenHarmonyView CreatePlatformView()
    {
        var view = new OpenHarmonyView { IsTextEntry = true, Background = Colors.DimGray };
        view.Tap = () =>
        {
            IsFocused = true;
            ((IView)VirtualView!).Focus();
        };
        return view;
    }

    private bool IsFocused
    {
        get => PlatformView.IsFocused;
        set => PlatformView.IsFocused = value;
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
                PlatformView.IsFocused = true;
                if (args is RetrievePlatformValueRequest<bool> focusRequest)
                {
                    focusRequest.SetResult(true);
                }
                return;
            case "Unfocus":
                PlatformView.IsFocused = false;
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
}
