using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Web.WebView2.Core;
using MultiAIChatbot.Services;

namespace MultiAIChatbot.Controls;

/// <summary>
/// One column: a header plus an embedded browser showing one AI website.
/// </summary>
public partial class AiPane : UserControl
{
    public static readonly DependencyProperty TitleProperty =
        DependencyProperty.Register(nameof(Title), typeof(string), typeof(AiPane),
            new PropertyMetadata("", (d, e) => ((AiPane)d).TitleText.Text = (string)e.NewValue));

    public static readonly DependencyProperty BadgeProperty =
        DependencyProperty.Register(nameof(Badge), typeof(string), typeof(AiPane),
            new PropertyMetadata("", (d, e) => ((AiPane)d).BadgeText.Text = (string)e.NewValue));

    public static readonly DependencyProperty AccentProperty =
        DependencyProperty.Register(nameof(Accent), typeof(Brush), typeof(AiPane),
            new PropertyMetadata(Brushes.Gray, (d, e) => ((AiPane)d).BadgeBorder.Background = (Brush)e.NewValue));

    public static readonly DependencyProperty SiteKeyProperty =
        DependencyProperty.Register(nameof(SiteKey), typeof(string), typeof(AiPane), new PropertyMetadata(""));

    public static readonly DependencyProperty HomeUrlProperty =
        DependencyProperty.Register(nameof(HomeUrl), typeof(string), typeof(AiPane), new PropertyMetadata(""));

    public string Title { get => (string)GetValue(TitleProperty); set => SetValue(TitleProperty, value); }
    public string Badge { get => (string)GetValue(BadgeProperty); set => SetValue(BadgeProperty, value); }
    public Brush Accent { get => (Brush)GetValue(AccentProperty); set => SetValue(AccentProperty, value); }
    /// <summary>Which site this column shows: "chatgpt", "claude" or "gemini".</summary>
    public string SiteKey { get => (string)GetValue(SiteKeyProperty); set => SetValue(SiteKeyProperty, value); }
    public string HomeUrl { get => (string)GetValue(HomeUrlProperty); set => SetValue(HomeUrlProperty, value); }

    private bool _initStarted;

    // Dragging the header to reorder columns. ChatView listens and does the actual move.
    private Point? _pressPoint;
    private bool _dragging;

    /// <summary>Raised while the header is being dragged; ChatView highlights the drop target.</summary>
    public event Action<AiPane, MouseEventArgs>? HeaderDragMoved;
    /// <summary>Raised when the header is released after a drag.</summary>
    public event Action<AiPane, MouseEventArgs>? HeaderDropped;
    /// <summary>Raised when a drag ends without a drop (e.g. the window lost the mouse).</summary>
    public event Action<AiPane>? HeaderDragCancelled;

    public AiPane()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    /// <summary>Every column by site key, so Settings can manage each account.</summary>
    public static Dictionary<string, AiPane> All { get; } = new();

