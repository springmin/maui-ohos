// Keyboard accelerators for the OpenHarmony slice's hardware-key surface - an
// OpenHarmony-specific extension over rc.1's Microsoft.Maui.Controls.KeyboardAccelerator
// (the only public collection is MenuFlyoutItem.KeyboardAccelerators; there is no per-element
// surface to consume automatically). The manager keeps an internal registry fed by the
// OpenHarmonyKeyListener hardware-key callback, matches the tracked modifier bits exactly and
// invokes as a click; the key-name/code subset, the modifier mapping and the changes a public
// rc.1 surface would need are documented in docs/openharmony-slice-notes.md
// ("Keyboard accelerators").
using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using Microsoft.Maui;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Handlers;

namespace Microsoft.Maui.Platform;

/// <summary>
/// Internal keyboard-accelerator registry for the slice: rc.1's KeyboardAccelerator type/enum
/// with matching driven by OpenHarmonyKeyListener. Inert (no subscription, no work per key)
/// until the first accelerator is registered.
/// </summary>
internal static class OpenHarmonyKeyboardAcceleratorManager
{
    /// <summary>Modifier bits the manager tracks from host key events.</summary>
    internal const KeyboardAcceleratorModifiers TrackedModifiers =
        KeyboardAcceleratorModifiers.Ctrl | KeyboardAcceleratorModifiers.Alt |
        KeyboardAcceleratorModifiers.Shift | KeyboardAcceleratorModifiers.Cmd |
        KeyboardAcceleratorModifiers.Windows;

    // ArkUI @ohos.multimodalInput.keyCode values used for modifier tracking.
    private const int KeyCodeAltLeft = 2045;
    private const int KeyCodeAltRight = 2046;
    private const int KeyCodeShiftLeft = 2047;
    private const int KeyCodeShiftRight = 2048;
    private const int KeyCodeCtrlLeft = 2072;
    private const int KeyCodeCtrlRight = 2073;
    private const int KeyCodeMetaLeft = 2076;
    private const int KeyCodeMetaRight = 2077;

    private sealed class Entry
    {
        public required WeakReference<BindableObject> Element { get; init; }
        public required string Key { get; init; }
        public required KeyboardAcceleratorModifiers Modifiers { get; init; }
        public Action? Invoked { get; init; }
    }

    private static readonly object s_sync = new();
    private static readonly List<Entry> s_entries = new();
    private static readonly Action<int, int> s_keyHandler = OnKeyEvent;
    private static bool s_installed;
    private static bool s_hooked;
    private static KeyboardAcceleratorModifiers s_modifiers;

    /// <summary>Registered accelerators (dead elements are pruned on access).</summary>
    internal static int Count
    {
        get { lock (s_sync) { PruneLocked(); return s_entries.Count; } }
    }

    /// <summary>True while the manager is subscribed to the key listener (only with registrations).</summary>
    internal static bool IsHooked => s_hooked;

    /// <summary>Modifier bits currently held (tracked from the host key stream).</summary>
    internal static KeyboardAcceleratorModifiers CurrentModifiers
    {
        get { lock (s_sync) { return s_modifiers; } }
    }

    /// <summary>Key-down events that matched a registered accelerator.</summary>
    internal static int Matches { get; private set; }

    /// <summary>Matched accelerators that invoked their element/callback.</summary>
    internal static int Invocations { get; private set; }

    /// <summary>Dead registrations pruned.</summary>
    internal static int Pruned { get; private set; }

    /// <summary>
    /// Ensures the hardware-key surface exists (OpenHarmonyKeyListener.Install is idempotent)
    /// but does not subscribe: the key hook is added with the first registration. Idempotent;
    /// called from the module initializer, so the consolidation pass can call it from
    /// UseOpenHarmony instead.
    /// </summary>
    internal static void Install()
    {
        if (s_installed)
        {
            return;
        }
        s_installed = true;
        OpenHarmonyKeyListener.Install();
    }

    [ModuleInitializer]
    internal static void Initialize() => Install();

    /// <summary>Registers an rc.1 accelerator (Key/Modifiers) for an element.</summary>
    internal static bool Register(BindableObject element, IKeyboardAccelerator accelerator, Action? invoked = null)
        => Register(element, accelerator?.Key ?? string.Empty, accelerator?.Modifiers ?? KeyboardAcceleratorModifiers.None, invoked);

