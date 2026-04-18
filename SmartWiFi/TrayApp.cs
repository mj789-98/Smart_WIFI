using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace SmartWiFi;

public sealed class TrayApp : ApplicationContext
{
    private const int ResumeReconnectDelayMs = 4000;

    private readonly AppSettings _settings;
    private readonly WiFiManager _wifiManager;
    private readonly LidWatcher _lidWatcher;
    private readonly StartupManager _startupManager;
    private readonly NotifyIcon _notifyIcon;
    private readonly NotificationService _notificationService;
    private readonly Icon _wifiOnIcon;
    private readonly Icon _wifiOffIcon;
    private readonly ToolStripMenuItem _statusMenuItem;
    private readonly ToolStripMenuItem _enableMenuItem;
    private readonly ToolStripMenuItem _disableMenuItem;
    private readonly ToolStripMenuItem _startupMenuItem;
    private readonly ToolStripMenuItem _notificationsMenuItem;
    private readonly System.Windows.Forms.Timer _resumeTimer;

    private bool _disconnectedByLid;

    public TrayApp()
    {
        _settings = AppSettings.Load();
        _wifiManager = new WiFiManager(_settings);
        _lidWatcher = new LidWatcher();
        _startupManager = new StartupManager("SmartWiFi", Application.ExecutablePath);
        _wifiOnIcon = CreateTrayIcon(Color.FromArgb(44, 123, 57));
        _wifiOffIcon = CreateTrayIcon(Color.FromArgb(176, 44, 44));

        _statusMenuItem = new ToolStripMenuItem("WiFi: Unknown")
        {
            Enabled = false
        };

        _enableMenuItem = new ToolStripMenuItem("Connect WiFi", null, (_, _) => ConnectWiFi(manual: true));
        _disableMenuItem = new ToolStripMenuItem("Disconnect WiFi", null, (_, _) => DisconnectWiFi(manual: true));
        _startupMenuItem = new ToolStripMenuItem("Start with Windows", null, (_, _) => ToggleStartup());
        _notificationsMenuItem = new ToolStripMenuItem("Notifications", null, (_, _) => ToggleNotifications());

        var exitMenuItem = new ToolStripMenuItem("Exit", null, (_, _) => ExitApplication());
        var contextMenu = new ContextMenuStrip();
        contextMenu.Items.AddRange(new ToolStripItem[]
        {
            _statusMenuItem,
            _enableMenuItem,
            _disableMenuItem,
            new ToolStripSeparator(),
            _startupMenuItem,
            _notificationsMenuItem,
            new ToolStripSeparator(),
            exitMenuItem
        });
        contextMenu.Opening += (_, _) => RefreshUi();

        _notifyIcon = new NotifyIcon
        {
            Visible = true,
            Icon = _wifiOnIcon,
            Text = "SmartWiFi",
            ContextMenuStrip = contextMenu
        };
        _notifyIcon.DoubleClick += (_, _) => RefreshUi();

        _notificationService = new NotificationService(_notifyIcon);
        _resumeTimer = new System.Windows.Forms.Timer
        {
            Interval = ResumeReconnectDelayMs
        };
        _resumeTimer.Tick += (_, _) =>
        {
            _resumeTimer.Stop();
            ConnectWiFi(manual: false);
        };

        _lidWatcher.LidClosed += (_, _) => HandleLidClosed();
        _lidWatcher.LidOpened += (_, _) => HandleLidOpened();

        _settings.AutoStart = _startupManager.IsEnabled;
        _settings.Save();

        RefreshUi();
    }

    protected override void ExitThreadCore()
    {
        _lidWatcher.Dispose();
        _resumeTimer.Stop();
        _resumeTimer.Dispose();
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        _wifiOnIcon.Dispose();
        _wifiOffIcon.Dispose();
        base.ExitThreadCore();
    }

    private void ToggleStartup()
    {
        var targetState = !_startupManager.IsEnabled;
        if (!_startupManager.SetEnabled(targetState, out var errorMessage))
        {
            _notificationService.ShowError(errorMessage ?? "Unable to update startup setting.");
            RefreshUi(errorMessage);
            return;
        }

        _settings.AutoStart = targetState;
        _settings.Save();
        RefreshUi();
    }

    private void ToggleNotifications()
    {
        _settings.NotificationsEnabled = !_settings.NotificationsEnabled;
        _settings.Save();
        RefreshUi();
    }

    private void ConnectWiFi(bool manual)
    {
        if (!manual && !_disconnectedByLid)
        {
            RefreshUi();
            return;
        }

        var result = _wifiManager.Connect();
        if (!result.Success)
        {
            _notificationService.ShowError(result.Message);
            RefreshUi(result.Message);
            return;
        }

        _disconnectedByLid = false;
        RefreshUi();

        if (!manual && result.Changed && _settings.NotificationsEnabled)
        {
            _notificationService.ShowConnected();
        }
    }

