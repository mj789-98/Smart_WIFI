using System.Text.Json;

namespace SmartWiFi;

public sealed class AppSettings
{
    private sealed class StoredSettings
    {
        public string? WifiAdapterName { get; set; }

        public string? LastConnectedProfileName { get; set; }

        public bool AutoStart { get; set; }

        public bool NotificationsEnabled { get; set; } = true;
    }

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    private AppSettings(string filePath)
    {
        FilePath = filePath;
    }

    public string FilePath { get; }

    public string? WifiAdapterName { get; set; }

    public string? LastConnectedProfileName { get; set; }

    public bool AutoStart { get; set; }

    public bool NotificationsEnabled { get; set; } = true;

    public static AppSettings Load()
    {
        var filePath = GetDefaultPath();
        var settings = new AppSettings(filePath);

        if (!File.Exists(filePath))
        {
            return settings;
        }

        try
        {
            var json = File.ReadAllText(filePath);
            var stored = JsonSerializer.Deserialize<StoredSettings>(json, SerializerOptions);
            if (stored is null)
            {
                return settings;
            }

            settings.WifiAdapterName = stored.WifiAdapterName;
            settings.LastConnectedProfileName = stored.LastConnectedProfileName;
            settings.AutoStart = stored.AutoStart;
            settings.NotificationsEnabled = stored.NotificationsEnabled;
        }
        catch
        {
            return settings;
        }

        return settings;
    }

    public void Save()
    {
        var directory = Path.GetDirectoryName(FilePath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var stored = new StoredSettings
        {
            WifiAdapterName = WifiAdapterName,
            LastConnectedProfileName = LastConnectedProfileName,
            AutoStart = AutoStart,
            NotificationsEnabled = NotificationsEnabled
        };

        var json = JsonSerializer.Serialize(stored, SerializerOptions);
        File.WriteAllText(FilePath, json);
    }

    private static string GetDefaultPath()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        return Path.Combine(appData, "SmartWiFi", "settings.json");
    }
}
