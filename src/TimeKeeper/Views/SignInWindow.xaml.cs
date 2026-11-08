using System.ComponentModel;
using System.Windows;

namespace TimeKeeper.Views;

/// <summary>
/// Hosts the WebView2 used for Dataverse. Stays alive (hidden) for the whole
/// session; shown only when the user has to sign in.
/// </summary>
public partial class SignInWindow : Window
{
    private bool _allowClose;

    public SignInWindow()
    {
        InitializeComponent();
    }

    /// <summary>The user closed the window instead of finishing sign-in.</summary>
    public event EventHandler? UserClosing;

    public new void Close()
    {
        _allowClose = true;
        base.Close();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_allowClose)
        {
            // Keep the WebView alive; just hide.
            e.Cancel = true;
            Hide();
            UserClosing?.Invoke(this, EventArgs.Empty);
        }

        base.OnClosing(e);
    }
}
