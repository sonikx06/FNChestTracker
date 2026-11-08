using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using ChestTracker.App.Localization;
using ChestTracker.App.Services;
using ChestTracker.Core.Settings;

namespace ChestTracker.App.Controls;

/// <summary>
/// Click, then press a key combination or a mouse side button (Mouse4/Mouse5, optionally with
/// Ctrl/Alt/Shift) to record a hotkey. Esc cancels, Backspace/Delete clears, clicking elsewhere cancels.
/// While recording, global hotkeys are suspended through <see cref="CaptureStateChanged"/>.
/// </summary>
public sealed class HotkeyBox : Button
{
    public static readonly DependencyProperty BindingValueProperty = DependencyProperty.Register(
        nameof(BindingValue), typeof(HotkeyAssignment), typeof(HotkeyBox),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, (d, _) => ((HotkeyBox)d).Refresh()));

    private bool _capturing;
    private Window? _window;

    public HotkeyBox()
    {
        SetResourceReference(StyleProperty, typeof(Button));
        MinWidth = 150;
        HorizontalContentAlignment = HorizontalAlignment.Center;
        Loc.LanguageChanged += (_, _) => Refresh();
        LostKeyboardFocus += (_, _) => EndCapture();
        Unloaded += (_, _) => EndCapture();
    }

    /// <summary>True while any HotkeyBox is recording.</summary>
    public static event EventHandler<bool>? CaptureStateChanged;

    public HotkeyAssignment? BindingValue
    {
        get => (HotkeyAssignment?)GetValue(BindingValueProperty);
        set => SetValue(BindingValueProperty, value);
    }

    protected override void OnClick()
    {
        base.OnClick();
        if (_capturing)
        {
            return;
        }

        _capturing = true;
        _window = Window.GetWindow(this);
        if (_window is not null)
        {
            // Window level, so a side button pressed anywhere over the window is recorded.
            _window.PreviewMouseDown += OnWindowPreviewMouseDown;
        }

        CaptureStateChanged?.Invoke(this, true);
        Focus();
        Keyboard.Focus(this);
        Refresh();
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        if (!_capturing)
        {
            base.OnPreviewKeyDown(e);
            return;
        }

        e.Handled = true;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        switch (key)
        {
            case Key.Escape:
                EndCapture();
                return;
            case Key.Back or Key.Delete:
                Commit(0, HotkeyModifiers.None);
                return;
            case Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift
                or Key.LWin or Key.RWin or Key.ImeProcessed or Key.DeadCharProcessed:
                return;
        }

        Commit(KeyInterop.VirtualKeyFromKey(key), CurrentModifiers());
    }

    private void OnWindowPreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (!_capturing)
        {
            return;
        }

        var vk = e.ChangedButton switch
        {
            MouseButton.XButton1 => MouseButtons.XButton1,
            MouseButton.XButton2 => MouseButtons.XButton2,
            _ => 0,
        };
        if (vk != 0)
        {
            e.Handled = true; // no "browser back" navigation while recording
            Commit(vk, CurrentModifiers());
        }
        else if (!IsMouseOver)
        {
            EndCapture(); // left/right/middle click outside the box: cancel
        }
    }

    private static HotkeyModifiers CurrentModifiers()
    {
        var modifiers = HotkeyModifiers.None;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) modifiers |= HotkeyModifiers.Control;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Alt)) modifiers |= HotkeyModifiers.Alt;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) modifiers |= HotkeyModifiers.Shift;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Windows)) modifiers |= HotkeyModifiers.Win;
        return modifiers;
    }

    private void Commit(int virtualKey, HotkeyModifiers modifiers)
    {
        var action = BindingValue?.Action ?? HotkeyAction.AddChest;
        BindingValue = new HotkeyAssignment { Action = action, VirtualKey = virtualKey, Modifiers = modifiers };
        EndCapture();
    }

    private void EndCapture()
    {
        if (!_capturing)
        {
            return;
        }

        _capturing = false;
        if (_window is not null)
        {
            _window.PreviewMouseDown -= OnWindowPreviewMouseDown;
            _window = null;
        }

        CaptureStateChanged?.Invoke(this, false);
        Refresh();
    }

    private void Refresh()
    {
        Content = _capturing
            ? Loc.T("Hotkey.Press")
            : BindingValue is null || BindingValue.IsEmpty ? Loc.T("Hotkey.None") : HotkeyService.Describe(BindingValue);
    }
}
