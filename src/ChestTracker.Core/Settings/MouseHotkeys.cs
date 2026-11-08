namespace ChestTracker.Core.Settings;

/// <summary>Win32 virtual-key codes of the mouse side buttons, stored in <see cref="HotkeyAssignment.VirtualKey"/>.</summary>
public static class MouseButtons
{
    /// <summary>VK_XBUTTON1, usually the rear side button ("Mouse4", browser back).</summary>
    public const int XButton1 = 0x05;

    /// <summary>VK_XBUTTON2, usually the front side button ("Mouse5", browser forward).</summary>
    public const int XButton2 = 0x06;

    public static bool IsMouseButton(int virtualKey) => virtualKey is XButton1 or XButton2;

    /// <summary>Display name, or null when the key is not a mouse button.</summary>
    public static string? Name(int virtualKey) => virtualKey switch
    {
        XButton1 => "Mouse4",
        XButton2 => "Mouse5",
        _ => null,
    };
}

/// <summary>
/// Turns mouse side-button transitions (from Raw Input) into hotkey actions.
/// A button fires once when pressed and not again until it is released, so holding it never repeats.
/// The modifier keys must match the binding exactly, like keyboard hotkeys.
/// </summary>
public sealed class MouseHotkeyMatcher
{
    private readonly HashSet<int> _held = [];
    private List<HotkeyAssignment> _bindings = [];

    /// <summary>True when at least one mouse binding is active (Raw Input is only registered then).</summary>
    public bool HasBindings => _bindings.Count > 0;

    /// <summary>Uses the mouse-button bindings from <paramref name="bindings"/>; conflicting ones are skipped.</summary>
    public void SetBindings(IEnumerable<HotkeyAssignment> bindings)
    {
        var list = bindings.Select(b => b.Clone()).ToList();
        var conflicts = HotkeyConflicts.Find(list);
        _bindings = list.Where(b => b.IsMouseButton && !conflicts.Contains(b.Action)).ToList();
        _held.Clear();
    }

    /// <returns>The action to run, or null (no binding, a release, or the button is still held).</returns>
    public HotkeyAction? OnButton(int virtualKey, bool down, HotkeyModifiers modifiers)
    {
        if (!MouseButtons.IsMouseButton(virtualKey))
        {
            return null;
        }

        if (!down)
        {
            _held.Remove(virtualKey);
            return null;
        }

        if (!_held.Add(virtualKey))
        {
            return null; // second "down" without an "up" in between
        }

        return _bindings.FirstOrDefault(b => b.VirtualKey == virtualKey && b.Modifiers == modifiers)?.Action;
    }

    /// <summary>Forgets held buttons (e.g. after hotkeys were suspended and the release was missed).</summary>
    public void Reset() => _held.Clear();
}