    private void DisconnectWiFi(bool manual)
    {
        var status = _wifiManager.GetStatus();
        if (!status.AdapterDetected)
        {
            RefreshUi(status.Message);
            return;
        }

        if (!manual && status.IsConnected == false)
        {
            _disconnectedByLid = false;
            RefreshUi();
            return;
        }

        var result = _wifiManager.Disconnect();
        if (!result.Success)
        {
            _notificationService.ShowError(result.Message);
            RefreshUi(result.Message);
            return;
        }

        _disconnectedByLid = !manual && result.Changed;
        RefreshUi();

        if (!manual && result.Changed && _settings.NotificationsEnabled)
        {
            _notificationService.ShowDisconnected();
        }
    }

    private void HandleLidClosed()
    {
        _resumeTimer.Stop();

        var status = _wifiManager.GetStatus();
        if (!status.AdapterDetected)
        {
            RefreshUi(status.Message);
            return;
        }

        if (status.IsConnected == false)
        {
            _disconnectedByLid = false;
            RefreshUi();
            return;
        }

        var result = _wifiManager.DisconnectForSuspend();
        if (!result.Success)
        {
            _notificationService.ShowError(result.Message);
            RefreshUi(result.Message);
            return;
        }

        _disconnectedByLid = true;
        RefreshUi();
    }

    private void HandleLidOpened()
    {
        if (!_disconnectedByLid)
        {
            RefreshUi();
            return;
        }

        _resumeTimer.Stop();
        _resumeTimer.Start();
        RefreshUi("Reconnect scheduled...");
    }

    private void RefreshUi(string? errorMessage = null)
    {
        var status = _wifiManager.GetStatus();

        if (status.AdapterDetected && !_lidWatcher.IsRunning)
        {
            _lidWatcher.Start();
        }
        else if (!status.AdapterDetected && _lidWatcher.IsRunning)
        {
            _lidWatcher.Stop();
        }

        var statusText = status.IsConnected switch
        {
            true => "Connected",
            false => "Disconnected",
            null => "UNKNOWN"
        };

        _statusMenuItem.Text = status.AdapterDetected ? $"WiFi: {statusText}" : "WiFi: Error";
        _enableMenuItem.Enabled = status.AdapterDetected && status.IsConnected != true;
        _disableMenuItem.Enabled = status.AdapterDetected && status.IsConnected != false;
        _startupMenuItem.Checked = _startupManager.IsEnabled;
        _notificationsMenuItem.Checked = _settings.NotificationsEnabled;
        _notifyIcon.Icon = status.IsConnected == false ? _wifiOffIcon : _wifiOnIcon;
        _notifyIcon.Text = BuildTooltip(errorMessage, status);
    }

    private void ExitApplication()
    {
        ExitThread();
    }

    private static string BuildTooltip(string? errorMessage, WiFiManager.WiFiConnectionStatus status)
    {
        if (!string.IsNullOrWhiteSpace(errorMessage))
        {
            return TrimTooltip($"SmartWiFi - {errorMessage}");
        }

        if (!status.AdapterDetected)
        {
            return "SmartWiFi - No WiFi adapter";
        }

        var state = status.IsConnected switch
        {
            true => "connected",
            false => "disconnected",
            null => "UNKNOWN"
        };

        return TrimTooltip($"SmartWiFi - WiFi {state}");
    }

    private static string TrimTooltip(string value)
    {
        return value.Length <= 63 ? value : value[..63];
    }

    private static Icon CreateTrayIcon(Color accentColor)
    {
        using var bitmap = new Bitmap(16, 16);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.Clear(Color.Transparent);

        using var pen = new Pen(accentColor, 1.8f);
        using var brush = new SolidBrush(accentColor);

        graphics.FillEllipse(brush, 6.4f, 11.2f, 3.2f, 3.2f);
        graphics.DrawArc(pen, 5.2f, 8.8f, 5.6f, 5.6f, 240, 60);
        graphics.DrawArc(pen, 3.3f, 6.4f, 9.4f, 9.4f, 230, 80);
        graphics.DrawArc(pen, 1.1f, 3.7f, 13.8f, 13.8f, 222, 96);

        var iconHandle = bitmap.GetHicon();
        try
        {
            using var icon = Icon.FromHandle(iconHandle);
            return (Icon)icon.Clone();
        }
        finally
        {
            DestroyIcon(iconHandle);
        }
    }

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr handle);
}
