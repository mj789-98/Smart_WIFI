# SmartWiFi — Lid-Aware WiFi Manager for Windows

## Project Overview

A lightweight Windows system tray application that automatically disables WiFi when the laptop lid is closed and re-enables it when the lid is opened. The app runs silently in the background, shows a tray icon with status, and fires Windows toast notifications on every WiFi state change.

---

## Goals

- Detect lid close / lid open events on Windows
- Automatically disable / enable the WiFi adapter on those events
- Show Windows toast notifications ("WiFi turned off — lid closed", "WiFi turned on — lid opened")
- Run as a system tray app (no visible window, just a tray icon)
- Auto-start with Windows on boot (optional, toggleable from tray menu)
- Zero user friction — install and forget

---

## Tech Stack

| Layer | Choice | Reason |
|---|---|---|
| Language | **C# (.NET 8)** | Native Windows APIs, WMI access, best tray support |
| UI / Tray | **WinForms** (NotifyIcon) | Simplest tray icon + context menu on Windows |
| Lid detection | **`RegisterPowerSettingNotification`** with `GUID_LIDSWITCH_STATE_CHANGE` | Monitors physical lid switch — fires regardless of lid-close action |
| WiFi control | **`netsh` CLI** / `System.Net.NetworkInformation` | `netsh interface set interface` to enable/disable adapter |
| Notifications | **Windows Toast (Microsoft.Toolkit.Uwp.Notifications)** | Native Windows 10/11 toast notifications |
| Auto-start | **Windows Registry** (`HKCU\Software\Microsoft\Windows\CurrentVersion\Run`) | Per-user startup without admin rights |

---

## Project Structure

```
SmartWiFi/
├── SmartWiFi.sln
├── SmartWiFi/
│   ├── SmartWiFi.csproj
│   ├── Program.cs                  # Entry point — runs app in tray mode
│   ├── TrayApp.cs                  # NotifyIcon setup, context menu, app lifecycle
│   ├── LidWatcher.cs               # Lid switch listener via RegisterPowerSettingNotification
│   ├── WiFiManager.cs              # Enable/disable WiFi adapter via netsh
│   ├── NotificationService.cs      # Windows toast notification wrapper
│   ├── StartupManager.cs           # Registry-based auto-start toggle
│   ├── AppSettings.cs              # Persisted settings (auto-start, adapter name)
│   └── Resources/
│       ├── icon_wifi_on.ico
│       └── icon_wifi_off.ico
├── README.md
└── .gitignore
```

---

## Module Breakdown

### 1. `Program.cs` — Entry Point

- Set application to run as a single instance (Mutex check)
- Disable default form visibility
- Start `Application.Run(new TrayApp())`

### 2. `TrayApp.cs` — System Tray Controller

- Create `NotifyIcon` with icon and tooltip
- Build right-click context menu:
  - **WiFi: ON / OFF** (current status, greyed out label)
  - **Enable WiFi** / **Disable WiFi** (manual override toggle)
  - Separator
  - **Start with Windows** (checkmark toggle)
  - **Exit**
- On init: detect current WiFi state and set appropriate tray icon
- Wire up `LidWatcher` events → call `WiFiManager` → call `NotificationService`

### 3. `LidWatcher.cs` — Lid Event Detector

- **Approach:** `RegisterPowerSettingNotification` with `GUID_LIDSWITCH_STATE_CHANGE` (`{BA3E0F4D-B817-4094-A2D1-D56379E6A0F3}`)
- Creates a hidden message-only `NativeWindow` that receives `WM_POWERBROADCAST` / `PBT_POWERSETTINGCHANGE` messages
- The `POWERBROADCAST_SETTING.Data` field indicates lid state: `0` = closed, `1` = opened
- Expose two C# events: `LidClosed` and `LidOpened`
- Start/stop watcher with app lifecycle
- Properly unregisters the notification via `UnregisterPowerSettingNotification` on dispose

> **Note:** The original plan used `SystemEvents.PowerModeChanged`, but this only fires on Sleep/Hibernate transitions. When the lid-close action is set to "Turn off display" or "Do nothing", no event fires. `GUID_LIDSWITCH_STATE_CHANGE` monitors the physical lid switch hardware directly, so it works regardless of the configured power plan action.

### 4. `WiFiManager.cs` — Adapter Control

- On startup: auto-detect the active WiFi adapter name using:
  ```csharp
  NetworkInterface.GetAllNetworkInterfaces()
    .Where(n => n.NetworkInterfaceType == NetworkInterfaceType.Wireless80211)
  ```
