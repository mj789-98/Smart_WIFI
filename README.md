# SmartWiFi

SmartWiFi is a lightweight Windows tray application that monitors the laptop lid switch hardware, disconnects WiFi when the lid is closed, and reconnects it when the lid is opened — regardless of your Power Options lid-close action (Sleep, Hibernate, Turn off display, or Do nothing).

## Features

- Single-instance tray app with no main window
- Hardware lid-switch detection via `RegisterPowerSettingNotification` + `GUID_LIDSWITCH_STATE_CHANGE`
- WiFi disconnect and reconnect using the native **WLAN API** (`wlanapi.dll`), with `netsh wlan` as a fallback
- Smart reconnect: remembers the last-used profile and tries it first; walks Windows' saved profile list (up to 5) if needed
- 4-second delayed reconnect after lid-open to let the adapter stabilise post-resume
- `DisconnectForSuspend` path for lid-close that fires the disconnect detached (non-blocking) to complete before the OS suspends
- Tray notifications for automatic WiFi changes (toggleable from tray menu)
- Start-with-Windows toggle backed by the current user registry (`HKCU`)
- Settings persisted to `%AppData%\SmartWiFi\settings.json`
- No external NuGet packages — built entirely on .NET 7 BCL + WinForms + P/Invoke

## Requirements

- Windows 10 or Windows 11
- .NET 7 SDK (or newer — retarget `<TargetFramework>` in `SmartWiFi.csproj` to upgrade)
- No admin rights required at runtime

If reconnect fails, confirm that Windows already has a saved profile for that network and that `netsh wlan show interfaces` reports your wireless adapter correctly.

## Build

```powershell
dotnet build .\SmartWiFi.sln -c Release
```

## Run

```powershell
dotnet run --project .\SmartWiFi\SmartWiFi.csproj
```

## Project Structure

```
SmartWiFi/
├── SmartWiFi.sln
├── SmartWiFi/
│   ├── SmartWiFi.csproj          # net7.0-windows, WinExe, no external packages
│   ├── app.manifest              # UAC: asInvoker (no elevation required)
│   ├── Program.cs                # Entry point — Mutex single-instance guard
│   ├── TrayApp.cs                # NotifyIcon, context menu, lid→WiFi event wiring
│   ├── LidWatcher.cs             # RegisterPowerSettingNotification watcher
│   ├── WiFiManager.cs            # Connect/Disconnect/GetStatus with retry logic
│   ├── NativeWifi.cs             # P/Invoke wrapper for wlanapi.dll
│   ├── NotificationService.cs    # Balloon-tip notifications via NotifyIcon
│   ├── StartupManager.cs         # HKCU Run registry toggle
│   └── AppSettings.cs            # JSON settings in %AppData%\SmartWiFi\
├── README.md
├── SmartWiFi_ProjectPlan.md      # Original design doc (may differ from impl)
├── lid_detection_flow.svg        # Architecture diagram
└── .gitignore
```

## How It Works

### `Program.cs`
Enforces a single running instance via a named `Mutex`, then starts the WinForms application context.

### `TrayApp.cs`
Owns the tray icon (programmatically drawn WiFi symbol — no `.ico` resource files), context menu, and the full lid-to-WiFi flow:

- On lid **close** → calls `WiFiManager.DisconnectForSuspend()` (non-blocking), sets `_disconnectedByLid = true`
- On lid **open** → starts a 4-second `Timer`; on tick calls `WiFiManager.Connect()` only if `_disconnectedByLid` is set
- Manual connect/disconnect from the menu uses `WiFiManager.Connect()` / `WiFiManager.Disconnect()` and does not set `_disconnectedByLid`

Menu items:
| Item | Behaviour |
|---|---|
| WiFi: Connected / Disconnected | Status label (disabled) |
| Connect WiFi | Manual reconnect |
| Disconnect WiFi | Manual disconnect |
| Start with Windows | Toggles `HKCU\...\Run` via `StartupManager` |
| Notifications | Toggles balloon-tip notifications on/off |
| Exit | Disposes resources and exits |

### `LidWatcher.cs`
Creates a message-only `NativeWindow` and calls `RegisterPowerSettingNotification` with `GUID_LIDSWITCH_STATE_CHANGE` (`{BA3E0F4D-B817-4094-A2D1-D56379E6A0F3}`). Fires `LidClosed` / `LidOpened` events regardless of the Power Options lid-close action. Automatically started/stopped by `TrayApp.RefreshUi()` when a WiFi adapter is present or absent.

