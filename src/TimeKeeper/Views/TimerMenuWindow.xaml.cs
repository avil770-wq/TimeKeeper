using System.Windows;

namespace TimeKeeper.Views;

public partial class TimerMenuWindow : Window
{
    private bool _closing;

    public TimerMenuWindow()
    {
        InitializeComponent();
        CloseButton.Click += (_, _) => SafeClose();
        Deactivated += (_, _) => SafeClose();
        Closing += (_, _) => _closing = true;
        Loaded += (_, _) => CloseButton.Focus();
    }

    private void SafeClose()
    {
        if (!_closing) Close();
    }
}
