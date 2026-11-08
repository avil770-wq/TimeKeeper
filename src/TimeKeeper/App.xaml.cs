using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Microsoft.Win32;
using TimeKeeper.Models;
using TimeKeeper.Services;
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
    }

    protected override void OnExit(ExitEventArgs e)
    {
        SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
        _settings?.Flush();
        _hotkeys?.Dispose();
        _tray?.Dispose();
        _theme?.Dispose();
        _singleInstance?.Dispose();
        Log.Info("Exited.");
        base.OnExit(e);
    }

    /// <summary>
    /// Corner click, tray left-click and the hotkey all land here.
    /// For now it opens the placeholder menu; later: no timer → menu,
    /// timer running → stop dialog.
    /// </summary>
    private void OnPrimaryAction()
    {
        if (_timerMenu is not null)
        {
            _timerMenu.Activate();
            return;
        }

        _timerMenu = new TimerMenuWindow();
        _timerMenu.Closed += (_, _) => _timerMenu = null;
        _timerMenu.Show();

        var screen = CornerPositioner.ResolveScreen(_settings.Current.Placement, out _);
        CornerPositioner.Place(_timerMenu, _settings.Current.Placement.Corner, screen);
        _timerMenu.Activate();
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

        _settingsWindow = new SettingsWindow(_settings, _hotkeys, _theme, ResetPosition);
        _settingsWindow.Closed += (_, _) => _settingsWindow = null;
        if (_hotkeyTakenAtStartup && _hotkeys.Current is null) _settingsWindow.ShowHotkeyTaken();
        _settingsWindow.Show();
        _settingsWindow.Activate();
    }

    private void ExitApp()
    {
        _settingsWindow?.Close();
        _timerMenu?.Close();
        Shutdown();
    }

    private void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log.Error("Unhandled exception.", e.Exception);
        e.Handled = true; // a time widget that crashes silently loses hours; keep running
    }
}