- Store adapter name in `AppSettings`
- To disable:
  ```
  netsh interface set interface "Wi-Fi" admin=disabled
  ```
- To enable:
  ```
  netsh interface set interface "Wi-Fi" admin=enabled
  ```
- Run via `Process.Start` with hidden window, check exit code
- Return `bool success` to caller
- Handle edge case: adapter not found, already in requested state

### 5. `NotificationService.cs` — Toast Notifications

- Use `Microsoft.Toolkit.Uwp.Notifications` NuGet package
- Show toast on WiFi disable:
  - Title: `SmartWiFi`
  - Body: `📴 WiFi disabled — lid closed`
- Show toast on WiFi enable:
  - Title: `SmartWiFi`
  - Body: `📶 WiFi enabled — lid opened`
- Suppress notification if action was triggered manually (not by lid event)

### 6. `StartupManager.cs` — Auto-Start Toggle

- Read/write to registry key:
  ```
  HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Run
  Value name: SmartWiFi
  Value data: "C:\path\to\SmartWiFi.exe"
  ```
- Expose `bool IsEnabled { get; }` and `void Toggle()`
- No admin rights required (HKCU)

### 7. `AppSettings.cs` — Persisted Configuration

- Save settings to `%AppData%\SmartWiFi\settings.json`
- Fields:
  ```json
  {
    "wifiAdapterName": "Wi-Fi",
    "autoStart": true,
    "notificationsEnabled": true
  }
  ```
- Load on startup, save on any change

---

## Event Flow

```
Lid Closed
    └─► LidWatcher fires OnLidClosed
            └─► TrayApp handles event
                    ├─► WiFiManager.Disable()
                    ├─► TrayApp updates icon → wifi_off.ico
                    └─► NotificationService.ShowDisabled()

Lid Opened
    └─► LidWatcher fires OnLidOpened
            └─► TrayApp handles event
                    ├─► WiFiManager.Enable()
                    ├─► TrayApp updates icon → wifi_on.ico
                    └─► NotificationService.ShowEnabled()
```

---

## NuGet Dependencies

```xml
<PackageReference Include="Microsoft.Toolkit.Uwp.Notifications" Version="7.1.3" />
<PackageReference Include="System.Management" Version="8.0.0" />
<PackageReference Include="Newtonsoft.Json" Version="13.0.3" />
```

---

## Build & Run Instructions (for Codex / Claude Code)

1. Target framework: `net8.0-windows`
2. Output type: `WinExe` (no console window)
3. Build command:
   ```bash
   dotnet build SmartWiFi.sln -c Release
   ```
4. Run:
   ```bash
   dotnet run --project SmartWiFi/SmartWiFi.csproj
   ```
5. The app must be run with sufficient permissions to run `netsh` commands. If disabling the adapter fails, prompt the user to run as Administrator once, or use a UAC manifest.

---

## app.manifest (UAC — Optional)

If `netsh` commands require elevation, add a manifest:

```xml
<requestedExecutionLevel level="requireAdministrator" uiAccess="false" />
```

Or attempt without elevation first and only prompt if the command fails.

---

## Edge Cases to Handle

| Scenario | Expected Behavior |
|---|---|
| WiFi already off when lid closes | Skip disable, no notification |
| WiFi manually disabled by user | Do not override on lid open |
| Multiple WiFi adapters | Pick first active wireless adapter; allow override in settings |
| Sleep vs hibernate | Both should trigger disable; resume should trigger enable |
| App crash / restart | Re-detect WiFi state on startup and sync tray icon |
| No WiFi adapter found | Show tray tooltip error, disable lid watching |

---

## Deliverables

- [ ] Working C# WinForms tray app
- [x] Lid open/close detection via `RegisterPowerSettingNotification` + `GUID_LIDSWITCH_STATE_CHANGE`
- [ ] WiFi enable/disable via `netsh`
- [ ] Windows toast notifications on state change
- [ ] Auto-start toggle in tray menu
- [ ] Settings persisted to `%AppData%\SmartWiFi\settings.json`
- [ ] Single-instance enforcement via Mutex
- [ ] README with setup instructions

---

## Out of Scope (Future)

- Per-network profiles (e.g. disable only on specific SSIDs)
- Scheduled WiFi rules
- Support for Ethernet or Bluetooth toggling
- Linux / macOS support
