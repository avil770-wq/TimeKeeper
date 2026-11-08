using Microsoft.Win32;

namespace TimeKeeper.Services;

/// <summary>Start with Windows via HKCU\...\Run (per user, no admin).</summary>
public static class StartupService
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "TimeKeeper";

    /// <summary>Debug builds: remove a Run entry that points at a bin\Debug exe.</summary>
    public static void RemoveDevBuildEntry()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
            if (key?.GetValue(ValueName) is string value &&
                value.Contains(@"\bin\Debug\", StringComparison.OrdinalIgnoreCase))
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
                Log.Info("Removed dev-build startup entry.");
            }
        }
        catch (Exception ex)
        {
            Log.Error("Failed to clean startup entry.", ex);
        }
    }

    public static void Apply(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
            if (enabled)
            {
                var exe = Environment.ProcessPath;
                if (string.IsNullOrEmpty(exe)) return;
                var value = $"\"{exe}\"";
                if (!string.Equals(key.GetValue(ValueName) as string, value, StringComparison.OrdinalIgnoreCase))
                {
                    key.SetValue(ValueName, value);
                    Log.Info($"Registered for startup: {value}");
                }
            }
            else if (key.GetValue(ValueName) is not null)
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
                Log.Info("Removed from startup.");
            }
        }
        catch (Exception ex)
        {
            Log.Error("Failed to update startup registration.", ex);
        }
    }
}
