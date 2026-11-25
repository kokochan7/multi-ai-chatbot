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

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_initStarted) return;   // Loaded fires again when the view is shown after being hidden.
        _initStarted = true;

        try
        {
            var env = await BrowserEnvironment.GetAsync();
            await Browser.EnsureCoreWebView2Async(env);
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
            return;
        }

        var core = Browser.CoreWebView2;
        core.Settings.IsStatusBarEnabled = false;
        core.Settings.AreDevToolsEnabled = true;
        core.NavigationCompleted += (_, _) => UpdateButtons();
        core.HistoryChanged += (_, _) => UpdateButtons();
        core.ProcessFailed += (_, args) =>
        {
            CrashLog.Write($"{Title} page process failed: {args.ProcessFailedKind}, {args.Reason}, exit code {args.ExitCode}", null);
            if (args.ProcessFailedKind == CoreWebView2ProcessFailedKind.RenderProcessExited ||
                args.ProcessFailedKind == CoreWebView2ProcessFailedKind.RenderProcessUnresponsive)
            {
                ShowStatus("Page crashed, reloading…", isError: true);
                try { Browser.CoreWebView2?.Reload(); } catch { /* the browser may already be gone */ }
            }
            else
            {
                ShowStatus("Browser stopped. Restart the app if this column stays blank.", isError: true);
            }
        };

        ApplyColorScheme();
        Theme.Changed += ApplyColorScheme;

        Browser.Visibility = Visibility.Visible;
        StatusPanel.Visibility = Visibility.Collapsed;
        GoHome();
    }

    /// <summary>Asks the AI sites to use light or dark mode to match the app.</summary>
    private void ApplyColorScheme()
    {
        if (Browser.CoreWebView2 is not { } core) return;
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
        BackButton.IsEnabled = Browser.CoreWebView2?.CanGoBack == true;
    }

    public void GoHome()
    {
        if (Browser.CoreWebView2 != null && Uri.TryCreate(HomeUrl, UriKind.Absolute, out var uri))
            Browser.CoreWebView2.Navigate(uri.ToString());
    }

    /// <summary>
    /// Types the prompt into this site and presses Send. Returns a task that finishes with the
    /// answer's text once the site is done replying ("" if it couldn't be read), or null if sending failed.
    /// </summary>
    public async Task<Task<string>?> SendPromptAsync(string prompt, bool captureAnswer)
    {
        var site = AiSites.ByKey(SiteKey);
        var core = Browser.CoreWebView2;
        if (core == null || site == null)
        {
            ShowStatus("Not ready", isError: true);
            return null;
        }

        ShowStatus("Sending…", isError: false);
        var countBefore = captureAnswer ? await PromptSender.CountAnswersAsync(core, site) : 0;
        var result = await PromptSender.SendAsync(core, site, prompt);
        switch (result)
        {
            case SendResult.Sent:
                ShowStatus("Sent", isError: false);
                break;
            case SendResult.NoChatBox:
                ShowStatus("Chat box not found. Are you logged in?", isError: true);
                break;
            case SendResult.SendButtonNotFound:
                ShowStatus("Pressed Enter (Send button not found)", isError: true);
                break;
            default:
                ShowStatus("Couldn't send", isError: true);
                break;
        }

        if (result is SendResult.NoChatBox or SendResult.Error) return null;
        return captureAnswer
            ? PromptSender.WaitForAnswerAsync(core, site, countBefore)
            : Task.FromResult("");
    }

    /// <summary>Clears this site's saved login and cookies (shared profile, so it affects all columns).</summary>
    public async Task ClearBrowsingDataAsync()
    {
        if (Browser.CoreWebView2 != null)
            await Browser.CoreWebView2.Profile.ClearBrowsingDataAsync();
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
        if (Browser.CoreWebView2?.CanGoBack == true) Browser.CoreWebView2.GoBack();
    }

    private void Reload_Click(object sender, RoutedEventArgs e) => Browser.CoreWebView2?.Reload();

    private void Home_Click(object sender, RoutedEventArgs e) => GoHome();

    private void OpenExternal_Click(object sender, RoutedEventArgs e)
    {
        var url = Browser.CoreWebView2?.Source ?? HomeUrl;
        if (!string.IsNullOrWhiteSpace(url))
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
    }
}
