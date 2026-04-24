# SmartWiFi — Deterministic Reconnect Fix (2026-04-24)

## Summary

Two files changed: `NativeWifi.cs` and `WiFiManager.cs`. No other files were modified.

---

## Problem 1: "No remembered WiFi profile is available to reconnect"

**Root cause:** `Connect()` depended on `LastConnectedProfileName` which was only written when
`GetStatus()` could read the active profile — but `WlanQueryInterface(CurrentConnection)` and
`netsh` both require Location Services to return profile names. With Location Services off the
setting was never written, so `Connect()` bailed immediately.

**Fix:** Added `NativeWifi.GetSavedProfiles()` backed by `WlanGetProfileList` (no Location
Services needed). `Connect()` now walks the saved profile list instead of depending solely on
the cached setting.

## Problem 2: Connect/disconnect cycling for ~1 minute before connecting

**Root cause (three issues):**
1. Each `WlanConnect()` call cancels the previous in-progress connection — iterating profiles
   rapidly caused visible connect/disconnect churn.
2. The per-profile wait was only 2 s (`maxAttempts: 4`), but WPA authentication takes 3-5 s —
   so even the correct profile was abandoned before its handshake finished.
3. A per-profile `netsh wlan connect` fallback doubled the churn.

**Fix:**
- `LastConnectedProfileName` is moved to the front of the candidate list (new
  `PrioritizeCandidates()` helper) so the right profile is tried first.
- Wait restored to 4 s (8 × 500 ms) per profile — enough for WPA handshake.
- When `TryConnect` returns false (API rejected), the profile is skipped entirely (no netsh).
- Profile iteration capped at 5 to bound worst-case latency.

---

## Files Changed

### 1. `SmartWiFi/NativeWifi.cs`

**What was added:**
- `GetSavedProfiles(string adapterName)` — public method returning saved WiFi profile names
  in Windows priority order via `WlanGetProfileList` (no Location Services needed).
- `WlanGetProfileList` P/Invoke declaration.
- `WlanProfileInfoListHeader` struct.
- `WlanProfileInfo` struct.

### 2. `SmartWiFi/WiFiManager.cs`

**What was changed:**
- `Connect()` — completely rewritten reconnect logic:
  - Uses `NativeWifi.GetSavedProfiles()` + `PrioritizeCandidates()` instead of checking
    only `LastConnectedProfileName`.
  - Iterates up to 5 profiles, skipping any the API rejects.
  - Gives each accepted profile 4 s for WPA handshake.
  - Saves the winning profile to `LastConnectedProfileName`.
- Added `PrioritizeCandidates()` — moves `LastConnectedProfileName` to front of candidate list.
- `WaitForConnectionState()` — added `maxAttempts` parameter (default 8, same as before).

---

## Full Final File Contents

Below are the complete, final versions of both files. Copy-paste these to replace the files
on your other PC.

---

### `SmartWiFi/NativeWifi.cs` (full file)

