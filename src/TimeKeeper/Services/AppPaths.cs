using System.IO;

namespace TimeKeeper.Services;

public static class AppPaths
{
    public static string DataFolder { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TimeKeeper");

    public static string SettingsFile => Path.Combine(DataFolder, "settings.json");

    public static string LogFolder => Path.Combine(DataFolder, "logs");
}
