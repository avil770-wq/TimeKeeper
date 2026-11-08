using System.Collections.Concurrent;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Interop;
using Microsoft.Web.WebView2.Core;
using TimeKeeper.Views;

namespace TimeKeeper.Services.Dataverse;

public enum SessionState
{
    NotConfigured,
    SigningIn,
    NeedsUser,
    SignedIn,
    SignedOut,
    Failed,
}

/// <summary>
/// Talks to Dataverse through the user's own Dynamics web session, so no Entra
/// app registration is needed. A hidden WebView2 sits on the environment's
/// origin and runs fetch() for us with the session cookies. If Windows SSO
/// can't sign in silently, the WebView window is shown so the user can.
/// Later this can be swapped for MSAL/WAM behind the same Send API.
/// </summary>
public sealed class DataverseSession : IDisposable
{
    private const string ApiPath = "api/data/v9.2/";

    // Installed into every document; only activates on the environment origin.
    private const string BridgeScript = """
        (() => {
          if (location.origin !== '__ORIGIN__' || window.__tkBridge) return;
          window.__tkBridge = true;
          window.chrome.webview.addEventListener('message', async e => {
            const m = e.data;
            try {
              const r = await fetch(m.url, { method: m.method, headers: m.headers, body: m.body, credentials: 'include' });
              const t = await r.text();
              window.chrome.webview.postMessage({ id: m.id, status: r.status, body: t });
            } catch (err) {
              window.chrome.webview.postMessage({ id: m.id, status: 0, body: String(err) });
            }
          });
          window.chrome.webview.postMessage({ ready: true });
        })();
        """;

    private readonly ConcurrentDictionary<string, TaskCompletionSource<(int Status, string Body)>> _pending = new();
    private SignInWindow? _window;
    private CoreWebView2Controller? _controller;
    private CoreWebView2? _web;
    private TaskCompletionSource? _bridgeReady;
    private TaskCompletionSource? _appLoaded;
    private Uri? _env;
    private bool _signInRunning;

    public SessionState State { get; private set; } = SessionState.NotConfigured;

    public string? Error { get; private set; }

    public Guid UserId { get; private set; }

    public string? UserName { get; private set; }

    public string? UserEmail { get; private set; }

    public Uri? Environment => _env;

    public event EventHandler? StateChanged;

    public bool IsSignedIn => State == SessionState.SignedIn;

    /// <summary>
    /// Sign in (silently if possible). With interactive=false the window is never
    /// shown and we stop at NeedsUser if Windows SSO isn't enough.
    /// </summary>
    public async Task SignInAsync(string environmentUrl, bool interactive)
    {
        if (_signInRunning) return;
        _signInRunning = true;
        try
        {
            if (!Uri.TryCreate(environmentUrl.TrimEnd('/') + "/", UriKind.Absolute, out var env))
            {
                SetState(SessionState.NotConfigured, "כתובת הסביבה לא תקינה.");
                return;
            }

            SetState(SessionState.SigningIn);
            await EnsureWebViewAsync(env);

            // 1. Maybe we already have a session cookie.
            if (await TryOpenApiAsync())
            {
                await LoadUserAsync();
                return;
            }

            // 2. Load the app root: Windows SSO usually signs in with no UI.
            _appLoaded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _web!.Navigate(env.ToString());
            // Silent SSO usually finishes in a few seconds; when the user asked to
            // sign in, show the window quickly instead of making them wait.
            var silentWait = interactive ? TimeSpan.FromSeconds(4) : TimeSpan.FromSeconds(20);
            var silent = await Task.WhenAny(_appLoaded.Task, Task.Delay(silentWait));

            if (silent != _appLoaded.Task)
            {
                if (!interactive)
                {
                    SetState(SessionState.NeedsUser, "כדי לראות לקוחות ומשימות צריך להתחבר.");
                    return;
                }

                SetState(SessionState.NeedsUser);
                ShowWindow();
                var done = await Task.WhenAny(_appLoaded.Task, Task.Delay(TimeSpan.FromMinutes(5)));
                HideWindow();
                if (done != _appLoaded.Task)
                {
                    SetState(SessionState.Failed, "ההתחברות לא הושלמה.");
                    return;
                }
            }

            if (await TryOpenApiAsync())
            {
                await LoadUserAsync();
            }
            else
            {
                SetState(SessionState.Failed, "לא הצלחתי להתחבר ל-Dataverse.");
            }
        }
        catch (Exception ex)
        {
            Log.Error("Sign-in failed.", ex);
            HideWindow();
            SetState(SessionState.Failed, "שגיאה בהתחברות. הפרטים ביומן.");
        }
        finally
        {
            _signInRunning = false;
        }
    }

