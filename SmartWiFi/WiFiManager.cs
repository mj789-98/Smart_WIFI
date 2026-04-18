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

        var profileName = _settings.LastConnectedProfileName;
        if (string.IsNullOrWhiteSpace(profileName))
        {
            return WiFiCommandResult.Failure("No remembered WiFi profile is available to reconnect.");
        }

        // Try the native WLAN API first (no Location Services requirement).
        var nativeSuccess = NativeWifi.TryConnect(adapterName, profileName);
        if (!nativeSuccess)
        {
            // Fallback to netsh.
            var output = ExecuteNetsh($"wlan connect name=\"{profileName}\" interface=\"{adapterName}\"");
            if (output.ExitCode != 0)
            {
                return WiFiCommandResult.Failure($"Failed to reconnect WiFi. {output.Output}".Trim());
            }
        }

        var finalStatus = WaitForConnectionState(adapterName, expectedConnected: true);
        if (finalStatus.IsConnected == true)
        {
            return WiFiCommandResult.FromSuccess(
                changed: true,
                status: finalStatus,
                message: "WiFi connected.");
        }

        return WiFiCommandResult.Failure("WiFi reconnect command completed, but the connection could not be confirmed.");
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

    private WiFiConnectionStatus WaitForConnectionState(string adapterName, bool expectedConnected)
    {
        for (var attempt = 0; attempt < 8; attempt++)
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
