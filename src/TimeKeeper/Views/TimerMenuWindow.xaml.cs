using System.Windows;

namespace TimeKeeper.Views;

public partial class TimerMenuWindow : Window
{
    private bool _closing;
    private DateTime _shownAt;

    public TimerMenuWindow()
    {
        InitializeComponent();
        CloseButton.Click += (_, _) => SafeClose();
        // Ignore a deactivation right after opening: Windows can bounce focus
        // back to the previous app when the click came from a no-activate window.
        Deactivated += (_, _) =>
        {
            if (DateTime.UtcNow - _shownAt > TimeSpan.FromMilliseconds(500)) SafeClose();
        };
        ContentRendered += (_, _) => _shownAt = DateTime.UtcNow;
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == System.Windows.Input.Key.Escape) SafeClose();
        };
        Closing += (_, _) => _closing = true;
        Loaded += (_, _) => CloseButton.Focus();
    }

    private void SafeClose()
    {
        if (!_closing) Close();
    }
}
