using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using TimeKeeper.Services;
using TimeKeeper.Services.Dataverse;

namespace TimeKeeper.Views;

/// <summary>
/// Client › project › task picker. Arrows move inside a column, Left goes one
/// level deeper (the layout is right-to-left), Right goes back, Enter picks.
/// </summary>
public partial class TimerMenuWindow : Window
{
    private readonly DataverseSession _session;
    private readonly DataverseService _data;
    private readonly Func<Task> _signIn;
    private CancellationTokenSource? _projectsCts;
    private CancellationTokenSource? _tasksCts;
    private bool _closing;
    private DateTime _shownAt;

    public TimerMenuWindow(DataverseSession session, DataverseService data, Func<Task> signIn)
    {
        InitializeComponent();
        _session = session;
        _data = data;
        _signIn = signIn;

        CloseButton.Click += (_, _) => SafeClose();
        SignInButton.Click += async (_, _) => await SignInAndLoadAsync();

        // Ignore a deactivation right after opening: Windows can bounce focus
        // back to the previous app when the click came from a no-activate window.
        Deactivated += (_, _) =>
        {
            if (DateTime.UtcNow - _shownAt > TimeSpan.FromMilliseconds(500)) SafeClose();
        };
        ContentRendered += (_, _) => _shownAt = DateTime.UtcNow;
        Closing += (_, _) =>
        {
            _closing = true;
            _projectsCts?.Cancel();
            _tasksCts?.Cancel();
        };
        Loaded += async (_, _) => await LoadClientsAsync();

        ClientList.SelectionChanged += async (_, _) => await OnClientSelectedAsync();
        ProjectList.SelectionChanged += async (_, _) => await OnProjectSelectedAsync();
        TaskList.MouseDoubleClick += (_, _) => ChooseTask();

        PreviewKeyDown += OnKeyDown;
    }

    /// <summary>The user picked a task. Starting the timer comes in the next stage.</summary>
    public event EventHandler<(Client Client, Project Project, WorkTask Task)>? TaskChosen;

    private async Task LoadClientsAsync()
    {
        if (!_session.IsSignedIn)
        {
            ShowSignIn(_session.Error);
            return;
        }

        ShowColumns();
        SetStatus("טוען לקוחות…");
        try
        {
            var clients = await _data.GetRecentClientsAsync();
            ClientList.ItemsSource = clients;
            SetStatus(clients.Count == 0 ? "אין דיווחים ב-90 הימים האחרונים." : $"{clients.Count} לקוחות אחרונים");
            if (clients.Count > 0)
            {
                ClientList.SelectedIndex = 0;
                FocusItem(ClientList);
            }
        }
        catch (Exception ex)
        {
            HandleError("לא הצלחתי לטעון לקוחות.", ex);
        }
    }

    private async Task OnClientSelectedAsync()
    {
        _projectsCts?.Cancel();
        _tasksCts?.Cancel();
        ProjectList.ItemsSource = null;
        TaskList.ItemsSource = null;
        if (ClientList.SelectedItem is not Client client) return;

        var cts = _projectsCts = new CancellationTokenSource();
        SetStatus("טוען פרויקטים…");
        try
        {
            var projects = await _data.GetProjectsAsync(client.Id, cts.Token);
            if (cts.IsCancellationRequested) return;
            ProjectList.ItemsSource = projects;
            SetStatus(projects.Count == 0 ? $"אין פרויקטים פעילים ל{client.Name}." : string.Empty);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            HandleError("לא הצלחתי לטעון פרויקטים.", ex);
        }
    }

    private async Task OnProjectSelectedAsync()
    {
        _tasksCts?.Cancel();
        TaskList.ItemsSource = null;
        if (ProjectList.SelectedItem is not Project project) return;

        var cts = _tasksCts = new CancellationTokenSource();
        SetStatus("טוען משימות…");
        try
        {
            var tasks = await _data.GetOpenTasksAsync(project.Id, cts.Token);
            if (cts.IsCancellationRequested) return;
            TaskList.ItemsSource = tasks;
            SetStatus(tasks.Count == 0 ? "אין קריאות פתוחות בפרויקט הזה." : string.Empty);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            HandleError("לא הצלחתי לטעון משימות.", ex);
        }
    }

    private void ChooseTask()
    {
        if (ClientList.SelectedItem is Client c && ProjectList.SelectedItem is Project p && TaskList.SelectedItem is WorkTask t)
        {
            TaskChosen?.Invoke(this, (c, p, t));
            SafeClose();
        }
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape:
                SafeClose();
                e.Handled = true;
                break;
            case Key.Left:
                MoveColumn(+1);
                e.Handled = true;
                break;
            case Key.Right:
                MoveColumn(-1);
                e.Handled = true;
                break;
            case Key.Enter:
                if (IsIn(TaskList)) ChooseTask();
                else MoveColumn(+1);
                e.Handled = true;
                break;
        }
    }

    private void MoveColumn(int direction)
    {
        var lists = new[] { ClientList, ProjectList, TaskList };
        var current = Array.FindIndex(lists, IsIn);
        if (current < 0) current = 0;
        var next = Math.Clamp(current + direction, 0, lists.Length - 1);
        if (next == current || lists[next].Items.Count == 0) return;
        if (lists[next].SelectedIndex < 0) lists[next].SelectedIndex = 0;
        FocusItem(lists[next]);
    }

    private static bool IsIn(ListBox list) => list.IsKeyboardFocusWithin;

    private static void FocusItem(ListBox list)
    {
        list.UpdateLayout();
        var index = Math.Max(list.SelectedIndex, 0);
        if (list.ItemContainerGenerator.ContainerFromIndex(index) is ListBoxItem item) item.Focus();
        else list.Focus();
    }

    private async Task SignInAndLoadAsync()
    {
        SignInButton.IsEnabled = false;
        SignInText.Text = "מתחבר…";
        try
        {
            await _signIn();
        }
        finally
        {
            SignInButton.IsEnabled = true;
        }

        if (!_closing) await LoadClientsAsync();
    }

    private void ShowSignIn(string? reason)
    {
        Columns.Visibility = Visibility.Collapsed;
        SignInPanel.Visibility = Visibility.Visible;
        SignInText.Text = string.IsNullOrEmpty(reason)
            ? "צריך להתחבר ל-Dynamics כדי לראות לקוחות ומשימות."
            : reason;
        SignInButton.Focus();
    }

    private void ShowColumns()
    {
        SignInPanel.Visibility = Visibility.Collapsed;
        Columns.Visibility = Visibility.Visible;
    }

    private void HandleError(string message, Exception ex)
    {
        Log.Error(message, ex);
        if (!_session.IsSignedIn)
        {
            ShowSignIn(_session.Error);
            return;
        }

        SetStatus(message);
    }

    private void SetStatus(string text) => StatusText.Text = text;

    private void SafeClose()
    {
        if (!_closing) Close();
    }
}