    public async Task SignOutAsync()
    {
        if (_web is not null)
        {
            _web.CookieManager.DeleteAllCookies();
            _web.Navigate("about:blank");
            await Task.Delay(100);
        }

        UserId = Guid.Empty;
        UserName = UserEmail = null;
        SetState(SessionState.SignedOut);
        Log.Info("Signed out.");
    }

    /// <summary>GET a Web API path (relative to api/data/v9.2/) and parse the JSON.</summary>
    public async Task<JsonDocument> GetAsync(string relativePath, CancellationToken ct = default)
    {
        var (status, body) = await SendAsync("GET", relativePath, null, ct);
        if (status == 401)
        {
            SetState(SessionState.SignedOut, "פג תוקף ההתחברות.");
        }

        if (status is < 200 or >= 300)
        {
            throw new DataverseException(status, body);
        }

        return JsonDocument.Parse(body);
    }

    public Task<(int Status, string Body)> SendAsync(string method, string relativePath, string? jsonBody, CancellationToken ct = default)
    {
        if (_web is null || _env is null || _bridgeReady is not { Task.IsCompletedSuccessfully: true })
        {
            throw new InvalidOperationException("Not connected to Dataverse.");
        }

        var id = Guid.NewGuid().ToString("N");
        var tcs = new TaskCompletionSource<(int, string)>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = tcs;
        ct.Register(() =>
        {
            if (_pending.TryRemove(id, out var t)) t.TrySetCanceled(ct);
        });

        var headers = new JsonObject
        {
            ["Accept"] = "application/json",
            ["OData-MaxVersion"] = "4.0",
            ["OData-Version"] = "4.0",
            ["Prefer"] = "odata.include-annotations=\"OData.Community.Display.V1.FormattedValue\"",
        };
        if (jsonBody is not null) headers["Content-Type"] = "application/json; charset=utf-8";

        var message = new JsonObject
        {
            ["id"] = id,
            ["url"] = new Uri(_env, ApiPath + relativePath).ToString(),
            ["method"] = method,
            ["headers"] = headers,
            ["body"] = jsonBody,
        };
        _web.PostWebMessageAsJson(message.ToJsonString());

        // A request that never comes back (page navigated away) shouldn't hang the UI.
        _ = Task.Delay(TimeSpan.FromSeconds(60), CancellationToken.None).ContinueWith(_ =>
        {
            if (_pending.TryRemove(id, out var t)) t.TrySetException(new TimeoutException("Dataverse request timed out."));
        }, TaskScheduler.Default);

        return tcs.Task;
    }

    public void Dispose()
    {
        _controller?.Close();
        _window?.Close();
    }

    private async Task EnsureWebViewAsync(Uri env)
    {
        if (_web is not null && _env == env) return;

        _env = env;
        if (_web is null)
        {
            var dataFolder = Path.Combine(
                System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData),
                "TimeKeeper", "WebView2");
            var options = new CoreWebView2EnvironmentOptions { Language = "he-IL" };
            var environment = await CoreWebView2Environment.CreateAsync(null, dataFolder, options);

            _window = new SignInWindow();
            var hwnd = new WindowInteropHelper(_window).EnsureHandle();
            _controller = await environment.CreateCoreWebView2ControllerAsync(hwnd);
            _controller.IsVisible = false;
            _web = _controller.CoreWebView2;
            _web.Settings.AreDevToolsEnabled = true;
            _web.WebMessageReceived += OnWebMessage;
            _web.NavigationCompleted += OnNavigationCompleted;
            _window.SizeChanged += (_, _) => ResizeController();
            _window.UserClosing += (_, _) => _appLoaded?.TrySetResult(); // stop waiting; TryOpenApi will fail cleanly
        }

