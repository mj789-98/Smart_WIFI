# SmartWiFi

SmartWiFi is a lightweight Windows tray application that monitors the laptop lid switch hardware, disconnects WiFi when the lid is closed, and reconnects it when the lid is opened — regardless of your Power Options lid-close action (Sleep, Hibernate, Turn off display, or Do nothing).

## Features

- Single-instance tray app with no main window
- Hardware lid-switch detection via `RegisterPowerSettingNotification` + `GUID_LIDSWITCH_STATE_CHANGE`
- WiFi disconnect and reconnect using `netsh wlan`
- Tray notifications for automatic WiFi changes
- Start-with-Windows toggle backed by the current user registry
- Settings persisted to `%AppData%\SmartWiFi\settings.json`

## Requirements

- Windows 10 or Windows 11
- .NET 7 SDK or newer to build this workspace
- Permissions to run `netsh wlan ...`

If reconnect fails, confirm that Windows already has a saved profile for that network and that `netsh wlan show interfaces` reports your wireless adapter correctly.

## Build

```powershell
dotnet build .\SmartWiFi.sln -c Release
```

## Run

```powershell
dotnet run --project .\SmartWiFi\SmartWiFi.csproj
```

## How It Works

- `Program.cs` enforces a single running instance and starts the tray application context.
- `TrayApp.cs` owns the tray icon, menu, app lifecycle, and lid-to-WiFi event flow.
- `LidWatcher.cs` uses `RegisterPowerSettingNotification` with `GUID_LIDSWITCH_STATE_CHANGE` to listen directly to the physical lid switch hardware, firing events regardless of the configured lid-close action.
- `WiFiManager.cs` auto-detects a wireless adapter, stores it in settings, disconnects with `netsh wlan disconnect`, and reconnects to the remembered profile with `netsh wlan connect`.
- `StartupManager.cs` toggles startup in `HKCU\Software\Microsoft\Windows\CurrentVersion\Run`.
- `AppSettings.cs` loads and saves `%AppData%\SmartWiFi\settings.json`.

## Notes

- The current workspace builds as `net7.0-windows` because the installed SDK on this machine is .NET 7. Retargeting to `.NET 8` is a one-line change in `SmartWiFi.csproj` once that SDK is installed.
- The original `SystemEvents.PowerModeChanged` approach was replaced with `RegisterPowerSettingNotification` + `GUID_LIDSWITCH_STATE_CHANGE` because the former only fires on Sleep/Hibernate transitions and is silent when the lid-close action is set to "Turn off display" or "Do nothing".
- The tray notifications are implemented with `NotifyIcon` notifications so the project builds without external NuGet downloads in this environment.
