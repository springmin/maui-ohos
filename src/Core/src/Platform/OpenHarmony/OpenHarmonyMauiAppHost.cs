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
    private bool _created;
    private bool _activated;

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
                        // MAUI's window lifecycle starts with Created (the platform window now
                        // exists); the platform event carries the activation, so raise Created
                        // first and only once.
                        EnsureWindowCreated();
                        EnsureWindowActivated();
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
        // Application.Handler comes first so Application.Windows and the application-level
        // commands (OpenWindow/CloseWindow/ActivateWindow/Quit) are live before any window.
        AttachApplicationHandler(application);
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
        // The platform's Create lifecycle event activates the window (and may arrive before Run
        // when the shell is fast); Created must precede it either way.
        EnsureWindowCreated();
        _dirty = true;
    }

    /// <summary>
    /// Raises <see cref="IWindow.Created"/> exactly once, as soon as the window exists. MAUI's
    /// window throws on a second Created, and the platform lifecycle event carries only the
    /// activation, so this is the single entry point that starts the MAUI window lifecycle
    /// (called from <see cref="Run"/> and from the platform Create event, whichever is first).
    /// </summary>
    private void EnsureWindowCreated()
    {
        if (_created || _window is null)
        {
            return;
        }
        _created = true;
        _window.Created();
    }

    /// <summary>
    /// Raises <see cref="IWindow.Activated"/> exactly once per window: the platform Create event
    /// carries the activation, and a window adopted later (through
    /// <see cref="OpenHarmonyApplicationHandler"/>'s OpenWindow) is activated directly, so the
    /// guard keeps a repeated platform event from raising Activated twice.
    /// </summary>
    private void EnsureWindowActivated()
    {
        if (_activated || _window is null)
        {
            return;
        }
        _activated = true;
        _window.Activated();
    }

    /// <summary>
    /// Attaches the OpenHarmony <see cref="IApplication"/> handler before any window is created,
    /// so <c>Application.Handler</c> is non-null and the application-level commands are live from
    /// the start. An application that already has a handler (a custom bootstrap) keeps it.
    /// </summary>
    private void AttachApplicationHandler(IApplication application)
    {
        if (application.Handler is OpenHarmonyApplicationHandler attached)
        {
            attached.Host = this;
            return;
        }
        if (application.Handler is not null)
        {
            return;
        }
        var handler = new OpenHarmonyApplicationHandler();
        handler.SetMauiContext(_context);
        application.Handler = handler;
        handler.Host = this;
        OpenHarmonyBridge.WriteStatus("[maui] application handler attached");
    }

    /// <summary>
    /// Makes <paramref name="window"/> the host's single live window: connects the slice handlers,
    /// arranges it for the last reported surface size, raises Created and (the platform is already
    /// foregrounded when a window is adopted after startup) Activated, and starts rendering.
    /// Returns false when another window is already live.
    /// </summary>
    internal bool TryAdoptWindow(IWindow window)
    {
        ArgumentNullException.ThrowIfNull(window);
        if (_window is not null)
        {
            // One live window: the same window is already adopted, a different one cannot be shown.
            return ReferenceEquals(_window, window);
        }
        _window = window;
        _created = false;
        _activated = false;
        OpenHarmonyHandlerConnector.Context = _context;
        OpenHarmonyHandlerConnector.ConnectTree(_window);
        OpenHarmonyHandlerConnector.ConnectTree(_window.Content);
        OpenHarmonyBridge.WriteStatus(
            $"[maui] window adopted ({_window.GetType().Name}), content={_window.Content?.GetType().Name}");
        EnsureWindowCreated();
        EnsureWindowActivated();
        if (_width > 0)
        {
            Arrange(_width, _height);
        }
        _dirty = true;
        return true;
    }

    /// <summary>
    /// Drops the host's reference to a window the application handler closed. Destroying already
    /// removed it from Application.Windows and disconnected its handler; the shell's window stays
    /// open, so the next OpenWindow can adopt a window on the same surface.
    /// </summary>
    internal void NotifyWindowClosed(IWindow window)
    {
        if (!ReferenceEquals(_window, window))
        {
            return;
        }
        _window = null;
        _created = false;
        _activated = false;
        _dirty = true;
        OpenHarmonyBridge.WriteStatus("[maui] window closed; the host has no live window");
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
        // The shell reports the surface size; mirror it onto the virtual window so Window.Width/
        // Height (and SizeChanged) are real values, like the other platforms' window handlers.
        _window.FrameChanged(bounds);
        // The window's avoid area is applied per page/content view through SafeAreaEdges (see
        // OpenHarmonySafeAreaArrange); the surface itself is arranged edge to edge.
        Thickness insets = OpenHarmonySafeArea.GetWindowInsets();
        OpenHarmonySafeAreaArrange.Arrange(content, bounds, bounds, insets);
        IView? root = FindArrangableRoot(content);
        if (root is not null && !ReferenceEquals(root, content))
        {
            OpenHarmonySafeAreaArrange.Arrange(root, bounds, bounds, insets);
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
    /// Executes an accessibility action routed from the provider. It maps exactly - and only - the
    /// actions <see cref="OpenHarmonyAccessibility.ActionsFor"/> advertises: CLICK simulates a tap
    /// at the node centre, SCROLL_FORWARD/BACKWARD move an IScrollView or step an ISlider, and the
    /// textInput actions work on the node's Entry/Editor (COPY/CUT write the clipboard, PASTE
    /// reads it back at the caret, SELECT_TEXT selects the whole text).
    /// </summary>
    public bool HandleAccessibilityAction(int nodeId, int action)
    {
        if (!OpenHarmonyAccessibility.TryFindNode(nodeId, out OpenHarmonyAccessibilityNode node))
        {
            return false;
        }
        OpenHarmonyAccessibility.TryFindView(nodeId, out IView? target);
        switch ((OpenHarmonyAccessibilityAction)action)
        {
            case OpenHarmonyAccessibilityAction.Click:
                return ClickAccessibilityNode(node);
            case OpenHarmonyAccessibilityAction.ScrollForward:
                return ScrollAccessibilityNode(target, forward: true);
            case OpenHarmonyAccessibilityAction.ScrollBackward:
                return ScrollAccessibilityNode(target, forward: false);
            case OpenHarmonyAccessibilityAction.Copy:
                return CopyAccessibilityNode(target, node, cut: false);
            case OpenHarmonyAccessibilityAction.Cut:
                return CopyAccessibilityNode(target, node, cut: true);
            case OpenHarmonyAccessibilityAction.Paste:
                return PasteAccessibilityNode(target);
            case OpenHarmonyAccessibilityAction.SelectText:
                return SelectAllAccessibilityNode(target);
            default:
                // Never advertised: SET_TEXT/SET_CURSOR_POSITION need a value payload the listener
                // does not carry, and this slice has no long-press path. Stale requests stay unhandled.
                return false;
        }
    }

    /// <summary>CLICK simulates a tap at the node centre, exactly what a real touch would do.</summary>
    private bool ClickAccessibilityNode(OpenHarmonyAccessibilityNode node)
    {
        if (RootView is not IView content)
        {
            return false;
        }
        float x = (float)(node.Bounds.X + node.Bounds.Width / 2);
        float y = (float)(node.Bounds.Y + node.Bounds.Height / 2);
        // Two phases like a real touch: the renderer's press tracking only fires a button's Tap on
        // the release phase, so a single down+up call would only set Pressed and never click.
        _renderer.HandleTouch(content, true, false, x, y);
        _renderer.HandleTouch(content, false, true, x, y);
        _dirty = true;
        return true;
    }

    /// <summary>Scrolls an IScrollView by most of a viewport, or steps an ISlider by 10% of its range.</summary>
    private bool ScrollAccessibilityNode(IView? target, bool forward)
    {
        if (target?.Handler?.PlatformView is not OpenHarmonyView platform)
        {
            return false;
        }
        if (platform.IsScrollView)
        {
            bool horizontal = target is IScrollView { Orientation: ScrollOrientation.Horizontal };
            float viewport = horizontal ? platform.Frame.Width : platform.Frame.Height;
            float content = horizontal ? platform.ScrollContentWidth : platform.ScrollContentHeight;
            float current = horizontal ? platform.ScrollOffsetX : platform.ScrollOffsetY;
            float step = Math.Max(40f, viewport * 0.8f);
            float limit = Math.Max(0f, content - viewport);
            float offset = Math.Clamp(current + (forward ? step : -step), 0f, limit);
            if (Math.Abs(offset - current) > 0.01f)
            {
                if (horizontal)
                {
                    platform.ScrollOffsetX = offset;
                    if (target is IScrollView scrollViewX)
                    {
                        scrollViewX.HorizontalOffset = offset;
                    }
                }
                else
                {
                    platform.ScrollOffsetY = offset;
                    // Keep the virtual view in sync the same way a real drag does.
                    if (target is IScrollView scrollViewY)
                    {
                        scrollViewY.VerticalOffset = offset;
                    }
                }
                platform.ScrollOffsetChanged?.Invoke();
            }
            _dirty = true;
            return true;
        }
        if (target is ISlider slider)
        {
            double range = slider.Maximum - slider.Minimum;
            double step = range > 0 ? range / 10.0 : 1.0;
            double value = Math.Clamp(slider.Value + (forward ? step : -step), slider.Minimum, slider.Maximum);
            if (value != slider.Value)
            {
                slider.Value = value;
            }
            _dirty = true;
            return true;
        }
        return false;
    }

    /// <summary>COPY/CUT put the node's text on the system clipboard; CUT clears the editable text.</summary>
    private bool CopyAccessibilityNode(IView? target, OpenHarmonyAccessibilityNode node, bool cut)
    {
        string? text = target is IText textPart && !string.IsNullOrEmpty(textPart.Text) ? textPart.Text : node.Text;
        if (string.IsNullOrEmpty(text))
        {
            return false;
        }
        _ = WriteClipboardAsync(text);
        if (cut)
        {
            SetEditableText(target, string.Empty);
        }
        _dirty = true;
        return true;
    }

    /// <summary>SELECT_TEXT selects the whole editable text (caret at the end).</summary>
    private bool SelectAllAccessibilityNode(IView? target)
    {
        if (target is not ITextInput input)
        {
            return false;
        }
        int length = (input.Text ?? string.Empty).Length;
        input.CursorPosition = length;
        input.SelectionLength = length;
        _dirty = true;
        return true;
    }

    /// <summary>PASTE reads the clipboard and applies the edit on the UI thread when one is available.</summary>
    private bool PasteAccessibilityNode(IView? target)
    {
        if (target is not ITextInput)
        {
            return false;
        }
        _ = PasteClipboardAsync(target);
        return true;
    }

    private async Task PasteClipboardAsync(IView target)
    {
        try
        {
            Microsoft.Maui.ApplicationModel.DataTransfer.IClipboard? clipboard =
                _context.Services.GetService(typeof(Microsoft.Maui.ApplicationModel.DataTransfer.IClipboard))
                as Microsoft.Maui.ApplicationModel.DataTransfer.IClipboard;
            string? text = clipboard is null ? null : await clipboard.GetTextAsync().ConfigureAwait(false);
            if (string.IsNullOrEmpty(text))
            {
                return;
            }
            // The action arrives on the platform accessibility thread; the text edit belongs on the UI thread.
            Microsoft.Maui.Dispatching.IDispatcher? dispatcher =
                _context.Services.GetService(typeof(Microsoft.Maui.Dispatching.IDispatcher))
                as Microsoft.Maui.Dispatching.IDispatcher;
            if (dispatcher is not null && dispatcher.IsDispatchRequired)
            {
                dispatcher.Dispatch(() => ApplyPaste(target, text));
                return;
            }
            ApplyPaste(target, text);
        }
        catch (Exception ex)
        {
            // Reverse P/Invoke boundary: the continuation must never throw either.
            OpenHarmonyBridge.WriteStatus(
                $"[maui] accessibility paste failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private void ApplyPaste(IView target, string pasted)
    {
        if (target is not ITextInput input)
        {
            return;
        }
        string current = input.Text ?? string.Empty;
        int index = Math.Clamp(input.CursorPosition, 0, current.Length);
        int selection = Math.Clamp(input.SelectionLength, 0, current.Length - index);
        string updated = current.Remove(index, selection).Insert(index, pasted);
        SetEditableText(target, updated, index + pasted.Length);
        _dirty = true;
    }

    /// <summary>Writes the text through the DI clipboard (the documented text pasteboard path).</summary>
    private async Task WriteClipboardAsync(string text)
    {
        try
        {
            Microsoft.Maui.ApplicationModel.DataTransfer.IClipboard? clipboard =
                _context.Services.GetService(typeof(Microsoft.Maui.ApplicationModel.DataTransfer.IClipboard))
                as Microsoft.Maui.ApplicationModel.DataTransfer.IClipboard;
            if (clipboard is not null)
            {
                await clipboard.SetTextAsync(text).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            OpenHarmonyBridge.WriteStatus(
                $"[maui] accessibility copy failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// Applies a text edit through the Controls types: IText.Text is read-only, exactly like the
    /// Entry/Editor handlers, so the assignment raises TextChanged and the platform mapper runs.
    /// </summary>
    private static void SetEditableText(IView? target, string text, int? caret = null)
    {
        switch (target)
        {
            case Microsoft.Maui.Controls.Entry entry:
                entry.Text = text;
                break;
            case Microsoft.Maui.Controls.Editor editor:
                editor.Text = text;
                break;
            default:
                return;
        }
        if (caret is int position && target is ITextInput input)
        {
            input.CursorPosition = position;
            input.SelectionLength = 0;
        }
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
