using Microsoft.Win32;

namespace SmartWiFi;

public sealed class StartupManager
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";

    private readonly string _applicationName;
    private readonly string _command;

    public StartupManager(string applicationName, string executablePath)
    {
        _applicationName = applicationName;
        _command = $"\"{executablePath}\"";
    }

    public bool IsEnabled
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
            return key?.GetValue(_applicationName) is string value && !string.IsNullOrWhiteSpace(value);
        }
    }

    public bool SetEnabled(bool enabled, out string? errorMessage)
    {
        errorMessage = null;

        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath);
            if (key is null)
            {
                errorMessage = "Unable to open the Windows startup registry key.";
                return false;
            }

            if (enabled)
            {
                key.SetValue(_applicationName, _command);
            }
            else if (key.GetValue(_applicationName) is not null)
            {
                key.DeleteValue(_applicationName, false);
            }

            return true;
        }
        catch (Exception ex)
        {
            errorMessage = $"Startup setting failed: {ex.Message}";
            return false;
        }
    }
}
