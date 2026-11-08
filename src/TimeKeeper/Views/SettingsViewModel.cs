using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using TimeKeeper.Models;
using TimeKeeper.Services;

namespace TimeKeeper.Views;

public abstract class Observable : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected void Raise([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>
/// Thin wrapper over AppSettings for binding. Every setter writes straight
/// through to SettingsService (no Save button, per spec). Invalid input
/// throws, which WPF shows as a validation error on the field.
/// </summary>
public sealed class SettingsViewModel : Observable
{
    private readonly SettingsService _settings;

    public SettingsViewModel(SettingsService settings)
    {
        _settings = settings;
        Days = settings.Current.WorkHours.Select(d => new WorkDayViewModel(d, settings)).ToList();
    }

    private AppSettings S => _settings.Current;

    // ---- General ---------------------------------------------------------

    public bool StartWithWindows
    {
        get => S.StartWithWindows;
        set
        {
            _settings.Update(s => s.StartWithWindows = value);
            StartupService.Apply(value);
            Raise();
        }
    }

    public bool ThemeSystem
    {
        get => S.Theme == ThemeMode.System;
        set { if (value) SetTheme(ThemeMode.System); }
    }

    public bool ThemeLight
    {
        get => S.Theme == ThemeMode.Light;
        set { if (value) SetTheme(ThemeMode.Light); }
    }

    public bool ThemeDark
    {
        get => S.Theme == ThemeMode.Dark;
        set { if (value) SetTheme(ThemeMode.Dark); }
    }

    public event EventHandler? ThemeChanged;

    private void SetTheme(ThemeMode mode)
    {
        if (S.Theme == mode) return;
        _settings.Update(s => s.Theme = mode);
        Raise(nameof(ThemeSystem));
        Raise(nameof(ThemeLight));
        Raise(nameof(ThemeDark));
        ThemeChanged?.Invoke(this, EventArgs.Empty);
    }

    // ---- Work hours ------------------------------------------------------

    public IReadOnlyList<WorkDayViewModel> Days { get; }

    // ---- Reminders -------------------------------------------------------

    public bool NoTimerEnabled
    {
        get => S.Reminders.NoTimer.Enabled;
        set { _settings.Update(s => s.Reminders.NoTimer.Enabled = value); Raise(); }
    }

    public int NoTimerAfterMinutes
    {
        get => S.Reminders.NoTimer.AfterMinutes;
        set { Range(value, 1, 240); _settings.Update(s => s.Reminders.NoTimer.AfterMinutes = value); Raise(); }
    }

    public int NoTimerRepeatMinutes
    {
        get => S.Reminders.NoTimer.RepeatMinutes;
        set { Range(value, 5, 480); _settings.Update(s => s.Reminders.NoTimer.RepeatMinutes = value); Raise(); }
    }

    public bool LongRunningEnabled
    {
        get => S.Reminders.LongRunning.Enabled;
        set { _settings.Update(s => s.Reminders.LongRunning.Enabled = value); Raise(); }
    }

    public int LongRunningAfterHours
    {
        get => S.Reminders.LongRunning.AfterHours;
        set { Range(value, 1, 12); _settings.Update(s => s.Reminders.LongRunning.AfterHours = value); Raise(); }
    }

    public bool IdleEnabled
    {
        get => S.Reminders.Idle.Enabled;
        set { _settings.Update(s => s.Reminders.Idle.Enabled = value); Raise(); }
    }

    public int IdleThresholdMinutes
    {
        get => S.Reminders.Idle.ThresholdMinutes;
        set { Range(value, 5, 240); _settings.Update(s => s.Reminders.Idle.ThresholdMinutes = value); Raise(); }
    }

    public bool EndOfDayEnabled
    {
        get => S.Reminders.EndOfDay.Enabled;
        set { _settings.Update(s => s.Reminders.EndOfDay.Enabled = value); Raise(); }
    }

    // ---- Account ---------------------------------------------------------

    public string EnvironmentUrl
    {
        get => S.EnvironmentUrl ?? string.Empty;
        set
        {
            var trimmed = value.Trim();
            if (trimmed.Length > 0 &&
                (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps))
            {
                throw new ArgumentException("כתובת לא תקינה. לדוגמה: https://bizaid.crm4.dynamics.com");
            }

            _settings.Update(s => s.EnvironmentUrl = trimmed.Length == 0 ? null : trimmed.TrimEnd('/'));
            Raise();
        }
    }

    private static void Range(int value, int min, int max)
    {
        if (value < min || value > max)
        {
            throw new ArgumentOutOfRangeException(nameof(value), $"ערך בין {min} ל-{max}.");
        }
    }
}

public sealed class WorkDayViewModel : Observable
{
    private static readonly string[] HebrewNames = { "ראשון", "שני", "שלישי", "רביעי", "חמישי", "שישי", "שבת" };

    private readonly WorkDay _day;
    private readonly SettingsService _settings;

    public WorkDayViewModel(WorkDay day, SettingsService settings)
    {
        _day = day;
        _settings = settings;
    }

    public string Name => HebrewNames[(int)_day.Day];

    public bool IsWorkDay
    {
        get => _day.IsWorkDay;
        set { _settings.Update(_ => _day.IsWorkDay = value); Raise(); }
    }

    public string StartText
    {
        get => _day.Start.ToString("HH:mm", CultureInfo.InvariantCulture);
        set
        {
            var t = ParseTime(value);
            if (t >= _day.End) throw new ArgumentException("שעת ההתחלה חייבת להיות לפני שעת הסיום.");
            _settings.Update(_ => _day.Start = t);
            Raise();
        }
    }

    public string EndText
    {
        get => _day.End.ToString("HH:mm", CultureInfo.InvariantCulture);
        set
        {
            var t = ParseTime(value);
            if (t <= _day.Start) throw new ArgumentException("שעת הסיום חייבת להיות אחרי שעת ההתחלה.");
            _settings.Update(_ => _day.End = t);
            Raise();
        }
    }

    private static TimeOnly ParseTime(string text)
    {
        var formats = new[] { "H:mm", "HH:mm", "Hmm", "HHmm", "H" };
        if (TimeOnly.TryParseExact(text.Trim(), formats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var t))
        {
            return t;
        }

        throw new FormatException("פורמט שעה: 08:00");
    }
}
