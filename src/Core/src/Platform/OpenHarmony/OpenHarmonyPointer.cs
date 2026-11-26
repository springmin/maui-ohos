// Pointer gestures for OpenHarmony. PointerGestureRecognizer's dispatch members are internal to
// Controls while this slice compiles as its own assembly, so they are invoked through cached
// reflection (the same guarded pattern used for the Essentials entry points). Failures are
// swallowed so a pointer dispatch can never take the frame loop down.
using System.Reflection;
using Microsoft.Maui;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Graphics;

namespace Microsoft.Maui.Platform;

internal static class OpenHarmonyPointer
{
    internal enum Kind
    {
        Entered,
        Exited,
        Moved,
        Pressed,
        Released,
    }

    private static readonly Dictionary<string, MethodInfo?> Methods = new();

    public static bool HasPointer(IView view)
    {
        // An indexed loop, not OfType().Any(): the hit-test walk asks this once per node and the
        // LINQ pipeline allocated an iterator on every call.
        if (view is not View controlsView)
        {
            return false;
        }
        IList<IGestureRecognizer> recognizers = controlsView.GestureRecognizers;
        for (int i = 0; i < recognizers.Count; i++)
        {
            if (recognizers[i] is PointerGestureRecognizer)
            {
                return true;
            }
        }
        return false;
    }

    public static bool Dispatch(IView view, Kind kind, float x, float y)
    {
        if (view is not View controlsView)
        {
            return false;
        }
        bool handled = false;
        foreach (IGestureRecognizer recognizer in controlsView.GestureRecognizers)
        {
            if (recognizer is not PointerGestureRecognizer pointer || MethodFor(pointer, kind) is not { } method)
            {
                continue;
            }
            try
            {
                method.Invoke(pointer, new object?[] { controlsView, new Func<IElement?, Point?>(_ => new Point(x, y)), null, ButtonsMask.Primary });
                handled = true;
            }
            catch (Exception)
            {
                // Ignored: the internal signature changed or the target refused the dispatch.
            }
        }
        return handled;
    }

    private static MethodInfo? MethodFor(PointerGestureRecognizer pointer, Kind kind)
    {
        string name = kind switch
        {
            Kind.Entered => "SendPointerEntered",
            Kind.Exited => "SendPointerExited",
            Kind.Moved => "SendPointerMoved",
            Kind.Pressed => "SendPointerPressed",
            _ => "SendPointerReleased",
        };
        if (Methods.TryGetValue(name, out MethodInfo? cached))
        {
            return cached;
        }
        MethodInfo? found = pointer.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
        Methods[name] = found;
        return found;
    }
}
