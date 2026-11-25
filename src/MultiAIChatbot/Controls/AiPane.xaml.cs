using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
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

        Browser.Visibility = Visibility.Visible;
        StatusText.Visibility = Visibility.Collapsed;
        GoHome();
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
        SendStatus.Foreground = isError
            ? new SolidColorBrush(Color.FromRgb(0xC6, 0x28, 0x28))
            : (Brush)FindResource("TextMuted");

        _statusClear?.Cancel();
        if (text.EndsWith("…")) return;          // keep "Sending…" until the result arrives
        var cts = _statusClear = new CancellationTokenSource();
        try
        {
            await Task.Delay(isError ? 8000 : 3000, cts.Token);
            SendStatus.Text = "";
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
