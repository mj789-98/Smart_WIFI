using System.Runtime.InteropServices;

namespace SmartWiFi;

/// <summary>
/// Listens to the physical lid switch via RegisterPowerSettingNotification
/// with GUID_LIDSWITCH_STATE_CHANGE. This fires regardless of the lid-close
/// action configured in Power Options (Sleep, Hibernate, Turn off display,
/// or Do nothing).
/// </summary>
public sealed class LidWatcher : IDisposable
{
    // GUID_LIDSWITCH_STATE_CHANGE = {BA3E0F4D-B817-4094-A2D1-D56379E6A0F3}
    private static readonly Guid GUID_LIDSWITCH_STATE_CHANGE =
        new("BA3E0F4D-B817-4094-A2D1-D56379E6A0F3");

    private const int WM_POWERBROADCAST = 0x0218;
    private const int PBT_POWERSETTINGCHANGE = 0x8013;

    public event EventHandler? LidClosed;

    public event EventHandler? LidOpened;

    public bool IsRunning { get; private set; }

    private LidNotificationWindow? _window;

    public void Start()
    {
        if (IsRunning)
        {
            return;
        }

        _window = new LidNotificationWindow(this);
        IsRunning = true;
    }

    public void Stop()
    {
        if (!IsRunning)
        {
            return;
        }

        _window?.DestroyHandle();
        _window = null;
        IsRunning = false;
    }

    public void Dispose()
    {
        Stop();
    }

    /// <summary>
    /// Hidden native window that receives WM_POWERBROADCAST messages with
    /// the lid-switch power setting notification.
    /// </summary>
    private sealed class LidNotificationWindow : NativeWindow
    {
        private readonly LidWatcher _owner;
        private IntPtr _registrationHandle;

        public LidNotificationWindow(LidWatcher owner)
        {
            _owner = owner;

            var cp = new CreateParams
            {
                Caption = "SmartWiFi_LidWatcher",
                // Message-only window (HWND_MESSAGE).
                Parent = new IntPtr(-3)
            };
            CreateHandle(cp);

            var guid = GUID_LIDSWITCH_STATE_CHANGE;
            _registrationHandle = RegisterPowerSettingNotification(
                Handle,
                ref guid,
                DEVICE_NOTIFY_WINDOW_HANDLE);
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_POWERBROADCAST && m.WParam == (IntPtr)PBT_POWERSETTINGCHANGE)
            {
                var ps = Marshal.PtrToStructure<POWERBROADCAST_SETTING>(m.LParam);
                if (ps.PowerSetting == GUID_LIDSWITCH_STATE_CHANGE)
                {
                    // Data == 0: lid closed, Data == 1: lid opened.
                    if (ps.Data == 0)
                    {
                        _owner.LidClosed?.Invoke(_owner, EventArgs.Empty);
                    }
                    else
                    {
                        _owner.LidOpened?.Invoke(_owner, EventArgs.Empty);
                    }
                }
            }

            base.WndProc(ref m);
        }

        public override void DestroyHandle()
        {
            if (_registrationHandle != IntPtr.Zero)
            {
                UnregisterPowerSettingNotification(_registrationHandle);
                _registrationHandle = IntPtr.Zero;
            }

            base.DestroyHandle();
        }
    }

    // ── P/Invoke declarations ──────────────────────────────────────

    private const int DEVICE_NOTIFY_WINDOW_HANDLE = 0x00000000;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr RegisterPowerSettingNotification(
        IntPtr hRecipient,
        ref Guid powerSettingGuid,
        int flags);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterPowerSettingNotification(IntPtr handle);

    [StructLayout(LayoutKind.Sequential)]
    private struct POWERBROADCAST_SETTING
    {
        public Guid PowerSetting;
        public int DataLength;
        public int Data;
    }
}
