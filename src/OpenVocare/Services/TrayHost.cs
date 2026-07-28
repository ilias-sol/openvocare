using System.Drawing;
using System.Windows;
using OpenVocare.Views;
using Forms = System.Windows.Forms;

namespace OpenVocare.Services;

public sealed class TrayHost : IDisposable
{
    private readonly Forms.NotifyIcon _icon;
    private readonly Icon _applicationIcon;
    private readonly Action _showSettings;
    private readonly Action _toggleDictation;
    private readonly Action _quit;
    private TrayMenuWindow? _menu;
    private bool _disposed;

    public TrayHost(Action showSettings, Action toggleDictation, Action quit)
    {
        _showSettings = showSettings;
        _toggleDictation = toggleDictation;
        _quit = quit;
        using Icon? extractedIcon = string.IsNullOrWhiteSpace(Environment.ProcessPath) ? null : Icon.ExtractAssociatedIcon(Environment.ProcessPath);
        _applicationIcon = (Icon)(extractedIcon ?? SystemIcons.Application).Clone();
        _icon = new Forms.NotifyIcon
        {
            Icon = _applicationIcon,
            Text = "OpenVocare — ready",
            Visible = true
        };
        _icon.DoubleClick += (_, _) => _showSettings();
        _icon.MouseUp += (_, e) =>
        {
            if (e.Button == Forms.MouseButtons.Right)
            {
                ShowTrayMenu();
            }
        };
    }

    public void SetStatus(string status)
    {
        string text = $"OpenVocare — {status}";
        _icon.Text = text.Length <= 63 ? text : "OpenVocare";
    }

    public void Notify(string title, string text, Forms.ToolTipIcon icon = Forms.ToolTipIcon.Info)
    {
        _icon.ShowBalloonTip(4000, title, text, icon);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _menu?.Dismiss();
        _icon.Visible = false;
        _icon.Dispose();
        _applicationIcon.Dispose();
        _disposed = true;
    }

    private void ShowTrayMenu()
    {
        System.Windows.Application.Current.Dispatcher.BeginInvoke(() =>
        {
            if (_menu is { IsVisible: true })
            {
                _menu.Dismiss();
                return;
            }

            _menu = new TrayMenuWindow(_showSettings, _toggleDictation, _quit);
            _menu.Closed += (_, _) => _menu = null;
            _menu.ShowNearCursor();
        });
    }
}
