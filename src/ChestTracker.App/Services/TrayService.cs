using ChestTracker.App.Localization;
using Forms = System.Windows.Forms;

namespace ChestTracker.App.Services;

public sealed class TrayService : IDisposable
{
    private readonly Forms.NotifyIcon _icon;

    public TrayService()
    {
        _icon = new Forms.NotifyIcon
        {
            Icon = IconFactory.CreateTrayIcon(),
            Visible = true,
            Text = "Fortnite Chest Tracker",
        };
        _icon.MouseClick += (_, e) =>
        {
            if (e.Button == Forms.MouseButtons.Left)
            {
                OpenRequested?.Invoke(this, EventArgs.Empty);
            }
        };
        BuildMenu();
        Loc.LanguageChanged += OnLanguageChanged;
    }

    public event EventHandler? OpenRequested;
    public event EventHandler? ToggleOverlayRequested;
    public event EventHandler? AddChestRequested;
    public event EventHandler? StartMatchRequested;
    public event EventHandler? EndMatchRequested;
    public event EventHandler? ExitRequested;

    public void UpdateTooltip(int match, int lifetime)
    {
        var text = Loc.F("Tray.Tooltip", match, lifetime);
        _icon.Text = text.Length > 63 ? text[..63] : text;
    }

    private void OnLanguageChanged(object? sender, EventArgs e) => BuildMenu();

    private void BuildMenu()
    {
        var old = _icon.ContextMenuStrip;
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add(Loc.T("Tray.Open"), null, (_, _) => OpenRequested?.Invoke(this, EventArgs.Empty));
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add(Loc.T("Tray.AddChest"), null, (_, _) => AddChestRequested?.Invoke(this, EventArgs.Empty));
        menu.Items.Add(Loc.T("Tray.StartMatch"), null, (_, _) => StartMatchRequested?.Invoke(this, EventArgs.Empty));
        menu.Items.Add(Loc.T("Tray.EndMatch"), null, (_, _) => EndMatchRequested?.Invoke(this, EventArgs.Empty));
        menu.Items.Add(Loc.T("Tray.ToggleOverlay"), null, (_, _) => ToggleOverlayRequested?.Invoke(this, EventArgs.Empty));
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add(Loc.T("Tray.Exit"), null, (_, _) => ExitRequested?.Invoke(this, EventArgs.Empty));
        _icon.ContextMenuStrip = menu;
        old?.Dispose();
    }

    public void Dispose()
    {
        Loc.LanguageChanged -= OnLanguageChanged;
        _icon.Visible = false;
        _icon.ContextMenuStrip?.Dispose();
        _icon.Icon?.Dispose();
        _icon.Dispose();
    }
}