    /// <summary>Registers an accelerator from its key name and modifier flags.</summary>
    internal static bool Register(BindableObject element, string key, KeyboardAcceleratorModifiers modifiers = KeyboardAcceleratorModifiers.None, Action? invoked = null)
    {
        ArgumentNullException.ThrowIfNull(element);
        if (string.IsNullOrWhiteSpace(key))
        {
            return false;
        }
        lock (s_sync)
        {
            PruneLocked();
            for (int i = s_entries.Count - 1; i >= 0; i--)
            {
                Entry existing = s_entries[i];
                if (existing.Element.TryGetTarget(out BindableObject? target) && ReferenceEquals(target, element) &&
                    string.Equals(existing.Key, key.Trim(), StringComparison.OrdinalIgnoreCase))
                {
                    s_entries.RemoveAt(i);
                }
            }
            s_entries.Add(new Entry
            {
                Element = new WeakReference<BindableObject>(element),
                Key = key.Trim(),
                Modifiers = modifiers,
                Invoked = invoked,
            });
        }
        EnsureKeyHook();
        return true;
    }

    /// <summary>
    /// Registers every accelerator of an rc.1 element collection (the only public one is
    /// MenuFlyoutItem.KeyboardAccelerators). Returns how many entries were registered.
    /// </summary>
    internal static int RegisterElementAccelerators(BindableObject element, Action? invoked = null)
    {
        ArgumentNullException.ThrowIfNull(element);
        if (element is not IMenuFlyoutItem menuFlyout || menuFlyout.KeyboardAccelerators is not { } accelerators)
        {
            return 0;
        }
        int count = 0;
        foreach (IKeyboardAccelerator accelerator in accelerators)
        {
            if (Register(element, accelerator, invoked))
            {
                count++;
            }
        }
        return count;
    }

    /// <summary>Removes every accelerator registered for an element.</summary>
    internal static int Unregister(BindableObject element)
    {
        ArgumentNullException.ThrowIfNull(element);
        int removed = 0;
        lock (s_sync)
        {
            for (int i = s_entries.Count - 1; i >= 0; i--)
            {
                if (!s_entries[i].Element.TryGetTarget(out BindableObject? target))
                {
                    s_entries.RemoveAt(i);
                    Pruned++;
                    continue;
                }
                if (ReferenceEquals(target, element))
                {
                    s_entries.RemoveAt(i);
                    removed++;
                }
            }
        }
        if (removed > 0)
        {
            ReleaseIfIdle();
        }
        return removed;
    }

    /// <summary>Removes every accelerator (tests/teardown).</summary>
    internal static void Clear()
    {
        lock (s_sync)
        {
            s_entries.Clear();
            s_modifiers = KeyboardAcceleratorModifiers.None;
        }
        ReleaseIfIdle();
    }

    /// <summary>Forgets the tracked modifier state (focus loss between modifier down/up).</summary>
    internal static void ResetModifiers()
    {
        lock (s_sync)
        {
            s_modifiers = KeyboardAcceleratorModifiers.None;
        }
    }

    /// <summary>
    /// Handles one raw host key event. Down events of non-modifier keys match the registry and
    /// invoke the winner; modifier keys only update the tracked mask. Returns true when an
    /// accelerator was invoked. Public-internal so the scratch driver/keys tests can drive it
    /// without the native callback (the listener's Dispatch reaches it through the event).
    /// </summary>
    internal static bool HandleKeyEvent(int keyCode, int eventType)
    {
        bool down = eventType == OpenHarmonyKeyListener.KeyTypeDown;
        bool up = eventType == OpenHarmonyKeyListener.KeyTypeUp;
        if (!down && !up)
        {
            return false;
        }
        bool isModifier = UpdateModifier(keyCode, down);
        if (!down || isModifier)
        {
            return false;
        }
        Entry? match = null;
        BindableObject? target = null;
        lock (s_sync)
        {
            if (s_entries.Count == 0)
            {
                return false;
            }
            PruneLocked();
            foreach (Entry entry in s_entries)
            {
                if (!entry.Element.TryGetTarget(out BindableObject? element) || !IsEnabled(element))
                {
                    continue;
                }
                if (!KeyMatches(entry.Key, keyCode))
                {
                    continue;
                }
                if ((s_modifiers & TrackedModifiers) != (entry.Modifiers & TrackedModifiers))
                {
                    continue;
                }
                match = entry;
                target = element;
                break;
            }
        }
        if (match is null || target is null)
        {
            return false;
        }
        Matches++;
        try
        {
            if (Invoke(match, target))
            {
                Invocations++;
                return true;
            }
        }
        catch (Exception)
        {
            // A user command/callback must not escape the key frame (the native callback path
            // is guarded as well); the accelerator simply reports "not handled".
        }
        return false;
    }

