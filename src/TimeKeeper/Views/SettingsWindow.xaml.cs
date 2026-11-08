using System.Windows;
using System.Windows.Input;
using TimeKeeper.Models;
using TimeKeeper.Services;
using TimeKeeper.Services.Dataverse;

namespace TimeKeeper.Views;

public partial class SettingsWindow : Window
{
    private const string TakenMessage = "הצירוף הזה תפוס על ידי אפליקציה אחרת. בחר צירוף אחר.";

    private readonly SettingsService _settings;
    private readonly HotkeyService _hotkeys;
    private readonly DataverseSession _session;

    public SettingsWindow(SettingsService settings, HotkeyService hotkeys, ThemeService theme, Action resetPosition,
        DataverseSession session, Func<Task> signIn)
    {
        InitializeComponent();
        _settings = settings;
        _hotkeys = hotkeys;
        _session = session;

        _session.StateChanged += OnSessionChanged;
        Closed += (_, _) => _session.StateChanged -= OnSessionChanged;
        SignInButton.Click += async (_, _) => await signIn();
        SignOutButton.Click += async (_, _) => await _session.SignOutAsync();
        UpdateAccount();

        var vm = new SettingsViewModel(settings);
        vm.ThemeChanged += (_, _) => theme.Apply(settings.Current.Theme);
        DataContext = vm;

        HotkeyBox.Text = settings.Current.Hotkey.ToString();
        HotkeyBox.PreviewKeyDown += OnHotkeyKeyDown;
        HotkeyBox.GotKeyboardFocus += (_, _) => _hotkeys.Unregister();
        HotkeyBox.LostKeyboardFocus += (_, _) => ReRegisterSavedHotkey();
        ResetPositionButton.Click += (_, _) => resetPosition();

        Closed += (_, _) => _settings.Flush();
    }

    private void OnSessionChanged(object? sender, EventArgs e) => Dispatcher.Invoke(UpdateAccount);

    private void UpdateAccount()
    {
        var signedIn = _session.IsSignedIn;
        AccountName.Text = signedIn ? _session.UserName ?? string.Empty : "לא מחובר";
        AccountDetail.Text = _session.State switch
        {
            SessionState.SignedIn => _session.UserEmail ?? string.Empty,
            SessionState.SigningIn => "מתחבר…",
            _ => _session.Error ?? string.Empty,
        };
        SignInButton.Visibility = signedIn ? Visibility.Collapsed : Visibility.Visible;
        SignInButton.IsEnabled = _session.State != SessionState.SigningIn;
        SignOutButton.Visibility = signedIn ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Shown on startup if the saved combo was already taken.</summary>
    public void ShowHotkeyTaken() => ShowHotkeyError(TakenMessage);

    private void OnHotkeyKeyDown(object sender, KeyEventArgs e)
    {
        e.Handled = true;

        // Alt combos arrive as Key.System.
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key is Key.Tab && Keyboard.Modifiers == ModifierKeys.None)
        {
            // Let Tab move focus normally.
            e.Handled = false;
            return;
        }

        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt
            or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin)
        {
            return; // wait for the real key
        }

        var mods = Keyboard.Modifiers;
        if (mods == ModifierKeys.None || mods == ModifierKeys.Shift)
        {
            ShowHotkeyError("צריך לפחות Ctrl, Alt או Win בצירוף.");
            return;
        }

        var candidate = new HotkeySetting { Modifiers = mods, Key = key };

        // Test it for real: RegisterHotKey is the only reliable "is it free?" check.
        if (!_hotkeys.TryRegister(candidate))
        {
            HotkeyBox.Text = candidate.ToString();
            ShowHotkeyError(TakenMessage);
            return;
        }

        // Free. Release it again while the box still has focus; LostFocus re-registers.
        _hotkeys.Unregister();
        _settings.Update(s => s.Hotkey = candidate);
        HotkeyBox.Text = candidate.ToString();
        HideHotkeyError();
    }

    private void ReRegisterSavedHotkey()
    {
        var saved = _settings.Current.Hotkey;
        HotkeyBox.Text = saved.ToString();
        if (_hotkeys.TryRegister(saved))
        {
            HideHotkeyError();
        }
        else
        {
            ShowHotkeyError(TakenMessage);
        }
    }

    private void ShowHotkeyError(string message)
    {
        HotkeyError.Text = message;
        HotkeyError.Visibility = Visibility.Visible;
    }

    private void HideHotkeyError() => HotkeyError.Visibility = Visibility.Collapsed;
}