        var origin = env.GetLeftPart(UriPartial.Authority);
        await _web.AddScriptToExecuteOnDocumentCreatedAsync(BridgeScript.Replace("__ORIGIN__", origin));
    }

    /// <summary>Load the lightweight WhoAmI endpoint as a page and wait for the bridge.</summary>
    private async Task<bool> TryOpenApiAsync()
    {
        _bridgeReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var nav = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);

        void Handler(object? s, CoreWebView2NavigationCompletedEventArgs e)
        {
            if (IsEnvHost(_web!.Source)) nav.TrySetResult(e.HttpStatusCode);
            else nav.TrySetResult(0);
        }

        _web!.NavigationCompleted += Handler;
        try
        {
            _web.Navigate(new Uri(_env!, ApiPath + "WhoAmI").ToString());
            var finished = await Task.WhenAny(nav.Task, Task.Delay(TimeSpan.FromSeconds(20)));
            if (finished != nav.Task || nav.Task.Result != 200) return false;

            var ready = await Task.WhenAny(_bridgeReady.Task, Task.Delay(TimeSpan.FromSeconds(10)));
            return ready == _bridgeReady.Task;
        }
        finally
        {
            _web.NavigationCompleted -= Handler;
        }
    }

    private async Task LoadUserAsync()
    {
        using var who = await GetAsync("WhoAmI");
        UserId = who.RootElement.GetProperty("UserId").GetGuid();

        using var user = await GetAsync($"systemusers({UserId})?$select=fullname,internalemailaddress,domainname");
        var root = user.RootElement;
        UserName = root.TryGetProperty("fullname", out var n) ? n.GetString() : null;
        UserEmail = root.TryGetProperty("internalemailaddress", out var m) && m.ValueKind == JsonValueKind.String
            ? m.GetString()
            : root.TryGetProperty("domainname", out var d) ? d.GetString() : null;

        Log.Info($"Signed in to {_env} as {UserEmail} ({UserId}).");
        SetState(SessionState.SignedIn);
    }

    private void OnNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        if (_web is null) return;
        var source = _web.Source;
        Log.Info($"WebView navigated: {Redact(source)} ({e.HttpStatusCode}, ok={e.IsSuccess})");

        // Back on the environment after the login pages → the app (or API) loaded.
        if (e.IsSuccess && IsEnvHost(source))
        {
            _appLoaded?.TrySetResult();
        }
    }

    private void OnWebMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            using var doc = JsonDocument.Parse(e.WebMessageAsJson);
            var root = doc.RootElement;
            if (root.TryGetProperty("ready", out _))
            {
                if (IsEnvHost(e.Source)) _bridgeReady?.TrySetResult();
                return;
            }

            var id = root.GetProperty("id").GetString()!;
            if (_pending.TryRemove(id, out var tcs))
            {
                tcs.TrySetResult((root.GetProperty("status").GetInt32(), root.GetProperty("body").GetString() ?? string.Empty));
            }
        }
        catch (Exception ex)
        {
            Log.Error("Bad message from WebView.", ex);
        }
    }

    private bool IsEnvHost(string? url) =>
        _env is not null && Uri.TryCreate(url, UriKind.Absolute, out var u) &&
        string.Equals(u.Host, _env.Host, StringComparison.OrdinalIgnoreCase);

    private void ShowWindow()
    {
        if (_window is null || _controller is null) return;
        _window.Show();
        _window.Activate();
        ResizeController();
        _controller.IsVisible = true;
    }

    private void HideWindow()
    {
        if (_controller is not null) _controller.IsVisible = false;
        _window?.Hide();
    }

    private void ResizeController()
    {
        if (_window is null || _controller is null) return;
        var host = _window.WebHost;
        var source = PresentationSource.FromVisual(host);
        if (source?.CompositionTarget is null) return;

        var m = source.CompositionTarget.TransformToDevice;
        var topLeft = host.TranslatePoint(new Point(0, 0), _window);
        _controller.Bounds = new System.Drawing.Rectangle(
            (int)(topLeft.X * m.M11), (int)(topLeft.Y * m.M22),
            (int)(host.ActualWidth * m.M11), (int)(host.ActualHeight * m.M22));
    }

    private void SetState(SessionState state, string? error = null)
    {
        State = state;
        Error = error;
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    private static string Redact(string url)
    {
        var q = url.IndexOf('?');
        return q < 0 ? url : url[..q] + "?…";
    }
}

public sealed class DataverseException : Exception
{
    public DataverseException(int status, string body)
        : base($"Dataverse returned {status}: {Shorten(body)}")
    {
        Status = status;
    }

    public int Status { get; }

    private static string Shorten(string s) => s.Length > 400 ? s[..400] + "…" : s;
}
