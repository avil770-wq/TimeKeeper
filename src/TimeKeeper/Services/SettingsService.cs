using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows.Threading;
using TimeKeeper.Models;

namespace TimeKeeper.Services;

/// <summary>
/// Loads and saves settings.json. Missing or corrupt file → defaults, and a
/// corrupt file is kept next to the new one for diagnosis. Saves are
/// debounced so a burst of changes writes once.
/// </summary>
public sealed class SettingsService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly DispatcherTimer _saveTimer;

    public SettingsService()
    {
        _saveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        _saveTimer.Tick += (_, _) =>
        {
            _saveTimer.Stop();
            SaveNow();
        };
    }

    public AppSettings Current { get; private set; } = new();

    /// <summary>Raised after any change was applied (before it hits disk).</summary>
    public event EventHandler? Changed;

    public void Load()
    {
        var path = AppPaths.SettingsFile;
        if (!File.Exists(path))
        {
            Log.Info("No settings file, using defaults.");
            Current = new AppSettings();
            Current.Normalize();
            SaveNow();
            return;
        }

        try
        {
            var json = File.ReadAllText(path);
            var loaded = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions)
                         ?? throw new JsonException("Settings file deserialized to null.");
            loaded.Normalize();
            Current = loaded;
            Log.Info("Settings loaded.");
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or IOException or InvalidOperationException)
        {
            Log.Error("Settings file is unreadable, falling back to defaults.", ex);
            BackupCorruptFile(path);
            Current = new AppSettings();
            Current.Normalize();
            SaveNow();
        }
    }

    /// <summary>Apply a change and schedule a save.</summary>
    public void Update(Action<AppSettings> change)
    {
        change(Current);
        Changed?.Invoke(this, EventArgs.Empty);
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    /// <summary>Write any pending change right away (used on exit).</summary>
    public void Flush()
    {
        if (_saveTimer.IsEnabled)
        {
            _saveTimer.Stop();
            SaveNow();
        }
    }

    private void SaveNow()
    {
        try
        {
            Directory.CreateDirectory(AppPaths.DataFolder);
            var path = AppPaths.SettingsFile;
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(Current, JsonOptions));
            File.Move(tmp, path, overwrite: true);
        }
        catch (Exception ex)
        {
            Log.Error("Failed to save settings.", ex);
        }
    }

    private static void BackupCorruptFile(string path)
    {
        try
        {
            var backup = Path.Combine(
                Path.GetDirectoryName(path)!,
                $"settings.corrupt-{DateTime.Now:yyyyMMdd-HHmmss}.json");
            File.Copy(path, backup, overwrite: true);
            Log.Warn($"Corrupt settings backed up to {backup}");
        }
        catch (Exception ex)
        {
            Log.Error("Could not back up corrupt settings file.", ex);
        }
    }
}
