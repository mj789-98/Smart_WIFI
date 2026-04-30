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

        // Only skip the netsh path when native gave us a usable profile name.
        // WlanQueryInterface(CurrentConnection) can succeed but return an empty
        // profile name (e.g. right after connecting to a new network, or when
        // the connection uses a temporary/discovery profile). If we returned
        // here in that case, LastConnectedProfileName would stay stale and
        // SmartWiFi would try to reconnect to the wrong network.
        if (nativeInfo is not null && !string.IsNullOrWhiteSpace(nativeInfo.ProfileName))
        {
            return new WiFiInterfaceInfo(adapterName, nativeInfo.IsConnected, nativeInfo.ProfileName, nativeInfo.Ssid);
        }

        var output = ExecuteNetsh("wlan show interfaces");
        if (output.ExitCode != 0 || string.IsNullOrWhiteSpace(output.Output))
        {
            // netsh unavailable — return whatever native gave us (may have connection
            // state without a profile name, which is still better than null).
            return nativeInfo is not null
                ? new WiFiInterfaceInfo(adapterName, nativeInfo.IsConnected, null, nativeInfo.Ssid)
                : null;
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
                    break;
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
            var netshResult = current.Build();

            // Prefer native connection state (comes from the WLAN state machine,
            // not string parsing) but fill in the profile name from netsh when
            // native didn't supply one.
            if (nativeInfo is not null)
            {
                return new WiFiInterfaceInfo(
                    adapterName,
                    nativeInfo.IsConnected,
                    string.IsNullOrWhiteSpace(nativeInfo.ProfileName) ? netshResult.ProfileName : nativeInfo.ProfileName,
                    nativeInfo.Ssid ?? netshResult.Ssid);
            }

            return netshResult;
        }

        return nativeInfo is not null
            ? new WiFiInterfaceInfo(adapterName, nativeInfo.IsConnected, null, nativeInfo.Ssid)
            : null;
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