```csharp
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Text;

namespace SmartWiFi;

internal static class NativeWifi
{
    private const uint ClientVersion = 2;

    public static NativeWifiConnectionInfo? TryGetConnectionInfo(string adapterName)
    {
        var adapter = NetworkInterface.GetAllNetworkInterfaces()
            .FirstOrDefault(candidate =>
                candidate.NetworkInterfaceType == NetworkInterfaceType.Wireless80211 &&
                string.Equals(candidate.Name, adapterName, StringComparison.OrdinalIgnoreCase));

        if (adapter is null || !Guid.TryParse(adapter.Id, out var adapterGuid))
        {
            return null;
        }

        var result = WlanOpenHandle(ClientVersion, IntPtr.Zero, out _, out var clientHandle);
        if (result != 0)
        {
            return null;
        }

        try
        {
            result = WlanEnumInterfaces(clientHandle, IntPtr.Zero, out var interfaceListPointer);
            if (result != 0 || interfaceListPointer == IntPtr.Zero)
            {
                return null;
            }

            try
            {
                var header = Marshal.PtrToStructure<WlanInterfaceInfoListHeader>(interfaceListPointer);
                var itemOffset = Marshal.SizeOf<WlanInterfaceInfoListHeader>();
                var itemSize = Marshal.SizeOf<WlanInterfaceInfo>();

                for (var index = 0; index < header.NumberOfItems; index++)
                {
                    var itemPointer = IntPtr.Add(interfaceListPointer, itemOffset + (index * itemSize));
                    var interfaceInfo = Marshal.PtrToStructure<WlanInterfaceInfo>(itemPointer);
                    if (interfaceInfo.InterfaceGuid != adapterGuid)
                    {
                        continue;
                    }

                    var connectionState = interfaceInfo.State switch
                    {
                        WlanInterfaceState.Connected => true,
                        WlanInterfaceState.Disconnected => false,
                        _ => (bool?)null
                    };

                    if (connectionState != true)
                    {
                        return new NativeWifiConnectionInfo(interfaceInfo.InterfaceDescription, connectionState, null, null);
                    }

                    result = WlanQueryInterface(
                        clientHandle,
                        interfaceInfo.InterfaceGuid,
                        WlanIntfOpcode.CurrentConnection,
                        IntPtr.Zero,
                        out _,
                        out var connectionAttributesPointer,
                        out _);

                    if (result != 0 || connectionAttributesPointer == IntPtr.Zero)
                    {
                        return new NativeWifiConnectionInfo(interfaceInfo.InterfaceDescription, connectionState, null, null);
                    }

                    try
                    {
                        var attributes = Marshal.PtrToStructure<WlanConnectionAttributes>(connectionAttributesPointer);
                        var ssid = GetSsid(attributes.AssociationAttributes.Dot11Ssid);
                        return new NativeWifiConnectionInfo(
                            interfaceInfo.InterfaceDescription,
                            true,
                            attributes.ProfileName,
                            ssid);
                    }
                    finally
                    {
                        WlanFreeMemory(connectionAttributesPointer);
                    }
                }
            }
            finally
            {
                WlanFreeMemory(interfaceListPointer);
            }
        }
        finally
        {
            WlanCloseHandle(clientHandle, IntPtr.Zero);
        }

        return null;
    }

    /// <summary>
    /// Connects the specified adapter to a saved WiFi profile using the native WLAN API.
    /// This bypasses the Location Services requirement that netsh wlan enforces.
    /// </summary>
    public static bool TryConnect(string adapterName, string profileName)
    {
        var adapterGuid = ResolveAdapterGuid(adapterName);
        if (adapterGuid is null)
        {
            return false;
        }

        var result = WlanOpenHandle(ClientVersion, IntPtr.Zero, out _, out var clientHandle);
        if (result != 0)
        {
            return false;
        }

        try
        {
            var parameters = new WlanConnectionParameters
            {
                ConnectionMode = WlanConnectionMode.Profile,
                Profile = profileName,
                Dot11Ssid = IntPtr.Zero,
                Dot11BssType = Dot11BssType.Any,
                DesiredBssidList = IntPtr.Zero,
                Flags = 0
            };

            result = WlanConnect(clientHandle, adapterGuid.Value, ref parameters, IntPtr.Zero);
            return result == 0;
        }
        finally
        {
            WlanCloseHandle(clientHandle, IntPtr.Zero);
        }
    }

    /// <summary>
    /// Disconnects the specified adapter using the native WLAN API.
    /// This bypasses the Location Services requirement that netsh wlan enforces.
    /// </summary>
    public static bool TryDisconnect(string adapterName)
    {
        var adapterGuid = ResolveAdapterGuid(adapterName);
        if (adapterGuid is null)
        {
            return false;
        }

        var result = WlanOpenHandle(ClientVersion, IntPtr.Zero, out _, out var clientHandle);
        if (result != 0)
        {
            return false;
        }

        try
        {
            result = WlanDisconnect(clientHandle, adapterGuid.Value, IntPtr.Zero);
            return result == 0;
        }
        finally
        {
            WlanCloseHandle(clientHandle, IntPtr.Zero);
        }
    }

    /// <summary>
    /// Returns the saved WiFi profile names for the specified adapter,
    /// in Windows priority order (most preferred first).
    /// Uses WlanGetProfileList which does NOT require Location Services.
    /// Returns an empty list on any failure — never throws.
    /// </summary>
    public static IReadOnlyList<string> GetSavedProfiles(string adapterName)
    {
        var adapterGuid = ResolveAdapterGuid(adapterName);
        if (adapterGuid is null)
        {
            return Array.Empty<string>();
        }

        var result = WlanOpenHandle(ClientVersion, IntPtr.Zero, out _, out var clientHandle);
        if (result != 0)
        {
            return Array.Empty<string>();
        }

        try
        {
            result = WlanGetProfileList(clientHandle, adapterGuid.Value, IntPtr.Zero, out var profileListPointer);
            if (result != 0 || profileListPointer == IntPtr.Zero)
            {
                return Array.Empty<string>();
            }

            try
            {
                var header = Marshal.PtrToStructure<WlanProfileInfoListHeader>(profileListPointer);
                var profiles = new List<string>(header.NumberOfItems);
                var itemOffset = Marshal.SizeOf<WlanProfileInfoListHeader>();
                var itemSize = Marshal.SizeOf<WlanProfileInfo>();

                for (var index = 0; index < header.NumberOfItems; index++)
                {
                    var itemPointer = IntPtr.Add(profileListPointer, itemOffset + (index * itemSize));
                    var profileInfo = Marshal.PtrToStructure<WlanProfileInfo>(itemPointer);

                    if (!string.IsNullOrWhiteSpace(profileInfo.ProfileName))
                    {
                        profiles.Add(profileInfo.ProfileName);
                    }
                }

                return profiles;
            }
            finally
            {
                WlanFreeMemory(profileListPointer);
            }
        }
        finally
        {
            WlanCloseHandle(clientHandle, IntPtr.Zero);
        }
    }

    private static Guid? ResolveAdapterGuid(string adapterName)
    {
        var adapter = NetworkInterface.GetAllNetworkInterfaces()
            .FirstOrDefault(candidate =>
                candidate.NetworkInterfaceType == NetworkInterfaceType.Wireless80211 &&
                string.Equals(candidate.Name, adapterName, StringComparison.OrdinalIgnoreCase));

        if (adapter is null || !Guid.TryParse(adapter.Id, out var guid))
        {
            return null;
        }

        return guid;
    }

    private static string? GetSsid(Dot11Ssid ssid)
    {
        if (ssid.Length == 0 || ssid.Ssid is null)
        {
            return null;
        }

        var length = Math.Min((int)ssid.Length, ssid.Ssid.Length);
        return Encoding.ASCII.GetString(ssid.Ssid, 0, length);
    }

    [DllImport("wlanapi.dll")]
    private static extern uint WlanOpenHandle(
        uint clientVersion,
        IntPtr reserved,
        out uint negotiatedVersion,
        out IntPtr clientHandle);

    [DllImport("wlanapi.dll")]
    private static extern uint WlanCloseHandle(IntPtr clientHandle, IntPtr reserved);

    [DllImport("wlanapi.dll")]
    private static extern uint WlanEnumInterfaces(
        IntPtr clientHandle,
        IntPtr reserved,
        out IntPtr interfaceList);

    [DllImport("wlanapi.dll")]
    private static extern void WlanFreeMemory(IntPtr pointer);

    [DllImport("wlanapi.dll")]
    private static extern uint WlanQueryInterface(
        IntPtr clientHandle,
        Guid interfaceGuid,
        WlanIntfOpcode opcode,
        IntPtr reserved,
        out int dataSize,
        out IntPtr data,
        out WlanOpcodeValueType opcodeValueType);

    [DllImport("wlanapi.dll")]
    private static extern uint WlanConnect(
        IntPtr clientHandle,
        [MarshalAs(UnmanagedType.LPStruct)] Guid interfaceGuid,
        ref WlanConnectionParameters connectionParameters,
        IntPtr reserved);

    [DllImport("wlanapi.dll")]
    private static extern uint WlanDisconnect(
        IntPtr clientHandle,
        [MarshalAs(UnmanagedType.LPStruct)] Guid interfaceGuid,
        IntPtr reserved);

    [DllImport("wlanapi.dll")]
    private static extern uint WlanGetProfileList(
        IntPtr clientHandle,
        [MarshalAs(UnmanagedType.LPStruct)] Guid interfaceGuid,
        IntPtr reserved,
        out IntPtr profileList);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WlanConnectionParameters
    {
        public WlanConnectionMode ConnectionMode;

        [MarshalAs(UnmanagedType.LPWStr)]
        public string Profile;

        /// <summary>PDOT11_SSID — set to IntPtr.Zero for profile-based connections.</summary>
        public IntPtr Dot11Ssid;

        public IntPtr DesiredBssidList;
        public Dot11BssType Dot11BssType;
        public uint Flags;
    }

    internal sealed record NativeWifiConnectionInfo(
        string InterfaceDescription,
        bool? IsConnected,
        string? ProfileName,
        string? Ssid);

    [StructLayout(LayoutKind.Sequential)]
    private struct WlanInterfaceInfoListHeader
    {
        public int NumberOfItems;
        public int Index;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WlanProfileInfoListHeader
    {
        public int NumberOfItems;
        public int Index;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WlanProfileInfo
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string ProfileName;

        public uint Flags;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WlanInterfaceInfo
    {
        public Guid InterfaceGuid;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string InterfaceDescription;

        public WlanInterfaceState State;
    }

    private enum WlanInterfaceState
    {
        NotReady = 0,
        Connected = 1,
        AdHocNetworkFormed = 2,
        Disconnecting = 3,
        Disconnected = 4,
        Associating = 5,
        Discovering = 6,
        Authenticating = 7
    }

    private enum WlanIntfOpcode
    {
        AutoconfEnabled = 1,
        BackgroundScanEnabled = 2,
        MediaStreamingMode = 3,
        RadioState = 4,
        BssType = 5,
        InterfaceState = 6,
        CurrentConnection = 7
    }

    private enum WlanOpcodeValueType
    {
        QueryOnly = 0,
        SetByGroupPolicy = 1,
        SetByUser = 2,
        Invalid = 3
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WlanConnectionAttributes
    {
        public WlanInterfaceState State;
        public WlanConnectionMode ConnectionMode;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string ProfileName;

        public WlanAssociationAttributes AssociationAttributes;
        public WlanSecurityAttributes SecurityAttributes;
    }

    private enum WlanConnectionMode
    {
        Profile = 0,
        TemporaryProfile = 1,
        DiscoverySecure = 2,
        DiscoveryUnsecure = 3,
        Auto = 4,
        Invalid = 5
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WlanAssociationAttributes
    {
        public Dot11Ssid Dot11Ssid;
        public Dot11BssType BssType;

        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 6)]
        public byte[] Bssid;

        public Dot11PhyType PhyType;
        public uint PhyIndex;
        public uint SignalQuality;
        public uint RxRate;
        public uint TxRate;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Dot11Ssid
    {
        public uint Length;

        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)]
        public byte[] Ssid;
    }

    private enum Dot11BssType
    {
        Infrastructure = 1,
        Independent = 2,
        Any = 3
    }

    private enum Dot11PhyType : uint
    {
        Unknown = 0,
        Any = 0xFFFFFFFF
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WlanSecurityAttributes
    {
        [MarshalAs(UnmanagedType.Bool)]
        public bool SecurityEnabled;

        [MarshalAs(UnmanagedType.Bool)]
        public bool OneXEnabled;

        public uint AuthAlgorithm;
        public uint CipherAlgorithm;
    }
}
```

