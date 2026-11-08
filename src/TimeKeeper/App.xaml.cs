using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Microsoft.Win32;
using TimeKeeper.Models;
using TimeKeeper.Services;
using TimeKeeper.Services.Dataverse;
using TimeKeeper.Views;

namespace TimeKeeper;

public partial class App : Application
{
    private const string MutexName = @"Local\LandauSolutions.TimeKeeper";

    private Mutex? _singleInstance;
    private SettingsService _settings = null!;
    private ThemeService _theme = null!;
    private HotkeyService _hotkeys = null!;
    private TrayIconService _tray = null!;
    private CornerWindow _corner = null!;
    private SettingsWindow? _settingsWindow;
    private TimerMenuWindow? _timerMenu;
    private DataverseSession _session = null!;
    private DataverseService _data = null!;
    private TimerService _timer = null!;
    private ReportQueue _queue = null!;
    private StopDialog? _stopDialog;
    private DispatcherTimer? _retryTimer;
    private bool _flushing;
    private bool _cornerHidden;
    private bool _hotkeyTakenAtStartup;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _singleInstance = new Mutex(initiallyOwned: true, MutexName, out var isFirst);
        if (!isFirst)
        {
            Log.Info("Another instance is running, exiting.");
            Shutdown();
            return;
        }

        DispatcherUnhandledException += OnUnhandledException;
        Log.Info($"Starting Time Keeper {typeof(App).Assembly.GetName().Version}");

        _settings = new SettingsService();
        _settings.Load();

        _theme = new ThemeService();
        _theme.Apply(_settings.Current.Theme);

#if DEBUG
        // Don't point the Run key at a bin\Debug build.
        StartupService.RemoveDevBuildEntry();
#else
        StartupService.Apply(_settings.Current.StartWithWindows);
#endif

        _corner = new CornerWindow { ContextMenuFactory = BuildCornerMenu };
        _corner.CornerClicked += (_, _) => OnPrimaryAction();
        _corner.CornerDropped += (_, _) => OnCornerDropped();
        _corner.Show();
        PlaceCorner();

        _tray = new TrayIconService();
        _tray.Clicked += (_, _) => OnPrimaryAction();
        _tray.SettingsRequested += (_, _) => ShowSettings();
        _tray.ToggleCornerRequested += (_, _) => ToggleCorner();
        _tray.ExitRequested += (_, _) => ExitApp();

        _hotkeys = new HotkeyService();
        _hotkeys.Pressed += (_, _) => OnPrimaryAction();
        if (!_hotkeys.TryRegister(_settings.Current.Hotkey))
        {
            // Keep working without a hotkey; the settings screen explains why.
            _hotkeyTakenAtStartup = true;
        }

        SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;

        _timer = new TimerService();
        _timer.Load();
        _timer.Changed += (_, _) => RefreshStatus();
        _queue = new ReportQueue();
        _queue.Load();
        _queue.Changed += (_, _) => RefreshStatus();

        _session = new DataverseSession();
        _data = new DataverseService(_session);
        _session.StateChanged += (_, _) =>
        {
            RefreshStatus();
            if (_session.IsSignedIn) _ = FlushQueueAsync();
        };

