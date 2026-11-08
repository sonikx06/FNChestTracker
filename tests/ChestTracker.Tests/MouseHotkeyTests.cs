using ChestTracker.Core.Settings;

namespace ChestTracker.Tests;

/// <summary>Fix 1: Mouse4/Mouse5 hotkeys.</summary>
public class MouseHotkeyTests
{
    private static MouseHotkeyMatcher Matcher(params HotkeyAssignment[] bindings)
    {
        var m = new MouseHotkeyMatcher();
        m.SetBindings(bindings);
        return m;
    }

    [Fact]
    public void Side_buttons_have_names_and_are_recognized()
    {
        Assert.Equal("Mouse4", MouseButtons.Name(MouseButtons.XButton1));
        Assert.Equal("Mouse5", MouseButtons.Name(MouseButtons.XButton2));
        Assert.Null(MouseButtons.Name(0x77));
        Assert.True(new HotkeyAssignment { VirtualKey = 0x05 }.IsMouseButton);
        Assert.False(new HotkeyAssignment { VirtualKey = 0x77 }.IsMouseButton);
    }

    [Fact]
    public void Press_fires_once_and_holding_never_repeats()
    {
        var m = Matcher(new HotkeyAssignment { Action = HotkeyAction.AddChest, VirtualKey = MouseButtons.XButton1 });

        Assert.Equal(HotkeyAction.AddChest, m.OnButton(MouseButtons.XButton1, down: true, HotkeyModifiers.None));
        // Repeated "down" while held (e.g. duplicate reports) is ignored.
        Assert.Null(m.OnButton(MouseButtons.XButton1, down: true, HotkeyModifiers.None));
        Assert.Null(m.OnButton(MouseButtons.XButton1, down: true, HotkeyModifiers.None));
        Assert.Null(m.OnButton(MouseButtons.XButton1, down: false, HotkeyModifiers.None));
        Assert.Equal(HotkeyAction.AddChest, m.OnButton(MouseButtons.XButton1, down: true, HotkeyModifiers.None));
    }

    [Fact]
    public void Each_button_maps_to_its_own_action_and_modifiers_must_match()
    {
        var m = Matcher(
            new HotkeyAssignment { Action = HotkeyAction.AddChest, VirtualKey = MouseButtons.XButton2 },
            new HotkeyAssignment { Action = HotkeyAction.RemoveLastChest, VirtualKey = MouseButtons.XButton1, Modifiers = HotkeyModifiers.Shift });

        Assert.Equal(HotkeyAction.AddChest, m.OnButton(MouseButtons.XButton2, true, HotkeyModifiers.None));
        Assert.Null(m.OnButton(MouseButtons.XButton1, true, HotkeyModifiers.None)); // Shift required
        m.OnButton(MouseButtons.XButton1, false, HotkeyModifiers.None);
        Assert.Equal(HotkeyAction.RemoveLastChest, m.OnButton(MouseButtons.XButton1, true, HotkeyModifiers.Shift));
        m.OnButton(MouseButtons.XButton2, false, HotkeyModifiers.None);
        Assert.Null(m.OnButton(MouseButtons.XButton2, true, HotkeyModifiers.Control)); // extra modifier: no match
    }

    [Fact]
    public void Keyboard_keys_and_unbound_buttons_are_ignored()
    {
        var m = Matcher(new HotkeyAssignment { Action = HotkeyAction.AddChest, VirtualKey = 0x77 }); // F8 only
        Assert.False(m.HasBindings);
        Assert.Null(m.OnButton(MouseButtons.XButton1, true, HotkeyModifiers.None));
        Assert.Null(m.OnButton(0x77, true, HotkeyModifiers.None));
    }

    [Fact]
    public void Conflicting_mouse_bindings_are_detected_and_not_fired()
    {
        HotkeyAssignment[] bindings =
        [
            new() { Action = HotkeyAction.AddChest, VirtualKey = MouseButtons.XButton1 },
            new() { Action = HotkeyAction.StartMatch, VirtualKey = MouseButtons.XButton1 },
            new() { Action = HotkeyAction.EndMatch, VirtualKey = MouseButtons.XButton1, Modifiers = HotkeyModifiers.Alt },
        ];

        Assert.Equal(new HashSet<HotkeyAction> { HotkeyAction.AddChest, HotkeyAction.StartMatch }, HotkeyConflicts.Find(bindings));
        var m = Matcher(bindings);
        Assert.Null(m.OnButton(MouseButtons.XButton1, true, HotkeyModifiers.None));
        m.OnButton(MouseButtons.XButton1, false, HotkeyModifiers.None);
        Assert.Equal(HotkeyAction.EndMatch, m.OnButton(MouseButtons.XButton1, true, HotkeyModifiers.Alt));
    }

    [Fact]
    public void Mouse_bindings_persist_and_restore()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ct-hotkeys-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var store = new SettingsStore(Path.Combine(dir, "settings.json"));
            var settings = store.Load();
            settings.Hotkeys.Bindings.Single(b => b.Action == HotkeyAction.AddChest).VirtualKey = MouseButtons.XButton2;
            var remove = settings.Hotkeys.Bindings.Single(b => b.Action == HotkeyAction.RemoveLastChest);
            remove.VirtualKey = MouseButtons.XButton1;
            remove.Modifiers = HotkeyModifiers.Control;
            store.Save(settings);

            var loaded = new SettingsStore(store.FilePath).Load();
            Assert.Equal(MouseButtons.XButton2, loaded.Hotkeys.Get(HotkeyAction.AddChest).VirtualKey);
            Assert.Equal(MouseButtons.XButton1, loaded.Hotkeys.Get(HotkeyAction.RemoveLastChest).VirtualKey);
            Assert.Equal(HotkeyModifiers.Control, loaded.Hotkeys.Get(HotkeyAction.RemoveLastChest).Modifiers);
            Assert.True(loaded.Hotkeys.Get(HotkeyAction.AddChest).IsMouseButton);
            Assert.Empty(HotkeyConflicts.Find(loaded.Hotkeys.Bindings));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }
}
