using Drawing = System.Drawing;
using Forms = System.Windows.Forms;

namespace Citadel.Shell;

internal interface ITrayHost : IDisposable
{
    event Action? OpenRequested;

    event Action? ExitRequested;
}

/// <summary>Shell-owned notification-area surface; it owns no application state.</summary>
internal sealed class TrayHost : ITrayHost
{
    private readonly Drawing.Icon _icon;
    private readonly Forms.ContextMenuStrip _menu;
    private readonly Forms.ToolStripMenuItem _openItem;
    private readonly Forms.ToolStripMenuItem _exitItem;
    private readonly Forms.NotifyIcon _notifyIcon;
    private bool _attached;
    private bool _disposed;

    private TrayHost(
        Drawing.Icon icon,
        Forms.ContextMenuStrip menu,
        Forms.ToolStripMenuItem openItem,
        Forms.ToolStripMenuItem exitItem,
        Forms.NotifyIcon notifyIcon)
    {
        _icon = icon;
        _menu = menu;
        _openItem = openItem;
        _exitItem = exitItem;
        _notifyIcon = notifyIcon;
    }

    public event Action? OpenRequested;

    public event Action? ExitRequested;

    internal static ITrayHost? TryCreate(out string? error)
    {
        TrayHost? host = null;
        Drawing.Icon? icon = null;
        Forms.ContextMenuStrip? menu = null;
        Forms.NotifyIcon? notifyIcon = null;
        try
        {
            var executablePath = Environment.ProcessPath
                ?? throw new InvalidOperationException("the executable path is unavailable");
            icon = Drawing.Icon.ExtractAssociatedIcon(executablePath)
                ?? throw new InvalidOperationException("the executable has no application icon");
            menu = new Forms.ContextMenuStrip();
            var openItem = new Forms.ToolStripMenuItem("Open Citadel");
            var exitItem = new Forms.ToolStripMenuItem("Exit");
            menu.Items.Add(openItem);
            menu.Items.Add(new Forms.ToolStripSeparator());
            menu.Items.Add(exitItem);
            notifyIcon = new Forms.NotifyIcon
            {
                Icon = icon,
                Text = "Citadel",
                ContextMenuStrip = menu,
            };

            host = new TrayHost(icon, menu, openItem, exitItem, notifyIcon);
            host.Attach();
            error = null;
            return host;
        }
        catch (Exception exception)
        {
            if (host is not null)
            {
                host.Dispose();
            }
            else
            {
                notifyIcon?.Dispose();
                menu?.Dispose();
                icon?.Dispose();
            }
            error = exception.Message;
            return null;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (_attached)
        {
            _notifyIcon.MouseClick -= OnMouseClick;
            _openItem.Click -= OnOpenClick;
            _exitItem.Click -= OnExitClick;
            _attached = false;
        }

        try { _notifyIcon.Visible = false; }
        catch (InvalidOperationException) { }
        _notifyIcon.Dispose();
        _menu.Dispose();
        _icon.Dispose();
    }

    private void Attach()
    {
        _notifyIcon.MouseClick += OnMouseClick;
        _openItem.Click += OnOpenClick;
        _exitItem.Click += OnExitClick;
        _attached = true;
        _notifyIcon.Visible = true;
    }

    private void OnMouseClick(object? sender, Forms.MouseEventArgs args)
    {
        if (args.Button == Forms.MouseButtons.Left) OpenRequested?.Invoke();
    }

    private void OnOpenClick(object? sender, EventArgs args) => OpenRequested?.Invoke();

    private void OnExitClick(object? sender, EventArgs args) => ExitRequested?.Invoke();
}
