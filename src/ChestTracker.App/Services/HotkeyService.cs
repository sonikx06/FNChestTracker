using System.Runtime.InteropServices;
using System.Windows.Input;
using System.Windows.Interop;
using ChestTracker.App.Interop;
using ChestTracker.Core.Diagnostics;
using ChestTracker.Core.Settings;

namespace ChestTracker.App.Services;

public enum HotkeyStatus
{
    Ok,
    Unbound,
    Duplicate,
    External,
    Disabled,
}

/// <summary>
/// System-wide hotkeys. Keyboard keys use RegisterHotKey on a message-only window; MOD_NOREPEAT makes
/// Windows drop auto-repeat while a key is held, so holding F8 counts once.
/// Mouse side buttons (Mouse4/Mouse5) cannot be registered with RegisterHotKey, so they are observed through
/// Raw Input (RIDEV_INPUTSINK). Raw Input only receives a copy of the input: the click still reaches Fortnite
/// unchanged, nothing is blocked, injected or sent to the game, and no hooks are installed.
/// Raw Input is registered only while at least one mouse button is bound.
/// </summary>
public sealed class HotkeyService : IDisposable
{
    private readonly Dictionary<int, HotkeyAction> _registered = new();
    private readonly Dictionary<HotkeyAction, HotkeyStatus> _status = new();
    private readonly MouseHotkeyMatcher _mouse = new();
    private bool _rawMouseRegistered;
    private HwndSource? _source;
    private List<HotkeyAssignment> _bindings = [];
    private bool _enabled = true;
    private int _suspendCount;
    private int _nextId = 0xB000;

    public event EventHandler<HotkeyAction>? Pressed;

    public event EventHandler? StatusChanged;

    public IReadOnlyDictionary<HotkeyAction, HotkeyStatus> Status => _status;

    public void Initialize()
    {
        var parameters = new HwndSourceParameters("FortniteChestTrackerHotkeys")
        {
            Width = 0,
            Height = 0,
            WindowStyle = 0,
            ParentWindow = new IntPtr(-3), // HWND_MESSAGE
        };
        _source = new HwndSource(parameters);
        _source.AddHook(WndProc);
    }

    public void Apply(IEnumerable<HotkeyAssignment> bindings, bool enabled)
    {
        _bindings = bindings.Select(b => b.Clone()).ToList();
        _enabled = enabled;
        Register();
    }

    /// <summary>Temporarily releases all hotkeys (e.g. while the user records a new key).</summary>
    public void Suspend()
    {
        if (_suspendCount++ == 0)
        {
            UnregisterAll();
        }
    }

    public void Resume()
    {
        if (_suspendCount > 0 && --_suspendCount == 0)
        {
            Register();
        }
    }

    public static string Describe(HotkeyAssignment binding)
    {
        if (binding.IsEmpty)
        {
            return "—";
        }

        var parts = new List<string>();
        if (binding.Modifiers.HasFlag(HotkeyModifiers.Control)) parts.Add("Ctrl");
        if (binding.Modifiers.HasFlag(HotkeyModifiers.Alt)) parts.Add("Alt");
        if (binding.Modifiers.HasFlag(HotkeyModifiers.Shift)) parts.Add("Shift");
        if (binding.Modifiers.HasFlag(HotkeyModifiers.Win)) parts.Add("Win");
        if (MouseButtons.Name(binding.VirtualKey) is { } mouse)
        {
            parts.Add(mouse);
            return string.Join(" + ", parts);
        }

        var key = KeyInterop.KeyFromVirtualKey(binding.VirtualKey);
        parts.Add(key switch
        {
            >= Key.D0 and <= Key.D9 => ((int)(key - Key.D0)).ToString(),
            >= Key.NumPad0 and <= Key.NumPad9 => "Num" + (int)(key - Key.NumPad0),
            Key.OemPlus => "+",
            Key.OemMinus => "-",
            Key.None => $"VK{binding.VirtualKey:X2}",
            _ => key.ToString(),
        });
        return string.Join(" + ", parts);
    }

    private void Register()
    {
        UnregisterAll();
        _status.Clear();
        var duplicates = HotkeyConflicts.Find(_bindings);
        var failures = new List<HotkeyAction>();

        foreach (var binding in _bindings)
        {
            if (!_enabled)
            {
                _status[binding.Action] = HotkeyStatus.Disabled;
                continue;
            }

            if (binding.IsEmpty)
            {
                _status[binding.Action] = HotkeyStatus.Unbound;
                continue;
            }

            if (duplicates.Contains(binding.Action))
            {
                _status[binding.Action] = HotkeyStatus.Duplicate;
                continue;
            }

            if (_source is null || _suspendCount > 0)
            {
                _status[binding.Action] = HotkeyStatus.Ok;
                continue;
            }

            if (binding.IsMouseButton)
            {
                _status[binding.Action] = HotkeyStatus.Ok;
                continue; // Raw Input below
            }

            var id = _nextId++;
            var ok = NativeMethods.RegisterHotKey(_source.Handle, id,
                (uint)binding.Modifiers | NativeMethods.MOD_NOREPEAT, (uint)binding.VirtualKey);
            if (ok)
            {
                _registered[id] = binding.Action;
                _status[binding.Action] = HotkeyStatus.Ok;
            }
            else
            {
                _status[binding.Action] = HotkeyStatus.External;
                failures.Add(binding.Action);
            }
        }

        _mouse.SetBindings(_enabled ? _bindings : []);
        SetRawMouse(_source is not null && _suspendCount == 0 && _mouse.HasBindings);

        if (failures.Count > 0)
        {
            Log.Warn("Hotkeys already used by another application: " + string.Join(", ", failures));
        }

        StatusChanged?.Invoke(this, EventArgs.Empty);
    }

