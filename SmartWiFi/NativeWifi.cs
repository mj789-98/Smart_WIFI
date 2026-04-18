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
