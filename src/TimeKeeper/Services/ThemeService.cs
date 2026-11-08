using System.Windows;
using Microsoft.Win32;
using TimeKeeper.Models;

namespace TimeKeeper.Services;

/// <summary>
/// Swaps the colour dictionary (Light / Dark / HighContrast) at runtime.
/// Everything in XAML uses DynamicResource, so a swap restyles open windows.
/// </summary>
public sealed class ThemeService : IDisposable
{
    private static readonly Uri LightUri = new("pack://application:,,,/Themes/Light.xaml");
    private static readonly Uri DarkUri = new("pack://application:,,,/Themes/Dark.xaml");
    private static readonly Uri HighContrastUri = new("pack://application:,,,/Themes/HighContrast.xaml");

    private ThemeMode _mode = ThemeMode.System;
    private ResourceDictionary? _active;

    public ThemeService()
    {
        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
        SystemParameters.StaticPropertyChanged += OnSystemParametersChanged;
    }

    public event EventHandler? ThemeChanged;

    public bool IsDark { get; private set; }

    public void Apply(ThemeMode mode)
    {
        _mode = mode;
        Refresh();
    }

    public void Dispose()
    {
        SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
        SystemParameters.StaticPropertyChanged -= OnSystemParametersChanged;
    }

    private void Refresh()
    {
        Uri uri;
        if (SystemParameters.HighContrast)
        {
            uri = HighContrastUri;
            IsDark = false;
        }
        else
        {
            IsDark = _mode switch
            {
                ThemeMode.Dark => true,
                ThemeMode.Light => false,
                _ => !WindowsAppsUseLightTheme(),
            };
            uri = IsDark ? DarkUri : LightUri;
        }

        var merged = Application.Current.Resources.MergedDictionaries;
        // HC dictionaries read SystemColors at load time, so always reload them.
        if (_active?.Source == uri && uri != HighContrastUri) return;

        var next = new ResourceDictionary { Source = uri };
        if (_active is not null) merged.Remove(_active);
        merged.Insert(0, next);
        _active = next;
        ThemeChanged?.Invoke(this, EventArgs.Empty);
    }

    private static bool WindowsAppsUseLightTheme()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is not int v || v != 0;
        }
        catch
        {
            return true;
        }
    }

    private void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        if (e.Category is UserPreferenceCategory.General or UserPreferenceCategory.Color or UserPreferenceCategory.Accessibility)
        {
            Application.Current.Dispatcher.BeginInvoke(Refresh);
        }
    }

    private void OnSystemParametersChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SystemParameters.HighContrast))
        {
            Application.Current.Dispatcher.BeginInvoke(Refresh);
        }
    }
}