    private static void OnKeyEvent(int keyCode, int eventType)
    {
        try
        {
            HandleKeyEvent(keyCode, eventType);
        }
        catch (Exception)
        {
            // A subscriber must not throw into the native key callback frame.
        }
    }

    private static bool UpdateModifier(int keyCode, bool down)
    {
        KeyboardAcceleratorModifiers bit = keyCode switch
        {
            KeyCodeAltLeft or KeyCodeAltRight => KeyboardAcceleratorModifiers.Alt,
            KeyCodeShiftLeft or KeyCodeShiftRight => KeyboardAcceleratorModifiers.Shift,
            KeyCodeCtrlLeft or KeyCodeCtrlRight => KeyboardAcceleratorModifiers.Ctrl,
            KeyCodeMetaLeft or KeyCodeMetaRight => KeyboardAcceleratorModifiers.Cmd | KeyboardAcceleratorModifiers.Windows,
            _ => KeyboardAcceleratorModifiers.None,
        };
        if (bit == KeyboardAcceleratorModifiers.None)
        {
            return false;
        }
        lock (s_sync)
        {
            if (down)
            {
                s_modifiers |= bit;
            }
            else
            {
                s_modifiers &= ~bit;
            }
        }
        return true;
    }

    /// <summary>Invokes the element the way a click would; returns false when nothing ran.</summary>
    private static bool Invoke(Entry entry, BindableObject element)
    {
        if (entry.Invoked is { } callback)
        {
            callback();
            return true;
        }
        if (element is IButton button)
        {
            // Button/ImageButton: runs Command and raises Clicked (verified rc.1 semantics).
            button.Clicked();
            return true;
        }
        if (element is MenuFlyoutItem menuItem && ActivateMenuItem(menuItem))
        {
            return true;
        }
        return TryExecuteCommand(element);
    }

    private static MethodInfo? s_activateMenuItem;
    private static bool s_activateResolved;

