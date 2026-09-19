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
        // Keystore results arrive from the ArkTS sink through the bridge.
        OpenHarmonyBridge.KeystoreResult += (requestId, rc, data) => OpenHarmonyKeystore.Complete(requestId, rc, data);
        OpenHarmonyBridge.PickerResult += (requestId, rc, name, data) => OpenHarmonyPickerClient.Complete(requestId, rc, name, data);
        OpenHarmonyBridge.WebEvent += (state, url) => OpenHarmonyWebViewHandler.OnPageEvent(state, url);
        // While a prompt overlay is open the keyboard text edits it instead of an Entry.
        OpenHarmonyBridge.TextInput += text => { if (OpenHarmonyAlertHost.Current?.Kind == OpenHarmonyAlertKind.Prompt) { OpenHarmonyAlertHost.PromptAppend(text); } };
        OpenHarmonyBridge.TextSubmitted += () => { if (OpenHarmonyAlertHost.Current?.Kind == OpenHarmonyAlertKind.Prompt) { OpenHarmonyAlertHost.Hide(); OpenHarmonyAlertHost.Current?.Complete(true); } };
        // Bindable objects created outside the service scope (TabbedPage/MultiPage, ...) resolve
        // their dispatcher through this provider.
        Microsoft.Maui.Dispatching.DispatcherProvider.SetCurrent(
            new OpenHarmonyDispatcherProvider(services.GetRequiredService<Microsoft.Maui.Dispatching.IDispatcher>()));
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
            bool handled = args.Action switch
            {
                OpenHarmonyTouchAction.Down => HandleTouch(true, false, args.X, args.Y),
                OpenHarmonyTouchAction.Up => HandleTouch(false, true, args.X, args.Y),
                OpenHarmonyTouchAction.Move => _renderer.HandleMove(args.X, args.Y),
                _ => false,
            };
            if (handled)
            {
                _dirty = true;
            }
        };

        OpenHarmonyBridge.RedrawRequested += () => _dirty = true;

        OpenHarmonyBridge.Frame += _ =>
        {
            // Activity indicators keep animating: advance the shared angle and redraw.
            if (_renderer.HasAnimations(_window?.Content as IView))
            {
                OpenHarmonyView.AnimationAngle = (OpenHarmonyView.AnimationAngle + 24f) % 360f;
                _dirty = true;
            }
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

    /// <summary>The view to render: the top modal page when one is pushed, else the window content.</summary>
    private IView? RootView
    {
        get
        {
            if (_window is Microsoft.Maui.Controls.Window window &&
                window.Navigation.ModalStack.Count > 0 &&
                window.Navigation.ModalStack[^1] is IView modal)
            {
                return modal;
            }
            return _window?.Content as IView;
        }
    }

    /// <summary>Creates the application window and connects the visual tree's handlers.</summary>
    public void Run(IApplication application)
    {
        ArgumentNullException.ThrowIfNull(application);
        // The ArkTS shell may report the device theme before the application exists; apply it now.
        OpenHarmonyTheme.Attach(application as Microsoft.Maui.Controls.Application);
        _window = application.CreateWindow(null) ?? application.Windows.FirstOrDefault();
        if (_window is null)
        {
            OpenHarmonyBridge.WriteStatus("[maui] application did not create a window");
            return;
        }
        OpenHarmonyHandlerConnector.Context = _context;
        OpenHarmonyHandlerConnector.ConnectTree(_window);
        OpenHarmonyHandlerConnector.ConnectTree(_window.Content);
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
        OpenHarmonyHandlerConnector.ConnectTree(content);
        var bounds = new Rect(0, 0, width, height);
        if (OpenHarmonyBridge.TryGetAvoidArea(out int avoidTop, out int avoidBottom, out int avoidLeft, out int avoidRight))
        {
            var inset = new Rect(avoidLeft, avoidTop,
                Math.Max(1, width - avoidLeft - avoidRight), Math.Max(1, height - avoidTop - avoidBottom));
            if (inset.Width > 1 && inset.Height > 1)
            {
                bounds = inset;
            }
        }
        // Pages have no platform layout of their own, so the content chain is arranged directly
        // (navigation bars are subtracted on the way down).
        OpenHarmonyContentArrange.Arrange(content, bounds);
        IView? root = FindArrangableRoot(content);
        if (root is not null && !ReferenceEquals(root, content))
        {
            root.Measure(width, height);
            root.Arrange(bounds);
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

    private bool _pinchWired;

    public bool HandleTouch(bool down, bool up, float x, float y)
    {
        if (!_pinchWired)
        {
            _pinchWired = true;
            OpenHarmonyBridge.RegisterPinchListener();
            OpenHarmonyBridge.Pinch += OnPinch;
            OpenHarmonyAccessibility.SetActionHandler((nodeId, action) => HandleAccessibilityAction(nodeId, action));
        }
        return RootView is IView content && _renderer.HandleTouch(content, down, up, x, y);
    }

    /// <summary>
    /// Routes an accessibility action back into the normal input path: CLICK simulates a tap at the
    /// node centre, which is exactly what a real touch would do.
    /// </summary>
    public bool HandleAccessibilityAction(int nodeId, int action)
    {
        if (action != 0x10 || !OpenHarmonyAccessibility.TryFindNode(nodeId, out OpenHarmonyAccessibilityNode node))
        {
            return false;
        }
        if (RootView is not IView content)
        {
            return false;
        }
        float x = (float)(node.Bounds.X + node.Bounds.Width / 2);
        float y = (float)(node.Bounds.Y + node.Bounds.Height / 2);
        _renderer.HandleTouch(content, true, true, x, y);
        return true;
    }

    private void OnPinch(int phase, double scale, float x, float y)
    {
        if (RootView is IView content)
        {
            _renderer.HandlePinch(content, phase, scale, x, y);
        }
    }

    /// <summary>Handles a touch move (drag scrolling).</summary>
    public bool HandleMove(float x, float y)
    {
        if (RootView is IView content)
        {
            _renderer.HandlePointerMove(content, x, y);
        }
        return _renderer.HandleMove(x, y);
    }

    public string Describe() => RootView is IView content ? _renderer.Describe(content) : "(no window content)";

}
