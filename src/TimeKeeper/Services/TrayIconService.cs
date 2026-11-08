using System.Windows;
using Forms = System.Windows.Forms;

namespace TimeKeeper.Services;

/// <summary>
/// System tray icon. Always present, even when the corner is hidden.
/// Left click = same as clicking the corner. Right click = settings / hide / exit.
/// </summary>
public sealed class TrayIconService : IDisposable
{
    private readonly Forms.NotifyIcon _icon;
    private readonly Forms.ToolStripMenuItem _toggleItem;

    public TrayIconService()
    {
        using var stream = Application.GetResourceStream(new Uri("pack://application:,,,/Assets/TimeKeeper.ico"))!.Stream;

        var menu = new Forms.ContextMenuStrip { RightToLeft = Forms.RightToLeft.Yes };
        menu.Items.Add("הגדרות", null, (_, _) => SettingsRequested?.Invoke(this, EventArgs.Empty));
        _toggleItem = new Forms.ToolStripMenuItem("הסתר את הפינה", null, (_, _) => ToggleCornerRequested?.Invoke(this, EventArgs.Empty));
        menu.Items.Add(_toggleItem);
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("יציאה", null, (_, _) => ExitRequested?.Invoke(this, EventArgs.Empty));

        _icon = new Forms.NotifyIcon
        {
            Icon = new System.Drawing.Icon(stream),
            Text = "Time Keeper",
            ContextMenuStrip = menu,
            Visible = true,
        };
        _icon.MouseClick += (_, e) =>
        {
            if (e.Button == Forms.MouseButtons.Left) Clicked?.Invoke(this, EventArgs.Empty);
        };
    }

    public event EventHandler? Clicked;
    public event EventHandler? SettingsRequested;
    public event EventHandler? ToggleCornerRequested;
    public event EventHandler? ExitRequested;

    public void SetCornerHidden(bool hidden) =>
        _toggleItem.Text = hidden ? "הצג את הפינה" : "הסתר את הפינה";

    public void ShowBalloon(string title, string text) =>
        _icon.ShowBalloonTip(5000, title, text, Forms.ToolTipIcon.Info);

    public void SetTooltip(string text) => _icon.Text = text.Length > 63 ? text[..63] : text;

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
    }
}
