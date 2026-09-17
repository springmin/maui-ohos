// MAUI application host for OpenHarmony: creates the app window through IApplication, keeps
// the visual tree's handlers connected to our platform handlers, and drives rendering from
// the platform contract (surface, touch, frame ticks, lifecycle).
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Maui.Hosting;
using Microsoft.OpenHarmony.Hosting;

namespace Microsoft.Maui.Platform;

public sealed class OpenHarmonyMauiAppHost
{
    private readonly MauiContext _context;
    private readonly OpenHarmonyWindowRenderer _renderer;
    private readonly OpenHarmonyWindowSurface _surface;
    private IWindow? _window;
    private int _width;
    private int _height;
    private bool _dirty = true;

    public OpenHarmonyMauiAppHost(IServiceProvider services)
    {
        _context = new MauiContext(services);
        _renderer = services.GetRequiredService<OpenHarmonyWindowRenderer>();
        _surface = services.GetRequiredService<OpenHarmonyWindowSurface>();

        OpenHarmonyBridge.SurfaceChanged += info =>
        {
            if (info.State == OpenHarmonySurfaceState.Created && info.Width > 0 && info.Height > 0)
            {
                Arrange(info.Width, info.Height);
                _dirty = true;
                Render();
            }
        };

        OpenHarmonyBridge.Touch += args =>
        {
            bool down = args.Action == OpenHarmonyTouchAction.Down;
            bool up = args.Action == OpenHarmonyTouchAction.Up;
            if (HandleTouch(down, up, args.X, args.Y))
            {
                _dirty = true;
            }
        };

        OpenHarmonyBridge.Frame += _ =>
        {
            if (_dirty)
            {
                _dirty = false;
                Render();
            }
        };

        OpenHarmonyBridge.LifecycleChanged += e =>
        {
            // MAUI's IWindow lifecycle expects a platform window handler; until this slice
            // provides one the events are best-effort (an exception must not stop the app).
            try
            {
                switch (e)
                {
                    case OpenHarmonyLifecycleEvent.Create:
                        _window?.Activated();
                        break;
                    case OpenHarmonyLifecycleEvent.Foreground:
                        _window?.Resumed();
                        break;
                    case OpenHarmonyLifecycleEvent.Background:
                        _window?.Stopped();
                        break;
                    case OpenHarmonyLifecycleEvent.Destroy:
                        _window?.Destroying();
                        break;
                }
                OpenHarmonyBridge.WriteStatus($"[maui] lifecycle {e} (window={_window?.GetType().Name})");
            }
            catch (Exception ex)
            {
                OpenHarmonyBridge.WriteStatus($"[maui] lifecycle {e} ignored: {ex.GetType().Name}: {ex.Message}");
            }
        };
    }

    public IWindow? Window => _window;

    /// <summary>Creates the application window and connects the visual tree's handlers.</summary>
    public void Run(IApplication application)
    {
        ArgumentNullException.ThrowIfNull(application);
        _window = application.CreateWindow(null) ?? application.Windows.FirstOrDefault();
        if (_window is null)
        {
            OpenHarmonyBridge.WriteStatus("[maui] application did not create a window");
            return;
        }
        ConnectTree(_window);
        ConnectTree(_window.Content);
        OpenHarmonyBridge.WriteStatus($"[maui] window created ({_window.GetType().Name}), content={_window.Content?.GetType().Name}");
        // The platform's Create lifecycle event activates the window.
        _dirty = true;
    }

    /// <summary>Measures/arranges the current window content for the given surface size.</summary>
    public void Arrange(int width, int height)
    {
        _width = width;
        _height = height;
        if (_window?.Content is not IView content)
        {
            return;
        }
        // MAUI measures/arranges through handlers; Page/ContentView have no platform handler
        // in this slice, so arrange the first descendant that has one.
        IView? root = FindArrangableRoot(content);
        if (root is not null)
        {
            root.Measure(width, height);
            root.Arrange(new Rect(0, 0, width, height));
        }
    }

    private static IView? FindArrangableRoot(IView view)
    {
        if (view.Handler is not null)
        {
            return view;
        }
        if (view is ILayout layout)
        {
            foreach (IView child in layout)
            {
                IView? found = FindArrangableRoot(child);
                if (found is not null)
                {
                    return found;
                }
            }
        }
        if (view is IContentView contentView && contentView.PresentedContent is IView presented)
        {
            return FindArrangableRoot(presented);
        }
        return null;
    }

    public bool Render()
    {
        if (_window?.Content is not IView content || _width <= 0)
        {
            return false;
        }
        return _renderer.Render(content, _width, _height);
    }

    public bool HandleTouch(bool down, bool up, float x, float y)
        => _window?.Content is IView content && _renderer.HandleTouch(content, down, up, x, y);

    public string Describe() => _window?.Content is IView content ? _renderer.Describe(content) : "(no window content)";

    private IElementHandler? HandlerFor(Type handlerType)
        => Activator.CreateInstance(handlerType) as IElementHandler;

    private Type? FindSliceHandlerType(Type viewType)
    {
        foreach (Type iface in viewType.GetInterfaces())
        {
            Type? candidate = _context.Handlers.GetHandlerType(iface);
            if (candidate is not null && candidate.Assembly == typeof(OpenHarmonyMauiAppHost).Assembly)
            {
                return candidate;
            }
        }
        Type? direct = _context.Handlers.GetHandlerType(viewType);
        return direct is not null && direct.Assembly == typeof(OpenHarmonyMauiAppHost).Assembly ? direct : null;
    }

    /// <summary>Connects handlers for a view and its descendants (idempotent).</summary>
    private void ConnectTree(IElement? element)
    {
        if (element is null)
        {
            return;
        }
        if (element is IView view && view.Handler is null)
        {
            // Prefer this slice's interface-registered handlers (ILabel/IButton/ILayout) over
            // MAUI's platform-partial concrete registrations, which have no platform view here.
            Type? handlerType = FindSliceHandlerType(view.GetType());
            if (handlerType is not null)
            {
                if (HandlerFor(handlerType) is IViewHandler viewHandler)
                {
                    viewHandler.SetMauiContext(_context);
                    view.Handler = viewHandler;
                }
            }
        }
        if (element is ILayout layout)
        {
            foreach (IView child in layout)
            {
                ConnectTree(child);
            }
        }
        else if (element is IContentView contentView)
        {
            ConnectTree(contentView.PresentedContent);
        }
    }
}