    private void UnregisterAll()
    {
        if (_source is null)
        {
            return;
        }

        foreach (var id in _registered.Keys)
        {
            NativeMethods.UnregisterHotKey(_source.Handle, id);
        }

        _registered.Clear();
        SetRawMouse(false);
        _mouse.Reset();
    }

    private void SetRawMouse(bool on)
    {
        if (_source is null || on == _rawMouseRegistered)
        {
            return;
        }

        var device = new NativeMethods.RAWINPUTDEVICE
        {
            UsagePage = 0x01, // generic desktop
            Usage = 0x02,     // mouse
            Flags = on ? NativeMethods.RIDEV_INPUTSINK : NativeMethods.RIDEV_REMOVE,
            Target = on ? _source.Handle : IntPtr.Zero,
        };
        if (NativeMethods.RegisterRawInputDevices([device], 1, (uint)Marshal.SizeOf<NativeMethods.RAWINPUTDEVICE>()))
        {
            _rawMouseRegistered = on;
            Log.Info(on ? "Mouse side-button hotkeys: Raw Input listening" : "Mouse side-button hotkeys: Raw Input stopped");
            return;
        }

        Log.Warn($"RegisterRawInputDevices({(on ? "add" : "remove")}) failed: {Marshal.GetLastPInvokeError()}");
        if (on)
        {
            foreach (var b in _bindings.Where(b => b.IsMouseButton && _status.GetValueOrDefault(b.Action) == HotkeyStatus.Ok))
            {
                _status[b.Action] = HotkeyStatus.External;
            }
        }
    }

    private void OnRawInput(IntPtr lParam)
    {
        var size = (uint)Marshal.SizeOf<NativeMethods.RAWINPUTMOUSE>();
        var header = (uint)Marshal.SizeOf<NativeMethods.RAWINPUTHEADER>();
        var buffer = Marshal.AllocHGlobal((int)size);
        try
        {
            var read = NativeMethods.GetRawInputData(lParam, NativeMethods.RID_INPUT, buffer, ref size, header);
            if (read == uint.MaxValue || read < header)
            {
                Log.Warn($"GetRawInputData failed ({read}, size {size})");
                return;
            }

            var input = Marshal.PtrToStructure<NativeMethods.RAWINPUTMOUSE>(buffer);
            if (input.Header.Type != NativeMethods.RIM_TYPEMOUSE)
            {
                return;
            }

            var flags = input.Mouse.ButtonFlags;
            const ushort sideButtons = NativeMethods.RI_MOUSE_BUTTON_4_DOWN | NativeMethods.RI_MOUSE_BUTTON_4_UP
                                       | NativeMethods.RI_MOUSE_BUTTON_5_DOWN | NativeMethods.RI_MOUSE_BUTTON_5_UP;
            if ((flags & sideButtons) == 0)
            {
                return; // movement, wheel, left/right/middle buttons: ignored
            }

            var modifiers = CurrentModifiers();
            HandleButton(MouseButtons.XButton1, flags, NativeMethods.RI_MOUSE_BUTTON_4_DOWN, NativeMethods.RI_MOUSE_BUTTON_4_UP, modifiers);
            HandleButton(MouseButtons.XButton2, flags, NativeMethods.RI_MOUSE_BUTTON_5_DOWN, NativeMethods.RI_MOUSE_BUTTON_5_UP, modifiers);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private void HandleButton(int vk, ushort flags, ushort downFlag, ushort upFlag, HotkeyModifiers modifiers)
    {
        // One report may contain both transitions (very fast click): handle "down" before "up".
        if ((flags & downFlag) != 0 && _mouse.OnButton(vk, down: true, modifiers) is { } action && _enabled && _suspendCount == 0)
        {
            Fire(action);
        }

        if ((flags & upFlag) != 0)
        {
            _mouse.OnButton(vk, down: false, modifiers);
        }
    }

    private static HotkeyModifiers CurrentModifiers()
    {
        static bool Down(int vk) => (NativeMethods.GetAsyncKeyState(vk) & 0x8000) != 0;
        var m = HotkeyModifiers.None;
        if (Down(NativeMethods.VK_CONTROL)) m |= HotkeyModifiers.Control;
        if (Down(NativeMethods.VK_MENU)) m |= HotkeyModifiers.Alt;
        if (Down(NativeMethods.VK_SHIFT)) m |= HotkeyModifiers.Shift;
        if (Down(NativeMethods.VK_LWIN) || Down(NativeMethods.VK_RWIN)) m |= HotkeyModifiers.Win;
        return m;
    }

    private void Fire(HotkeyAction action)
    {
        try
        {
            Pressed?.Invoke(this, action);
        }
        catch (Exception ex)
        {
            Log.Error($"Hotkey handler failed for {action}", ex);
        }
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == NativeMethods.WM_HOTKEY && _registered.TryGetValue(wParam.ToInt32(), out var action))
        {
            handled = true;
            Fire(action);
        }
        else if (msg == NativeMethods.WM_INPUT && _rawMouseRegistered)
        {
            // Not marked handled: DefWindowProc must still run for WM_INPUT cleanup.
            OnRawInput(lParam);
        }

        return IntPtr.Zero;
    }

    public void Dispose()
    {
        UnregisterAll();
        _source?.RemoveHook(WndProc);
        _source?.Dispose();
        _source = null;
    }
}