        // Retry pending reports every few minutes (network back, server back).
        _retryTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(5) };
        _retryTimer.Tick += (_, _) => _ = FlushQueueAsync();
        _retryTimer.Start();

        RefreshStatus();
        _ = SignInAsync(interactive: false); // silent; the menu offers a button if it needs the user
    }

    /// <summary>Corner colour and tray tooltip from timer + queue + session.</summary>
    private void RefreshStatus()
    {
        if (_timer.Current is { } run)
        {
            _corner.SetState(CornerState.Running);
            _tray.SetTooltip($"רץ: {run.Target.TaskTitle}");
        }
        else if (_queue.Count > 0)
        {
            _corner.SetState(CornerState.Pending);
            _tray.SetTooltip(_queue.Count == 1 ? "דיווח אחד ממתין לשליחה" : $"{_queue.Count} דיווחים ממתינים לשליחה");
        }
        else
        {
            _corner.SetState(CornerState.Idle);
            _tray.SetTooltip(SessionTooltip());
        }
    }

    private async Task<(SaveOutcome, string?)> SaveReportAsync(HoursReport report)
    {
        try
        {
            if (!_session.IsSignedIn) throw new InvalidOperationException("Not signed in.");
            await _data.CreateHoursAsync(report);
            _timer.Clear();
            _tray.ShowBalloon("הדיווח נשמר", $"{report.Minutes} דק׳ · {report.Target.TaskTitle}");
            return (SaveOutcome.Saved, null);
        }
        catch (Exception ex) when (DataverseService.IsTransient(ex))
        {
            Log.Warn($"Report {report.LocalId} queued: {ex.Message}");
            _queue.Enqueue(report with { LastError = ex.Message });
            _timer.Clear();
            _tray.ShowBalloon("הדיווח נשמר במחשב", "אין חיבור כרגע. הדיווח יישלח כשהחיבור יחזור.");
            return (SaveOutcome.Queued, null);
        }
        catch (Exception ex)
        {
            Log.Error($"Report {report.LocalId} rejected.", ex);
            return (SaveOutcome.Failed, "Dynamics דחה את הדיווח. הפרטים ביומן.");
        }
    }

    private async Task FlushQueueAsync()
    {
        if (_flushing || _queue.Count == 0 || !_session.IsSignedIn) return;
        _flushing = true;
        var sent = 0;
        try
        {
            foreach (var report in _queue.Items.ToList())
            {
                try
                {
                    await _data.CreateHoursAsync(report);
                    _queue.Remove(report.LocalId);
                    sent++;
                }
                catch (Exception ex) when (DataverseService.IsTransient(ex))
                {
                    Log.Warn($"Still can't send {report.LocalId}: {ex.Message}");
                    break; // try the rest next round
                }
                catch (Exception ex)
                {
                    // Won't succeed on retry; keep it so nothing is lost, and say so in the log.
                    Log.Error($"Queued report {report.LocalId} rejected by Dataverse; keeping it.", ex);
                    _queue.Enqueue(report with { LastError = ex.Message });
                }
            }
        }
        finally
        {
            _flushing = false;
        }

        if (sent > 0)
        {
            _tray.ShowBalloon("החיבור חזר", sent == 1 ? "הדיווח נשלח." : $"{sent} דיווחים נשלחו.");
        }
    }

    private Task SignInAsync(bool interactive) =>
        _session.SignInAsync(_settings.Current.EnvironmentUrl ?? AppSettings.DefaultEnvironmentUrl, interactive);

    private string SessionTooltip() => _session.State switch
    {
        SessionState.SignedIn => $"Time Keeper – {_session.UserName}",
        SessionState.SigningIn => "Time Keeper – מתחבר…",
        _ => "Time Keeper – לא מחובר",
    };

    protected override void OnExit(ExitEventArgs e)
    {
        SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
        _settings?.Flush();
        _retryTimer?.Stop();
        _session?.Dispose();
        _hotkeys?.Dispose();
        _tray?.Dispose();
        _theme?.Dispose();
        _singleInstance?.Dispose();
        Log.Info("Exited.");
        base.OnExit(e);
    }

    /// <summary>
    /// Corner click, tray left-click and the hotkey all land here:
    /// no timer → the task menu, timer running → the stop dialog.
    /// </summary>
    private void OnPrimaryAction()
    {
        if (_timer.Current is { } run)
        {
            ShowStopDialog(run);
            return;
        }

        if (_timerMenu is not null)
        {
            _timerMenu.Activate();
            return;
        }

        _timerMenu = new TimerMenuWindow(_session, _data, () => SignInAsync(interactive: true));
        _timerMenu.Closed += (_, _) => _timerMenu = null;
        _timerMenu.TaskChosen += (_, pick) =>
        {
            _timer.Start(TimerTarget.From(pick.Client, pick.Project, pick.Task));
            _tray.ShowBalloon("הטיימר התחיל", $"{pick.Task.Title}\n{pick.Project.Name} · {pick.Client.Name}");
        };
        _timerMenu.Show();

        var screen = CornerPositioner.ResolveScreen(_settings.Current.Placement, out _);
        CornerPositioner.Place(_timerMenu, _settings.Current.Placement.Corner, screen);
        _timerMenu.Activate();
    }

    private void ShowStopDialog(RunningTimer run)
    {
        if (_stopDialog is not null)
        {
            _stopDialog.Activate();
            return;
        }

        _stopDialog = new StopDialog(run, SaveReportAsync, () =>
        {
            Log.Info($"Timer deleted: {run.Target.TaskTitle}, started {run.StartedAt:t}.");
            _timer.Clear();
        });
        _stopDialog.Closed += (_, _) => _stopDialog = null;
        _stopDialog.Show();

        var screen = CornerPositioner.ResolveScreen(_settings.Current.Placement, out _);
        CornerPositioner.Place(_stopDialog, _settings.Current.Placement.Corner, screen);
        _stopDialog.Activate();
    }

    private void PlaceCorner()
    {
        var placement = _settings.Current.Placement;
        var screen = CornerPositioner.ResolveScreen(placement, out var fellBack);
        if (fellBack)
        {
            // Don't overwrite the saved monitor: it may come back.
            Log.Info($"Saved monitor {placement.MonitorDeviceName} not connected, using primary.");
        }

        _corner.Corner = placement.Corner;
        CornerPositioner.Place(_corner, placement.Corner, screen);
    }

    private void OnCornerDropped()
    {
        var (screen, corner) = CornerPositioner.NearestCorner(_corner);
        _settings.Update(s =>
        {
            s.Placement.Corner = corner;
            s.Placement.MonitorDeviceName = screen.Primary ? null : screen.DeviceName;
        });
        _corner.Corner = corner;
        CornerPositioner.Place(_corner, corner, screen);
        Log.Info($"Corner moved to {corner} on {screen.DeviceName}.");
    }

    private void ResetPosition()
    {
        _settings.Update(s => s.Placement = new CornerPlacement());
        if (_cornerHidden) ToggleCorner();
        PlaceCorner();
    }

    private void OnDisplaySettingsChanged(object? sender, EventArgs e) =>
        Dispatcher.BeginInvoke(DispatcherPriority.Background, PlaceCorner);

    private void ToggleCorner()
    {
        _cornerHidden = !_cornerHidden;
        if (_cornerHidden)
        {
            _corner.Hide();
        }
        else
        {
            _corner.Show();
            PlaceCorner();
        }

        _tray.SetCornerHidden(_cornerHidden);
    }

    private ContextMenu BuildCornerMenu()
    {
        var menu = new ContextMenu();
        menu.Items.Add(Item("הגדרות", ShowSettings));
        menu.Items.Add(Item("הסתר את הפינה", ToggleCorner));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("יציאה", ExitApp));
        return menu;

        static MenuItem Item(string header, Action action)
        {
            var item = new MenuItem { Header = header };
            item.Click += (_, _) => action();
            return item;
        }
    }

    private void ShowSettings()
    {
        if (_settingsWindow is not null)
        {
            if (_settingsWindow.WindowState == WindowState.Minimized) _settingsWindow.WindowState = WindowState.Normal;
            _settingsWindow.Activate();
            return;
        }

        _settingsWindow = new SettingsWindow(_settings, _hotkeys, _theme, ResetPosition, _session,
            () => SignInAsync(interactive: true));
        _settingsWindow.Closed += (_, _) => _settingsWindow = null;
        if (_hotkeyTakenAtStartup && _hotkeys.Current is null) _settingsWindow.ShowHotkeyTaken();
        _settingsWindow.Show();
        _settingsWindow.Activate();
    }

    private void ExitApp()
    {
        _settingsWindow?.Close();
        _timerMenu?.Close();
        _stopDialog?.Close();
        Shutdown();
    }

    private void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log.Error("Unhandled exception.", e.Exception);
        e.Handled = true; // a time widget that crashes silently loses hours; keep running
    }
}
