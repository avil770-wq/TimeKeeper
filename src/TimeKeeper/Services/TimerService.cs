using System.IO;
using System.Text.Json;
using TimeKeeper.Services.Dataverse;

namespace TimeKeeper.Services;

/// <summary>What the running timer is on. Names are kept so the UI works offline.</summary>
public sealed record TimerTarget(
    Guid ClientId, string ClientName,
    Guid ProjectId, string ProjectName,
    Guid TaskId, string TaskTitle, string? TaskNumber)
{
    public static TimerTarget From(Client c, Project p, WorkTask t) =>
        new(c.Id, c.Name, p.Id, p.Name, t.Id, t.Title, t.Number);
}

public sealed record RunningTimer(TimerTarget Target, DateTimeOffset StartedAt);

/// <summary>
/// The one running timer. Saved to timer.json on every change so a restart,
/// crash or reboot doesn't lose the time.
/// </summary>
public sealed class TimerService
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public RunningTimer? Current { get; private set; }

    public bool IsRunning => Current is not null;

    public event EventHandler? Changed;

    private static string FilePath => Path.Combine(AppPaths.DataFolder, "timer.json");

    public void Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return;
            Current = JsonSerializer.Deserialize<RunningTimer>(File.ReadAllText(FilePath), JsonOptions);
            if (Current is not null) Log.Info($"Resumed timer on {Current.Target.TaskTitle} from {Current.StartedAt:t}.");
        }
        catch (Exception ex)
        {
            Log.Error("Could not read timer.json; starting without a timer.", ex);
            Current = null;
        }
    }

    public void Start(TimerTarget target)
    {
        Current = new RunningTimer(target, DateTimeOffset.Now);
        Save();
        Log.Info($"Timer started: {target.ClientName} / {target.ProjectName} / {target.TaskTitle}");
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Clear()
    {
        Current = null;
        Save();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Round up to the next 15 minutes, minimum 15 (agreed rule).</summary>
    public static int RoundUpToQuarter(TimeSpan elapsed)
    {
        var minutes = (int)Math.Ceiling(elapsed.TotalMinutes);
        return Math.Max(15, (int)Math.Ceiling(minutes / 15.0) * 15);
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(AppPaths.DataFolder);
            if (Current is null)
            {
                File.Delete(FilePath);
            }
            else
            {
                File.WriteAllText(FilePath, JsonSerializer.Serialize(Current, JsonOptions));
            }
        }
        catch (Exception ex)
        {
            Log.Error("Could not save timer.json.", ex);
        }
    }
}