### `WiFiManager.cs`
Wraps all WiFi state and control:

- **`GetStatus()`** — calls `NativeWifi.TryGetConnectionInfo()` first; falls back to parsing `netsh wlan show interfaces`. Persists the current profile name to settings when connected.
- **`Connect()`** — builds a prioritised candidate list from `NativeWifi.GetSavedProfiles()` (Windows priority order), promotes `LastConnectedProfileName` to the front. Tries up to 5 profiles via `NativeWifi.TryConnect()`, waits up to 4 s per attempt for the WPA handshake.
- **`Disconnect()`** — calls `NativeWifi.TryDisconnect()`; falls back to `netsh wlan disconnect`. Confirms disconnected state before returning.
- **`DisconnectForSuspend()`** — same as `Disconnect()` but fires the `netsh` fallback detached (non-blocking) so it completes before the OS suspends.

### `NativeWifi.cs`
P/Invoke wrapper for `wlanapi.dll`. Provides:
- `TryGetConnectionInfo()` — `WlanOpenHandle` → `WlanEnumInterfaces` → `WlanQueryInterface(CurrentConnection)` to read SSID and profile name without requiring Location Services.
- `TryConnect()` — `WlanConnect` with `WlanConnectionMode.Profile`.
- `TryDisconnect()` — `WlanDisconnect`.
- `GetSavedProfiles()` — `WlanGetProfileList` (does not require Location Services).

### `NotificationService.cs`
Thin wrapper around `NotifyIcon.ShowBalloonTip` (3 seconds). No external NuGet package — uses the built-in WinForms balloon API.

### `StartupManager.cs`
Reads and writes `HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Run` to register the app for startup under the current user account. No admin rights required.

### `AppSettings.cs`
Loads and saves `%AppData%\SmartWiFi\settings.json` using `System.Text.Json`:

```json
{
  "wifiAdapterName": "Wi-Fi",
  "lastConnectedProfileName": "MyHomeNetwork",
  "autoStart": true,
  "notificationsEnabled": true
}
```

## Event Flow

```
Lid Closed
    └─► LidWatcher fires LidClosed
            └─► TrayApp.HandleLidClosed()
                    ├─► WiFiManager.DisconnectForSuspend()   (non-blocking)
                    ├─► _disconnectedByLid = true
                    └─► RefreshUi()

Lid Opened
    └─► LidWatcher fires LidOpened
            └─► TrayApp.HandleLidOpened()
                    ├─► Start 4-second Timer
                    └─► On tick: WiFiManager.Connect()  (only if _disconnectedByLid)
                            ├─► NativeWifi.TryConnect(lastProfile)
                            ├─► If fails: walk saved profile list (up to 5)
                            ├─► _disconnectedByLid = false
                            └─► NotificationService.ShowConnected()  (if notifications on)
```

## Implementation Notes

- **Native WLAN API over `netsh`** — the native API (`wlanapi.dll`) does not require Location Services and runs without a visible console window. `netsh` is kept as a fallback for environments where the API is unavailable.
- **`GUID_LIDSWITCH_STATE_CHANGE` over `SystemEvents.PowerModeChanged`** — `PowerModeChanged` only fires on Sleep/Hibernate transitions and is silent when the lid-close action is "Turn off display" or "Do nothing". The GUID monitors the physical lid switch hardware directly.
- **Programmatic tray icon** — the WiFi icon is drawn at runtime using `Graphics.DrawArc` / `FillEllipse` so no `.ico` resource files are embedded in the assembly.
- **No external NuGet packages** — the project builds against the .NET 7 BCL + WinForms only. The original plan referenced `Microsoft.Toolkit.Uwp.Notifications` and `Newtonsoft.Json`; neither is used.
- **Retargeting to .NET 8+** — change `<TargetFramework>net7.0-windows</TargetFramework>` in `SmartWiFi.csproj` once a newer SDK is installed.

## Implemented Status

| Feature | Status |
|---|---|
| Single-instance Mutex | Done |
| Lid detection via `GUID_LIDSWITCH_STATE_CHANGE` | Done |
| WiFi disconnect on lid close | Done |
| WiFi reconnect on lid open (4 s delay) | Done |
| Native WLAN API (connect/disconnect/profiles) | Done |
| Smart profile prioritisation | Done |
| Balloon-tip notifications (toggleable) | Done |
| Start-with-Windows registry toggle | Done |
| Settings persisted to JSON | Done |
| Programmatic tray icon (no .ico files) | Done |