    /// <summary>Raised when a column's browser is ready (or restarted), so Settings can refresh.</summary>
    public static event Action? BrowserReady;

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_initStarted) return;   // Loaded fires again when the view is shown after being hidden.
        _initStarted = true;
        All[SiteKey] = this;
        Theme.Changed += ApplyColorScheme;
        await StartBrowserAsync();
    }

    // Set when the embedded browser has crashed; touching it after that throws.
    private bool _browserDead;
    private bool _restarting;

    /// <summary>
    /// The live browser, or null while it is starting, after it crashed, or while it restarts.
    /// Always use this instead of Browser.CoreWebView2, which throws once the browser has crashed.
    /// </summary>
    private CoreWebView2? Core
    {
        get
        {
            if (_browserDead) return null;
            try
            {
                return Browser.CoreWebView2;
            }
            catch (InvalidOperationException)
            {
                _browserDead = true;
                return null;
            }
        }
    }

    private async Task StartBrowserAsync()
    {
        CoreWebView2Environment env;
        try
        {
            env = await BrowserEnvironment.GetAsync();
            // Each site gets its own browser profile, so its login (cookies) is separate:
            // signing in to one site with Google doesn't sign you in to the others.
            var options = env.CreateCoreWebView2ControllerOptions();
            options.ProfileName = BrowserEnvironment.ProfileNameFor(SiteKey);
            await Browser.EnsureCoreWebView2Async(env, options);
        }
        catch (WebView2RuntimeNotFoundException)
        {
            StatusText.Text = "Microsoft Edge WebView2 Runtime is missing. Install it from " +
                              "https://go.microsoft.com/fwlink/p/?LinkId=2124703 and restart the app.";
            return;
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Couldn't start the browser: {ex.Message}";
            CrashLog.Write($"{Title} browser failed to start", ex);
            return;
        }

        var core = Browser.CoreWebView2;
        core.Settings.IsStatusBarEnabled = false;
        core.Settings.AreDevToolsEnabled = true;
        core.NavigationCompleted += (_, args) =>
        {
            UpdateButtons();
            if (_privatePending && args.IsSuccess)
            {
                _privatePending = false;
                _ = TurnOnPrivateChatAsync();
            }
        };
        core.HistoryChanged += (_, _) => UpdateButtons();
        core.ProcessFailed += (_, args) => OnProcessFailed(env, args);

        ApplyColorScheme();
        Browser.Visibility = Visibility.Visible;
        StatusPanel.Visibility = Visibility.Collapsed;
        GoHome();
        BrowserReady?.Invoke();
    }

    private void OnProcessFailed(CoreWebView2Environment env, CoreWebView2ProcessFailedEventArgs args)
    {
        CrashLog.Write($"{Title} browser process failed: {args.ProcessFailedKind}, {args.Reason}, exit code {args.ExitCode}", null);
        switch (args.ProcessFailedKind)
        {
            case CoreWebView2ProcessFailedKind.BrowserProcessExited:
                // The whole browser is gone and this control can't be used again: build a new one.
                _browserDead = true;
                Dispatcher.InvokeAsync(() => RestartBrowserAsync(env));
                break;
            case CoreWebView2ProcessFailedKind.RenderProcessExited:
            case CoreWebView2ProcessFailedKind.RenderProcessUnresponsive:
                ShowStatus("Page crashed, reloading…", isError: true);
                try { Core?.Reload(); } catch { /* the browser may already be gone */ }
                break;
            // GPU and helper processes restart on their own.
        }
    }

    /// <summary>Replaces a crashed browser with a fresh one and reopens this column's site.</summary>
    private async Task RestartBrowserAsync(CoreWebView2Environment deadEnv)
    {
        if (_restarting) return;
        _restarting = true;
        try
        {
            ShowStatus("Browser crashed, restarting…", isError: true);
            StatusText.Text = "Restarting…";
            StatusPanel.Visibility = Visibility.Visible;

            var host = (Panel)Browser.Parent;
            var old = Browser;
            host.Children.Remove(old);
            try { old.Dispose(); } catch { /* already broken */ }

            Browser = new Microsoft.Web.WebView2.Wpf.WebView2 { Visibility = Visibility.Hidden };
            host.Children.Add(Browser);

            BrowserEnvironment.Reset(deadEnv);
            _browserDead = false;
            await StartBrowserAsync();
            if (Core != null) ShowStatus("Restarted after a browser crash", isError: false);
        }
        catch (Exception ex)
        {
            CrashLog.Write($"{Title} browser restart failed", ex);
            StatusText.Text = "The browser crashed and couldn't restart. Please restart the app.";
        }
        finally
        {
            _restarting = false;
        }
    }

    /// <summary>Asks the AI sites to use light or dark mode to match the app.</summary>
    private void ApplyColorScheme()
    {
        if (Core is not { } core) return;
        core.Profile.PreferredColorScheme = AppState.Settings.Theme switch
        {
            Theme.DarkMode => CoreWebView2PreferredColorScheme.Dark,
            Theme.LightMode => CoreWebView2PreferredColorScheme.Light,
            _ => CoreWebView2PreferredColorScheme.Auto,
        };
    }

    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        // The header's buttons handle their own clicks, so this only runs for the bar itself.
        _pressPoint = e.GetPosition(this);
        HeaderBar.CaptureMouse();
    }

    private void Header_MouseMove(object sender, MouseEventArgs e)
    {
        if (_pressPoint is not { } start || e.LeftButton != MouseButtonState.Pressed) return;

        if (!_dragging)
        {
            var delta = e.GetPosition(this) - start;
            if (Math.Abs(delta.X) < SystemParameters.MinimumHorizontalDragDistance &&
                Math.Abs(delta.Y) < SystemParameters.MinimumVerticalDragDistance) return;
            _dragging = true;
            SetDragHighlight(DragHighlight.Moving);
        }
        HeaderDragMoved?.Invoke(this, e);
    }

    private void Header_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        var wasDragging = _dragging;
        _dragging = false;
        _pressPoint = null;
        HeaderBar.ReleaseMouseCapture();
        SetDragHighlight(DragHighlight.None);
        if (wasDragging) HeaderDropped?.Invoke(this, e);
    }

    private void Header_LostMouseCapture(object sender, MouseEventArgs e)
    {
        _pressPoint = null;
        if (!_dragging) return;
        _dragging = false;
        SetDragHighlight(DragHighlight.None);
        HeaderDragCancelled?.Invoke(this);
    }

    public enum DragHighlight { None, Moving, DropTarget }

    /// <summary>Colors the header while it is dragged, or while another column hovers over it.</summary>
    public void SetDragHighlight(DragHighlight state)
    {
        switch (state)
        {
            case DragHighlight.Moving:
                HeaderBar.SetResourceReference(Border.BackgroundProperty, "AccentSoft");
                HeaderBar.SetResourceReference(Border.BorderBrushProperty, "Divider");
                break;
            case DragHighlight.DropTarget:
                HeaderBar.SetResourceReference(Border.BackgroundProperty, "AccentSoft");
                HeaderBar.SetResourceReference(Border.BorderBrushProperty, "Accent");
                break;
            default:
                HeaderBar.Background = Brushes.Transparent;
                HeaderBar.SetResourceReference(Border.BorderBrushProperty, "Divider");
                break;
        }
    }

    private void UpdateButtons()
    {
        BackButton.IsEnabled = Core?.CanGoBack == true;
    }

    public void GoHome()
    {
        var isPrivate = AppState.Settings.PrivateFor(SiteKey);
        var url = AiSites.HomeUrl(SiteKey, HomeUrl, isPrivate);
        if (Core is { } core && Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            _privatePending = isPrivate && AiSites.PrivateButtonPattern(SiteKey) != null;
            core.Navigate(uri.ToString());
        }
    }

    private bool _privatePending;
    private static readonly string PrivateChatScript = LoadPrivateScript();

    private static string LoadPrivateScript()
    {
        var asm = System.Reflection.Assembly.GetExecutingAssembly();
        var name = asm.GetManifestResourceNames().First(n => n.EndsWith("private-chat.js", StringComparison.OrdinalIgnoreCase));
        using var reader = new System.IO.StreamReader(asm.GetManifestResourceStream(name)!);
        return reader.ReadToEnd();
    }

    /// <summary>
    /// Clicks the site's private-chat button once its page is ready (Claude: Incognito, Gemini:
    /// Temporary chat). Signed-out pages have no such button, so nothing happens there.
    /// </summary>
    private async Task TurnOnPrivateChatAsync()
    {
        if (AiSites.PrivateButtonPattern(SiteKey) is not { } pattern) return;
        var script = PrivateChatScript.Replace("__WANT__", System.Text.Json.JsonSerializer.Serialize(pattern));
        for (var attempt = 0; attempt < 16; attempt++)   // the button appears a moment after the page loads
        {
            await Task.Delay(500);
            if (Core is not { } core) return;
            try
            {
                var result = System.Text.Json.JsonSerializer.Deserialize<string>(await core.ExecuteScriptAsync(script));
                if (result is "clicked" or "already") return;
            }
            catch { return; }
        }
    }

    /// <summary>
    /// Types the prompt into this site and presses Send. Returns a task that finishes with the
    /// answer's text once the site is done replying ("" if it couldn't be read), or null if sending failed.
    /// </summary>
    public async Task<Task<string>?> SendPromptAsync(string prompt, bool captureAnswer,
        IReadOnlyList<Attachment>? files = null, Action<string>? onProgress = null)
    {
        var site = AiSites.ByKey(SiteKey);
        var core = Core;
        if (core == null || site == null)
        {
            ShowStatus("Not ready", isError: true);
            return null;
        }

        ShowStatus(files is { Count: > 0 } ? "Uploading and sending…" : "Sending…", isError: false);
        var countBefore = captureAnswer ? await PromptSender.CountAnswersAsync(core, site) : 0;
        var result = await PromptSender.SendAsync(core, site, prompt, files);
        switch (result)
        {
            case SendResult.Sent:
                ShowStatus("Sent", isError: false);
                break;
            case SendResult.NoChatBox:
                ShowStatus("Chat box not found. Are you logged in?", isError: true);
                break;
            case SendResult.FilesNeedSignIn:
                ShowStatus($"Not sent. Sign in to {AiSites.DisplayName(SiteKey)} to send files", isError: true);
                break;
            case SendResult.FilesNotAttached:
                ShowStatus($"Not sent. Couldn't attach the picture on {AiSites.DisplayName(SiteKey)}", isError: true);
                break;
            case SendResult.SendButtonNotFound:
                ShowStatus("Pressed Enter (Send button not found)", isError: true);
                break;
            default:
                ShowStatus("Couldn't send", isError: true);
                break;
        }

        if (result is SendResult.NoChatBox or SendResult.FilesNeedSignIn or SendResult.FilesNotAttached or SendResult.Error) return null;
        return captureAnswer
            ? PromptSender.WaitForAnswerAsync(core, site, countBefore, onProgress)
            : Task.FromResult("");
    }

    /// <summary>Clears this site's saved login, cookies and site data (this column's own profile only).</summary>
    public async Task ClearBrowsingDataAsync()
    {
        if (Core is { } core)
            await core.Profile.ClearBrowsingDataAsync();
    }

    /// <summary>True when the site's login cookie is present, false when not, null while the browser isn't ready.</summary>
    public async Task<bool?> IsSignedInAsync()
    {
        if (Core is not { } core) return null;
        var (url, names) = AiSites.LoginCookie(SiteKey);
        if (url.Length == 0) return null;
        try
        {
            var cookies = await core.CookieManager.GetCookiesAsync(url);
            return cookies.Any(c => names.Any(n => c.Name == n || c.Name.StartsWith(n + ".")));
        }
        catch
        {
            return null;
        }
    }

    public bool IsReady => Core != null;

    /// <summary>Reads the answer to question number <paramref name="index"/> of the chat open in this column.</summary>
    public async Task<string> ReadAnswerAsync(int index, int questions)
    {
        var site = AiSites.ByKey(SiteKey);
        if (Core is not { } core || site == null) return "";
        try { return await PromptSender.ReadAnswerAtAsync(core, site, index, questions); }
        catch { return ""; }
    }

    // ---- Search all AIs (Ctrl+F) ----
    // The WPF WebView2 control passes Ctrl+F, F3 and Esc pressed inside a site on as normal
    // key events, so MainWindow's key handler sees them and ChatView runs the search.

    private static readonly string FindScript = LoadScriptResource("find-text.js");

    private static string LoadScriptResource(string fileName)
    {
        var asm = System.Reflection.Assembly.GetExecutingAssembly();
        var name = asm.GetManifestResourceNames().First(n => n.EndsWith(fileName, StringComparison.OrdinalIgnoreCase));
        using var reader = new System.IO.StreamReader(asm.GetManifestResourceStream(name)!);
        return reader.ReadToEnd();
    }

    /// <summary>
    /// Finds <paramref name="query"/> on this column's page and highlights every match.
    /// step: 0 searches again, 1 moves to the next match, -1 to the previous one.
    /// Returns (matches, current match index), or null if the page couldn't be searched.
    /// </summary>
    public async Task<(int Count, int Index)?> FindAsync(string query, int step)
    {
        if (Core is not { } core) return null;
        var script = FindScript
            .Replace("__QUERY__", System.Text.Json.JsonSerializer.Serialize(query))
            .Replace("__STEP__", step.ToString(System.Globalization.CultureInfo.InvariantCulture));
        try
        {
            var json = System.Text.Json.JsonSerializer.Deserialize<string>(await core.ExecuteScriptAsync(script));
            if (json is null) return null;
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.TryGetProperty("error", out _)) return null;
            return (root.GetProperty("count").GetInt32(), root.GetProperty("index").GetInt32());
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Removes the search highlights from this column's page.</summary>
    public async Task ClearFindAsync()
    {
        if (Core is not { } core) return;
        try { await core.ExecuteScriptAsync(FindScript.Replace("__QUERY__", "\"\"").Replace("__STEP__", "'clear'")); }
        catch { /* page gone or navigating */ }
    }

    /// <summary>Opens the site's sign-in page in this column.</summary>
    public void OpenSignIn()
    {
        var url = AiSites.SignInUrl(SiteKey);
        if (Core is { } core && Uri.TryCreate(url, UriKind.Absolute, out var uri)) core.Navigate(uri.ToString());
    }

    /// <summary>Signs out of this site only: clears its profile's cookies and data, then reloads its start page.</summary>
    public async Task DisconnectAsync()
    {
        await ClearBrowsingDataAsync();
        GoHome();
    }

    private CancellationTokenSource? _statusClear;

    private async void ShowStatus(string text, bool isError)
    {
        SendStatus.Text = text;
        StatusPill.Visibility = string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;
        SendStatus.SetResourceReference(TextBlock.ForegroundProperty, isError ? "ErrorText" : "TextMuted");
        StatusPill.SetResourceReference(Border.BackgroundProperty, isError ? "DangerHover" : "NavHover");
        SendStatus.ToolTip = text;

        _statusClear?.Cancel();
        if (text.EndsWith("…")) return;          // keep "Sending…" until the result arrives
        var cts = _statusClear = new CancellationTokenSource();
        try
        {
            await Task.Delay(isError ? 8000 : 3000, cts.Token);
            SendStatus.Text = "";
            StatusPill.Visibility = Visibility.Collapsed;
        }
        catch (TaskCanceledException) { }
    }

    private void Back_Click(object sender, RoutedEventArgs e)
    {
        if (Core is { CanGoBack: true } core) core.GoBack();
    }

    private void Reload_Click(object sender, RoutedEventArgs e) => Core?.Reload();

    private void Home_Click(object sender, RoutedEventArgs e) => GoHome();

    private void OpenExternal_Click(object sender, RoutedEventArgs e)
    {
        var url = Core?.Source ?? HomeUrl;
        if (!string.IsNullOrWhiteSpace(url))
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
    }
}