    /// <summary>MenuFlyoutItem activation: the rc.1 IMenuItemController.Activate() is internal and
    /// runs the item's Command and raises Clicked (verified); reached through the slice's usual
    /// guarded reflection.</summary>
    private static bool ActivateMenuItem(MenuFlyoutItem menuItem)
    {
        if (!s_activateResolved)
        {
            s_activateResolved = true;
            s_activateMenuItem = typeof(MenuItem).GetMethod(
                "Microsoft.Maui.Controls.IMenuItemController.Activate",
                BindingFlags.Instance | BindingFlags.NonPublic);
        }
        if (s_activateMenuItem is null)
        {
            return false;
        }
        try
        {
            s_activateMenuItem.Invoke(menuItem, null);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>Generic public Command/CommandParameter fallback for elements that are not
    /// Buttons/MenuFlyoutItems.</summary>
    private static bool TryExecuteCommand(BindableObject element)
    {
        PropertyInfo? commandProperty = element.GetType().GetProperty("Command", BindingFlags.Public | BindingFlags.Instance);
        if (commandProperty?.GetValue(element) is not ICommand command)
        {
            return false;
        }
        object? parameter = element.GetType()
            .GetProperty("CommandParameter", BindingFlags.Public | BindingFlags.Instance)?
            .GetValue(element);
        if (!command.CanExecute(parameter))
        {
            return false;
        }
        command.Execute(parameter);
        return true;
    }

    private static bool IsEnabled(BindableObject element)
    {
        if (element is IView view && !view.IsEnabled)
        {
            return false;
        }
        if (element is MenuItem menuItem && !menuItem.IsEnabled)
        {
            return false;
        }
        return true;
    }

    private static void EnsureKeyHook()
    {
        lock (s_sync)
        {
            if (s_hooked)
            {
                return;
            }
            s_hooked = true;
        }
        OpenHarmonyKeyListener.KeyEvent += s_keyHandler;
    }

    private static void ReleaseIfIdle()
    {
        bool release;
        lock (s_sync)
        {
            PruneLocked();
            release = s_hooked && s_entries.Count == 0;
            if (release)
            {
                s_hooked = false;
                s_modifiers = KeyboardAcceleratorModifiers.None;
            }
        }
        if (release)
        {
            OpenHarmonyKeyListener.KeyEvent -= s_keyHandler;
        }
    }

    private static void PruneLocked()
    {
        for (int i = s_entries.Count - 1; i >= 0; i--)
        {
            if (!s_entries[i].Element.TryGetTarget(out _))
            {
                s_entries.RemoveAt(i);
                Pruned++;
            }
        }
    }

    // ------------------------------------------------------------------ key mapping

    /// <summary>True when the accelerator key name matches the host key code.</summary>
    internal static bool KeyMatches(string key, int keyCode)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return false;
        }
        string trimmed = key.Trim();
        if (int.TryParse(trimmed, NumberStyles.None, CultureInfo.InvariantCulture, out int raw))
        {
            return raw == keyCode;
        }
        foreach (string name in NamesFor(keyCode))
        {
            if (string.Equals(name, trimmed, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>Canonical name of a host key code (null when outside the documented subset).</summary>
    internal static string? MapKeyName(int keyCode)
    {
        string[] names = NamesFor(keyCode);
        return names.Length > 0 ? names[0] : null;
    }

    private static string[] NamesFor(int keyCode) => keyCode switch
    {
        >= 2000 and <= 2009 => new[] { ((char)('0' + keyCode - 2000)).ToString(), $"D{keyCode - 2000}", $"Number{keyCode - 2000}" },
        >= 2017 and <= 2042 => new[] { ((char)('A' + keyCode - 2017)).ToString() },
        >= 2090 and <= 2101 => new[] { $"F{keyCode - 2089}" },
        2012 => new[] { "Up", "ArrowUp", "DPadUp" },
        2013 => new[] { "Down", "ArrowDown", "DPadDown" },
        2014 => new[] { "Left", "ArrowLeft", "DPadLeft" },
        2015 => new[] { "Right", "ArrowRight", "DPadRight" },
        2016 => new[] { "Center", "DPadCenter" },
        2043 => new[] { "Comma", "OemComma" },
        2044 => new[] { "Period", "OemPeriod" },
        2049 => new[] { "Tab" },
        2050 => new[] { "Space", "Spacebar" },
        2054 => new[] { "Enter", "Return" },
        2055 => new[] { "Backspace", "Back" },
        2056 => new[] { "Grave", "OemTilde" },
        2057 => new[] { "Minus", "OemMinus" },
        2058 => new[] { "Equals", "Plus", "OemPlus" },
        2059 => new[] { "LeftBracket", "OemOpenBrackets" },
        2060 => new[] { "RightBracket", "OemCloseBrackets" },
        2061 => new[] { "Backslash", "OemPipe" },
        2062 => new[] { "Semicolon", "OemSemicolon" },
        2063 => new[] { "Apostrophe", "Quote", "OemQuotes" },
        2064 => new[] { "Slash", "OemQuestion" },
        2067 => new[] { "Menu" },
        2068 => new[] { "PageUp" },
        2069 => new[] { "PageDown" },
        2070 => new[] { "Escape", "Esc" },
        2071 => new[] { "Delete", "Del", "ForwardDelete" },
        2074 => new[] { "CapsLock" },
        2081 => new[] { "Home" },
        2082 => new[] { "End" },
        2083 => new[] { "Insert" },
        2102 => new[] { "NumLock" },
        >= 2103 and <= 2112 => new[] { $"Numpad{keyCode - 2103}", $"NumberPad{keyCode - 2103}" },
        2113 => new[] { "NumpadDivide", "Divide" },
        2114 => new[] { "NumpadMultiply", "Multiply" },
        2115 => new[] { "NumpadSubtract", "Subtract" },
        2116 => new[] { "NumpadAdd", "Add" },
        2117 => new[] { "NumpadDecimal", "NumpadDot", "Decimal" },
        2119 => new[] { "NumpadEnter" },
        2120 => new[] { "NumpadEquals" },
        _ => Array.Empty<string>(),
    };

    /// <summary>Resets counters/state and releases the key hook (tests).</summary>
    internal static void ResetForTests()
    {
        Clear();
        lock (s_sync)
        {
            s_entries.Clear();
            s_modifiers = KeyboardAcceleratorModifiers.None;
            Matches = 0;
            Invocations = 0;
            Pruned = 0;
        }
    }
}
