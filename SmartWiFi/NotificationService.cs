using System.Windows.Forms;

namespace SmartWiFi;

public sealed class NotificationService
{
    private readonly NotifyIcon _notifyIcon;

    public NotificationService(NotifyIcon notifyIcon)
    {
        _notifyIcon = notifyIcon;
    }

    public void ShowConnected()
    {
        Show("SmartWiFi", "WiFi reconnected - lid opened", ToolTipIcon.Info);
    }

    public void ShowDisconnected()
    {
        Show("SmartWiFi", "WiFi disconnected - lid closed", ToolTipIcon.Info);
    }

    public void ShowError(string message)
    {
        Show("SmartWiFi", message, ToolTipIcon.Warning);
    }

    public void ShowInfo(string message)
    {
        Show("SmartWiFi", message, ToolTipIcon.Info);
    }

    private void Show(string title, string message, ToolTipIcon icon)
    {
        try
        {
            _notifyIcon.BalloonTipTitle = title;
            _notifyIcon.BalloonTipText = message;
            _notifyIcon.BalloonTipIcon = icon;
            _notifyIcon.ShowBalloonTip(3000);
        }
        catch
        {
            // Ignore notification errors so tray behavior remains reliable.
        }
    }
}
