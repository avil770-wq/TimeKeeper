using System.Text.Json.Serialization;
using System.Windows.Input;

namespace TimeKeeper.Models;

/// <summary>
/// Everything the widget remembers per user. Stored as JSON in
/// %AppData%\TimeKeeper\settings.json. New properties must have sensible
/// defaults so older files keep loading.
/// </summary>
public sealed class AppSettings
{
    public int SchemaVersion { get; set; } = 1;

    public CornerPlacement Placement { get; set; } = new();

    public ThemeMode Theme { get; set; } = ThemeMode.System;

    public bool StartWithWindows { get; set; } = true;

    public HotkeySetting Hotkey { get; set; } = HotkeySetting.Default;

    public List<WorkDay> WorkHours { get; set; } = WorkDay.CreateDefaults();

    public ReminderSettings Reminders { get; set; } = new();

    public string? EnvironmentUrl { get; set; }

    /// <summary>Fills in anything a hand-edited or older file left out.</summary>
    public void Normalize()
    {
        Placement ??= new CornerPlacement();
        Hotkey ??= HotkeySetting.Default;
        Reminders ??= new ReminderSettings();
        Reminders.Normalize();

        var days = WorkHours ?? new List<WorkDay>();
        WorkHours = Enum.GetValues<DayOfWeek>()
            .Select(d => days.FirstOrDefault(x => x.Day == d) ?? WorkDay.CreateDefault(d))
            .OrderBy(d => WorkDay.SortKey(d.Day))
            .ToList();
    }
}

public enum ThemeMode
{
    System,
    Light,
    Dark,
}

public enum Corner
{
    BottomRight,
    BottomLeft,
    TopRight,
    TopLeft,
}

public sealed class CornerPlacement
{
    public Corner Corner { get; set; } = Corner.BottomRight;

    /// <summary>
    /// Device name of the monitor (e.g. \\.\DISPLAY2). Null means the primary
    /// monitor. If the saved monitor isn't connected we fall back to the
    /// primary one without overwriting this value.
    /// </summary>
    public string? MonitorDeviceName { get; set; }
}

public sealed class HotkeySetting
{
    public static HotkeySetting Default => new()
    {
        Modifiers = ModifierKeys.Control | ModifierKeys.Alt,
        Key = Key.T,
    };

    public ModifierKeys Modifiers { get; set; }

    public Key Key { get; set; }

    [JsonIgnore]
    public bool IsEmpty => Key == Key.None;

    public override string ToString()
    {
        if (IsEmpty) return "ללא";
        var parts = new List<string>();
        if (Modifiers.HasFlag(ModifierKeys.Control)) parts.Add("Ctrl");
        if (Modifiers.HasFlag(ModifierKeys.Alt)) parts.Add("Alt");
        if (Modifiers.HasFlag(ModifierKeys.Shift)) parts.Add("Shift");
        if (Modifiers.HasFlag(ModifierKeys.Windows)) parts.Add("Win");
        parts.Add(KeyToText(Key));
        return string.Join("+", parts);
    }

    private static string KeyToText(Key key) => key switch
    {
        >= Key.D0 and <= Key.D9 => ((int)(key - Key.D0)).ToString(),
        >= Key.NumPad0 and <= Key.NumPad9 => "Num" + (int)(key - Key.NumPad0),
        _ => key.ToString(),
    };

    public HotkeySetting Clone() => new() { Modifiers = Modifiers, Key = Key };
}

public sealed class WorkDay
{
    public DayOfWeek Day { get; set; }

    public bool IsWorkDay { get; set; }

    public TimeOnly Start { get; set; } = new(8, 0);

    public TimeOnly End { get; set; } = new(17, 0);

    public static WorkDay CreateDefault(DayOfWeek day) => new()
    {
        Day = day,
        IsWorkDay = day is >= DayOfWeek.Sunday and <= DayOfWeek.Thursday,
    };

    public static List<WorkDay> CreateDefaults() =>
        Enum.GetValues<DayOfWeek>().Select(CreateDefault).OrderBy(d => SortKey(d.Day)).ToList();

    /// <summary>Israeli week: Sunday first.</summary>
    public static int SortKey(DayOfWeek day) => (int)day;
}

public sealed class ReminderSettings
{
    public NoTimerReminder NoTimer { get; set; } = new();
    public LongRunningReminder LongRunning { get; set; } = new();
    public IdleReminder Idle { get; set; } = new();
    public EndOfDayReminder EndOfDay { get; set; } = new();

    public void Normalize()
    {
        NoTimer ??= new();
        LongRunning ??= new();
        Idle ??= new();
        EndOfDay ??= new();
    }
}

public sealed class NoTimerReminder
{
    public bool Enabled { get; set; } = true;
    public int AfterMinutes { get; set; } = 15;
    public int RepeatMinutes { get; set; } = 30;
}

public sealed class LongRunningReminder
{
    public bool Enabled { get; set; } = true;
    public int AfterHours { get; set; } = 3;
}

public sealed class IdleReminder
{
    public bool Enabled { get; set; } = true;
    public int ThresholdMinutes { get; set; } = 20;
}

public sealed class EndOfDayReminder
{
    public bool Enabled { get; set; } = true;
    public int MinutesAfterEnd { get; set; } = 30;
}
