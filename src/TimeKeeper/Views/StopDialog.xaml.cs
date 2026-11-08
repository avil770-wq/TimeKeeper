using System.Windows;
using System.Windows.Threading;
using TimeKeeper.Services;

namespace TimeKeeper.Views;

public enum SaveOutcome
{
    Saved,
    Queued,
    Failed,
}

/// <summary>
/// Opened by clicking the corner while a timer runs. Save writes the report,
/// "continue" just closes (the timer keeps going), delete drops the time.
/// </summary>
public partial class StopDialog : Window
{
    private const int MaxMinutes = 24 * 60;

    private readonly RunningTimer _timer;
    private readonly Func<HoursReport, Task<(SaveOutcome Outcome, string? Error)>> _save;
    private readonly Action _delete;
    private readonly DispatcherTimer _tick;
    private int _minutes;
    private bool _userAdjusted;
    private bool _busy;
    private bool _closing;

    public StopDialog(RunningTimer timer, Func<HoursReport, Task<(SaveOutcome, string?)>> save, Action delete)
    {
        InitializeComponent();
        _timer = timer;
        _save = save;
        _delete = delete;

        var t = timer.Target;
        TaskTitle.Text = string.IsNullOrEmpty(t.TaskNumber) ? t.TaskTitle : $"{t.TaskTitle} ({t.TaskNumber})";
        TaskContext.Text = $"{t.ProjectName} · {t.ClientName}";
        DateBox.SelectedDate = timer.StartedAt.LocalDateTime.Date;

        _minutes = TimerService.RoundUpToQuarter(Elapsed);
        UpdateDuration();

        // Keep the exact time (and the suggestion, until the user touches it) live.
        _tick = new DispatcherTimer { Interval = TimeSpan.FromSeconds(15) };
        _tick.Tick += (_, _) =>
        {
            if (!_userAdjusted) _minutes = TimerService.RoundUpToQuarter(Elapsed);
            UpdateDuration();
        };
        _tick.Start();

        MinusButton.Click += (_, _) => Adjust(-15);
        PlusButton.Click += (_, _) => Adjust(+15);
        SaveButton.Click += async (_, _) => await SaveAsync();
        ContinueButton.Click += (_, _) => SafeClose();
        DeleteButton.Click += (_, _) => ShowConfirm(true);
        BackButton.Click += (_, _) => ShowConfirm(false);
        ConfirmDeleteButton.Click += (_, _) =>
        {
            _delete();
            SafeClose();
        };
        DescriptionBox.TextChanged += (_, _) =>
        {
            if (DescriptionBox.Text.Trim().Length > 0) DescriptionError.Visibility = Visibility.Collapsed;
        };

        Loaded += (_, _) => DescriptionBox.Focus();
        Closing += (_, _) =>
        {
            _closing = true;
            _tick.Stop();
        };
    }

    private TimeSpan Elapsed => DateTimeOffset.Now - _timer.StartedAt;

    private void Adjust(int delta)
    {
        _userAdjusted = true;
        _minutes = Math.Clamp(_minutes + delta, 15, MaxMinutes);
        UpdateDuration();
    }

    private void UpdateDuration()
    {
        DurationText.Text = FormatMinutes(_minutes);
        MinusButton.IsEnabled = _minutes > 15 && !_busy;
        PlusButton.IsEnabled = _minutes < MaxMinutes && !_busy;
        var e = Elapsed;
        ExactText.Text = $"זמן מדויק: {(int)e.TotalHours}:{e.Minutes:00}";
    }

    private static string FormatMinutes(int minutes) =>
        minutes < 60 ? $"{minutes} דק׳" : $"{minutes / 60}:{minutes % 60:00}";

    private async Task SaveAsync()
    {
        if (_busy) return;

        var description = DescriptionBox.Text.Trim();
        if (description.Length == 0)
        {
            DescriptionError.Visibility = Visibility.Visible;
            DescriptionBox.Focus();
            return;
        }

        if (DateBox.SelectedDate is not { } date)
        {
            ShowError("בחר תאריך.");
            DateBox.Focus();
            return;
        }

        var report = new HoursReport(
            Guid.NewGuid(), _timer.Target, description, _minutes, DateOnly.FromDateTime(date), DateTimeOffset.Now);

        SetBusy(true);
        ErrorText.Visibility = Visibility.Collapsed;
        var (outcome, error) = await _save(report);
        SetBusy(false);

        if (outcome == SaveOutcome.Failed)
        {
            ShowError(error ?? "השמירה נכשלה.");
            return;
        }

        SafeClose();
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        SaveButton.Content = busy ? "שומר…" : "שמור";
        SaveButton.IsEnabled = ContinueButton.IsEnabled = DeleteButton.IsEnabled = !busy;
        DescriptionBox.IsEnabled = DateBox.IsEnabled = !busy;
        UpdateDuration();
    }

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorText.Visibility = Visibility.Visible;
    }

    private void ShowConfirm(bool show)
    {
        FormPanel.Visibility = show ? Visibility.Hidden : Visibility.Visible;
        ConfirmPanel.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        if (show) BackButton.Focus();
        else DeleteButton.Focus();
    }

    private void SafeClose()
    {
        if (!_closing) Close();
    }
}