---

### `SmartWiFi/WiFiManager.cs` (full file)

```csharp
using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Text.RegularExpressions;
using System.Threading;

namespace SmartWiFi;

public sealed class WiFiManager
{
    private readonly AppSettings _settings;

    public WiFiManager(AppSettings settings)
    {
        _settings = settings;
    }

    public WiFiConnectionStatus GetStatus()
    {
        var adapterName = EnsureAdapterName();
        if (string.IsNullOrWhiteSpace(adapterName))
        {
            return WiFiConnectionStatus.NotDetected("No WiFi adapter detected.");
        }

        var info = TryGetInterfaceInfo(adapterName);
        var connectionState = info?.IsConnected;
        var statusLabel = connectionState switch
        {
            true => "Connected",
            false => "Disconnected",
            null => "UNKNOWN"
        };

        if (connectionState == true && !string.IsNullOrWhiteSpace(info?.ProfileName))
        {
            _settings.LastConnectedProfileName = info.ProfileName;
            _settings.Save();
        }

        return new WiFiConnectionStatus(
            adapterName,
            true,
            connectionState,
            info?.ProfileName,
            info?.Ssid,
            $"WiFi {statusLabel}");
    }

    public WiFiCommandResult Connect()
    {
        var adapterName = EnsureAdapterName();
        if (string.IsNullOrWhiteSpace(adapterName))
        {
            return WiFiCommandResult.Failure("No WiFi adapter detected.");
        }

        var currentStatus = GetStatus();
        if (currentStatus.IsConnected == true)
        {
            return WiFiCommandResult.FromSuccess(
                changed: false,
                status: currentStatus,
                message: "WiFi is already connected.");
        }

        // Build the candidate profile list from Windows' saved profiles
        // (priority-ordered, does NOT require Location Services).
        var savedProfiles = NativeWifi.GetSavedProfiles(adapterName);
        var candidates = PrioritizeCandidates(savedProfiles, _settings.LastConnectedProfileName);

        if (candidates.Count == 0)
        {
            return WiFiCommandResult.Failure("No saved WiFi profiles found on this machine to reconnect.");
        }

        // Limit the number of profiles we attempt to avoid long connect/disconnect
        // cycling — each attempt involves a full WPA handshake wait (~4 s).
        const int maxProfilesToTry = 5;

        foreach (var profile in candidates.Take(maxProfilesToTry))
        {
            // Try the native WLAN API. If it returns false the API rejected the
            // request outright (invalid profile, adapter busy, etc.) — skip it
            // entirely. Calling netsh here would just trigger another visible
            // disconnect/reconnect cycle for a profile that won't work anyway.
            if (!NativeWifi.TryConnect(adapterName, profile))
            {
                continue;
            }

            // The API accepted the connection request. Give the full 4 s
            // (8 × 500 ms) for the WPA/WPA2 handshake to complete — 2 s was
            // too short and caused the adapter to cycle before authentication
            // could finish.
            var checkStatus = WaitForConnectionState(adapterName, expectedConnected: true, maxAttempts: 8);
            if (checkStatus.IsConnected == true)
            {
                // Persist this profile so future reconnects can use the
                // fast single-profile path without walking the list again.
                _settings.LastConnectedProfileName = profile;
                _settings.Save();

                return WiFiCommandResult.FromSuccess(
                    changed: true,
                    status: checkStatus,
                    message: "WiFi connected.");
            }
        }

        return WiFiCommandResult.Failure("Could not reconnect using any saved Windows profile.");
    }

    /// <summary>
    /// Builds the candidate list with <paramref name="preferredProfile"/> at the front
    /// (if it exists in the saved list), followed by the rest in Windows-priority order.
    /// If <paramref name="savedProfiles"/> is empty, falls back to a single-element list
    /// containing <paramref name="preferredProfile"/> (when non-blank).
    /// </summary>
    private static IReadOnlyList<string> PrioritizeCandidates(
        IReadOnlyList<string> savedProfiles,
        string? preferredProfile)
    {
        if (savedProfiles.Count == 0)
        {
            return string.IsNullOrWhiteSpace(preferredProfile)
                ? Array.Empty<string>()
                : new[] { preferredProfile };
        }

        if (string.IsNullOrWhiteSpace(preferredProfile))
        {
            return savedProfiles;
        }

        // Check whether the preferred profile is already in the list.
        var preferredIndex = -1;
        for (var i = 0; i < savedProfiles.Count; i++)
        {
            if (string.Equals(savedProfiles[i], preferredProfile, StringComparison.OrdinalIgnoreCase))
            {
                preferredIndex = i;
                break;
            }
        }

        if (preferredIndex < 0)
        {
            // Preferred profile is not in the Windows list — prepend it anyway
            // so we still attempt it first (it might have been removed from the
            // list but remain connectable).
            var result = new List<string>(savedProfiles.Count + 1) { preferredProfile };
            result.AddRange(savedProfiles);
            return result;
        }

        if (preferredIndex == 0)
        {
            // Already at the front — nothing to reorder.
            return savedProfiles;
        }

        // Move the preferred profile to the front; keep the rest in order.
        var reordered = new List<string>(savedProfiles.Count) { preferredProfile };
        for (var i = 0; i < savedProfiles.Count; i++)
        {
            if (i != preferredIndex)
            {
                reordered.Add(savedProfiles[i]);
            }
        }

        return reordered;
    }

    public WiFiCommandResult Disconnect()
    {
        var adapterName = EnsureAdapterName();
        if (string.IsNullOrWhiteSpace(adapterName))
        {
            return WiFiCommandResult.Failure("No WiFi adapter detected.");
        }

        var currentStatus = GetStatus();
        if (currentStatus.IsConnected == false)
        {
            return WiFiCommandResult.FromSuccess(changed: false, status: currentStatus, message: "WiFi is already disconnected.");
        }

        if (!string.IsNullOrWhiteSpace(currentStatus.ProfileName))
        {
            _settings.LastConnectedProfileName = currentStatus.ProfileName;
            _settings.Save();
        }

        // Try the native WLAN API first (no Location Services requirement).
        var nativeSuccess = NativeWifi.TryDisconnect(adapterName);
        if (!nativeSuccess)
        {
            // Fallback to netsh.
            var output = ExecuteNetsh($"wlan disconnect interface=\"{adapterName}\"");
            if (output.ExitCode != 0)
            {
                return WiFiCommandResult.Failure($"Failed to disconnect WiFi. {output.Output}".Trim());
            }
        }

        var finalStatus = WaitForConnectionState(adapterName, expectedConnected: false);
        if (finalStatus.IsConnected == false)
        {
            return WiFiCommandResult.FromSuccess(
                changed: true,
                status: finalStatus,
                message: "WiFi disconnected.");
        }

        return WiFiCommandResult.Failure("WiFi disconnect command completed, but the disconnected state could not be confirmed.");
    }

    public WiFiCommandResult DisconnectForSuspend()
    {
        var adapterName = EnsureAdapterName();
        if (string.IsNullOrWhiteSpace(adapterName))
        {
            return WiFiCommandResult.Failure("No WiFi adapter detected.");
        }

        var currentStatus = GetStatus();
        if (currentStatus.IsConnected == false)
        {
            return WiFiCommandResult.FromSuccess(changed: false, status: currentStatus, message: "WiFi is already disconnected.");
        }

        if (!string.IsNullOrWhiteSpace(currentStatus.ProfileName))
        {
            _settings.LastConnectedProfileName = currentStatus.ProfileName;
            _settings.Save();
        }

        // Try the native WLAN API first (no Location Services requirement).
        var nativeSuccess = NativeWifi.TryDisconnect(adapterName);
        if (!nativeSuccess)
        {
            // Fallback to netsh detached.
            var started = StartNetshDetached($"wlan disconnect interface=\"{adapterName}\"");
            if (!started)
            {
                return WiFiCommandResult.Failure("Failed to start the WiFi disconnect command before suspend.");
            }
        }

        return WiFiCommandResult.FromSuccess(
            changed: true,
            status: currentStatus with { IsConnected = false, Message = "WiFi disconnect requested." },
            message: "WiFi disconnect requested.");
    }

    private string? EnsureAdapterName()
    {
        if (!string.IsNullOrWhiteSpace(_settings.WifiAdapterName) && AdapterExists(_settings.WifiAdapterName))
        {
            return _settings.WifiAdapterName;
        }

        var detected = DetectPreferredAdapterName();
        if (string.IsNullOrWhiteSpace(detected))
        {
            _settings.WifiAdapterName = null;
            _settings.Save();
            return null;
        }

        if (!string.Equals(_settings.WifiAdapterName, detected, StringComparison.OrdinalIgnoreCase))
        {
            _settings.WifiAdapterName = detected;
            _settings.Save();
        }

        return detected;
    }

    private static bool AdapterExists(string adapterName)
    {
        return NetworkInterface.GetAllNetworkInterfaces()
            .Any(adapter =>
                adapter.NetworkInterfaceType == NetworkInterfaceType.Wireless80211 &&
                string.Equals(adapter.Name, adapterName, StringComparison.OrdinalIgnoreCase));
    }

    private static string? DetectPreferredAdapterName()
    {
        return NetworkInterface.GetAllNetworkInterfaces()
            .Where(adapter => adapter.NetworkInterfaceType == NetworkInterfaceType.Wireless80211)
            .OrderByDescending(adapter => adapter.OperationalStatus == OperationalStatus.Up)
            .Select(adapter => adapter.Name)
            .FirstOrDefault();
    }

    private WiFiConnectionStatus WaitForConnectionState(string adapterName, bool expectedConnected, int maxAttempts = 8)
    {
        for (var attempt = 0; attempt < maxAttempts; attempt++)
        {
            var status = GetStatus();
            if (status.IsConnected == expectedConnected)
            {
                return status;
            }

            Thread.Sleep(500);
        }

        return GetStatus();
    }

    private static WiFiInterfaceInfo? TryGetInterfaceInfo(string adapterName)
    {
        var nativeInfo = NativeWifi.TryGetConnectionInfo(adapterName);
        if (nativeInfo is not null)
        {
            return new WiFiInterfaceInfo(adapterName, nativeInfo.IsConnected, nativeInfo.ProfileName, nativeInfo.Ssid);
        }

        var output = ExecuteNetsh("wlan show interfaces");
        if (output.ExitCode != 0 || string.IsNullOrWhiteSpace(output.Output))
        {
            return null;
        }

        WiFiInterfaceInfoBuilder? current = null;

        foreach (var rawLine in output.Output.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None))
        {
            var separatorIndex = rawLine.IndexOf(':');
            if (separatorIndex < 0)
            {
                continue;
            }

            var key = rawLine[..separatorIndex].Trim();
            var value = rawLine[(separatorIndex + 1)..].Trim();
            if (string.IsNullOrWhiteSpace(key))
            {
                continue;
            }

            if (key.Equals("Name", StringComparison.OrdinalIgnoreCase))
            {
                if (current is not null && string.Equals(current.Name, adapterName, StringComparison.OrdinalIgnoreCase))
                {
                    return current.Build();
                }

                current = new WiFiInterfaceInfoBuilder
                {
                    Name = value
                };

                continue;
            }

            if (current is null)
            {
                continue;
            }

            if (key.Equals("State", StringComparison.OrdinalIgnoreCase))
            {
                current.State = value;
            }
            else if (key.Equals("Profile", StringComparison.OrdinalIgnoreCase))
            {
                current.ProfileName = value;
            }
            else if (key.Equals("SSID", StringComparison.OrdinalIgnoreCase) && !rawLine.TrimStart().StartsWith("BSSID", StringComparison.OrdinalIgnoreCase))
            {
                current.Ssid = value;
            }
        }

        if (current is not null && string.Equals(current.Name, adapterName, StringComparison.OrdinalIgnoreCase))
        {
            return current.Build();
        }

        return null;
    }

    private static ProcessResult ExecuteNetsh(string arguments)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "netsh",
                Arguments = arguments,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };

        process.Start();
        var standardOutput = process.StandardOutput.ReadToEnd();
        var standardError = process.StandardError.ReadToEnd();
        process.WaitForExit();

        var combinedOutput = string.Join(
            Environment.NewLine,
            new[] { standardOutput.Trim(), standardError.Trim() }.Where(value => !string.IsNullOrWhiteSpace(value)));

        return new ProcessResult(process.ExitCode, combinedOutput);
    }

    private static bool StartNetshDetached(string arguments)
    {
        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "netsh",
                    Arguments = arguments,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };

            return process.Start();
        }
        catch
        {
            return false;
        }
    }

    public sealed record WiFiConnectionStatus(
        string? AdapterName,
        bool AdapterDetected,
        bool? IsConnected,
        string? ProfileName,
        string? Ssid,
        string Message)
    {
        public static WiFiConnectionStatus NotDetected(string message)
        {
            return new(null, false, null, null, null, message);
        }
    }

    public sealed record WiFiCommandResult(bool Success, bool Changed, WiFiConnectionStatus? Status, string Message)
    {
        public static WiFiCommandResult FromSuccess(bool changed, WiFiConnectionStatus status, string message)
        {
            return new(true, changed, status, message);
        }

        public static WiFiCommandResult Failure(string message)
        {
            return new(false, false, null, message);
        }
    }

    private sealed record ProcessResult(int ExitCode, string Output);

    private sealed class WiFiInterfaceInfoBuilder
    {
        public string? Name { get; init; }

        public string? State { get; set; }

        public string? ProfileName { get; set; }

        public string? Ssid { get; set; }

        public WiFiInterfaceInfo Build()
        {
            var isConnected = State?.Equals("connected", StringComparison.OrdinalIgnoreCase) switch
            {
                true => true,
                false when State?.Equals("disconnected", StringComparison.OrdinalIgnoreCase) == true => false,
                _ => (bool?)null
            };

            return new WiFiInterfaceInfo(Name, isConnected, ProfileName, Ssid);
        }
    }

    private sealed record WiFiInterfaceInfo(string? Name, bool? IsConnected, string? ProfileName, string? Ssid);
}
```
