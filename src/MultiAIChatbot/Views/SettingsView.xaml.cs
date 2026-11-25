using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using MultiAIChatbot.Controls;
using MultiAIChatbot.Services;

namespace MultiAIChatbot.Views;

public partial class SettingsView : UserControl
{
    public sealed record ColumnRow(string Key, string Name, bool Shown);

    /// <summary>One AI's account row: whether it's signed in, and which buttons to show.</summary>
    public sealed class AccountRow : INotifyPropertyChanged
    {
        public required string Key { get; init; }
        public required string Name { get; init; }
        public required Brush Color { get; init; }

        private string _status = "Checking…";
        private bool? _signedIn;

        public string Status { get => _status; set { _status = value; Changed(nameof(Status)); } }

        public bool? SignedIn
        {
            get => _signedIn;
            set
            {
                _signedIn = value;
                Changed(nameof(SignInVisibility));
                Changed(nameof(DisconnectVisibility));
            }
        }

        // Unknown (browser still starting): show both buttons.
        public Visibility SignInVisibility => SignedIn == true ? Visibility.Collapsed : Visibility.Visible;
        public Visibility DisconnectVisibility => SignedIn == false ? Visibility.Collapsed : Visibility.Visible;

        public event PropertyChangedEventHandler? PropertyChanged;
        private void Changed(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    private readonly List<AccountRow> _accounts = AiSites.AllKeys.Select(k => new AccountRow
    {
        Key = k,
        Name = AiSites.DisplayName(k),
        Color = (Brush)new BrushConverter().ConvertFromString(AiSites.Color(k))!,
    }).ToList();

    /// <summary>Raised when Sign in is clicked, so the window can switch to the Chat page.</summary>
    public event Action? ShowChatRequested;

    private bool _loading;
    private UpdateInfo? _pendingUpdate;

    /// <summary>Raised when a newer version is found, with its version text (e.g. "1.0.25").</summary>
    public event Action<string>? UpdateFound;

    /// <summary>Installs the update found by the last check (used by the menu's update button).</summary>
    /// <returns>false if installing failed (the reason is shown on the Settings page).</returns>
    public async Task<bool> InstallPendingAsync()
    {
        if (_pendingUpdate is not { } ready)
        {
            await CheckAsync(askToInstall: true);
            return true;
        }
        return await InstallAsync(ready);
    }
    private bool _busy;

    public SettingsView()
    {
        InitializeComponent();
        DataFolderText.Text = "Your website logins, history and settings are stored only on this PC, in " +
                              AppState.Folder;
        AccountList.ItemsSource = _accounts;
        // Check the logins whenever Settings is opened or a browser finishes starting.
        IsVisibleChanged += (_, _) => { if (IsVisible) _ = RefreshAccountsAsync(); };
        AiPane.BrowserReady += () => { if (IsVisible) _ = RefreshAccountsAsync(); };
        VersionText.Text = $"You have version {Updater.CurrentVersionText}.";
        Refresh();
    }

    private void Refresh()
    {
        _loading = true;
        var s = AppState.Settings;
        ColumnList.ItemsSource = s.ColumnOrder
            .Select(k => new ColumnRow(k, AiSites.DisplayName(k), !s.HiddenSites.Contains(k)))
            .ToList();
        SaveAnswersBox.IsChecked = s.SaveAnswers;
        PrivateChatGptBox.IsChecked = s.PrivateChatGpt;
        PrivateClaudeBox.IsChecked = s.PrivateClaude;
        PrivateGeminiBox.IsChecked = s.PrivateGemini;
        AutoCheckBox.IsChecked = s.CheckForUpdatesOnStartup;
        ThemeSystem.IsChecked = s.Theme is not (Theme.LightMode or Theme.DarkMode);
        ThemeLight.IsChecked = s.Theme == Theme.LightMode;
        ThemeDark.IsChecked = s.Theme == Theme.DarkMode;
        _loading = false;
    }

    private void Theme_Checked(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        var mode = sender == ThemeDark ? Theme.DarkMode
                 : sender == ThemeLight ? Theme.LightMode
                 : Theme.FollowWindows;
        if (AppState.Settings.Theme == mode) return;
        AppState.Settings.Theme = mode;
        AppState.SaveSettings();
        Theme.Apply(mode);
    }

    private void Shown_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not CheckBox { Tag: string key } box) return;
        var hidden = AppState.Settings.HiddenSites;
        hidden.Remove(key);
        if (box.IsChecked != true) hidden.Add(key);

        if (hidden.Count >= AiSites.AllKeys.Length)
        {
            hidden.Remove(key);   // Keep at least one column.
            StatusText.Text = "At least one AI has to stay visible.";
        }
        else
        {
            StatusText.Text = "";
        }
        AppState.SaveSettings();
        Refresh();
    }

    private void MoveUp_Click(object sender, RoutedEventArgs e) => Move(sender, -1);
    private void MoveDown_Click(object sender, RoutedEventArgs e) => Move(sender, +1);

    private void Move(object sender, int delta)
    {
        if (sender is not Button { Tag: string key }) return;
        var order = AppState.Settings.ColumnOrder;
        var i = order.IndexOf(key);
        var j = i + delta;
        if (i < 0 || j < 0 || j >= order.Count) return;
        (order[i], order[j]) = (order[j], order[i]);
        AppState.SaveSettings();
        Refresh();
    }

    private void Private_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading || sender is not CheckBox { Tag: string key } box) return;
        var on = box.IsChecked == true;
        var s = AppState.Settings;
        switch (key)
        {
            case "chatgpt": s.PrivateChatGpt = on; break;
            case "claude": s.PrivateClaude = on; break;
            case "gemini": s.PrivateGemini = on; break;
        }
        AppState.SaveSettingsQuietly();
        // Start a fresh chat on that site in the new mode.
        if (AiPane.All.TryGetValue(key, out var pane)) pane.GoHome();
        StatusText.Text = $"{AiSites.DisplayName(key)}: new chats {(on ? "start in " + AiSites.PrivateModeName(key) : "are normal chats")}.";
    }

    private void SaveAnswers_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        AppState.Settings.SaveAnswers = SaveAnswersBox.IsChecked == true;
        AppState.SaveSettings();
    }

    private void ClearHistory_Click(object sender, RoutedEventArgs e)
    {
        var answer = MessageBox.Show(Window.GetWindow(this)!, "Delete all saved questions and answers?",
            "Clear history", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (answer != MessageBoxResult.Yes) return;
        AppState.ClearHistory();
        StatusText.Text = "History cleared.";
    }

    // ---- Accounts ----

    private async Task RefreshAccountsAsync()
    {
        foreach (var row in _accounts)
        {
            if (!AiPane.All.TryGetValue(row.Key, out var pane) || !pane.IsReady)
            {
                row.SignedIn = null;
                row.Status = "Starting…";
                continue;
            }
            var signedIn = await pane.IsSignedInAsync();
            row.SignedIn = signedIn;
            row.Status = signedIn switch
            {
                true => "Signed in",
                false => "Not signed in",
                null => "Couldn't check",
            };
        }
    }

    private void AccountRefresh_Click(object sender, RoutedEventArgs e) => _ = RefreshAccountsAsync();

    private void AccountSignIn_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string key } || !AiPane.All.TryGetValue(key, out var pane)) return;
        if (AppState.Settings.HiddenSites.Contains(key))
        {
            // Its column has to be on screen to sign in.
            AppState.Settings.HiddenSites.Remove(key);
            AppState.SaveSettings();
            Refresh();
        }
        pane.OpenSignIn();
        ShowChatRequested?.Invoke();
    }

    private async void AccountDisconnect_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string key } || !AiPane.All.TryGetValue(key, out var pane)) return;
        var name = AiSites.DisplayName(key);
        var answer = MessageBox.Show(Window.GetWindow(this)!,
            $"Disconnect {name}? This signs you out of {name} in this app and clears its saved site data. The other AIs stay signed in.",
            $"Disconnect {name}", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (answer != MessageBoxResult.Yes) return;
        try
        {
            await pane.DisconnectAsync();
            StatusText.Text = $"Disconnected {name}.";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Couldn't disconnect {name}: {ex.Message}";
        }
        await RefreshAccountsAsync();
    }

    private async void LogOut_Click(object sender, RoutedEventArgs e)
    {
        var answer = MessageBox.Show(Window.GetWindow(this)!,
            "Log out of ChatGPT, Claude and Gemini? You'll need to sign in to each again.",
            "Log out of all sites", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (answer != MessageBoxResult.Yes) return;

        try
        {
            await AppState.LogOutAllAsync();
            StatusText.Text = "Logged out of all sites.";
            await RefreshAccountsAsync();
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Couldn't log out: {ex.Message}";
        }
    }

    private void AutoCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        AppState.Settings.CheckForUpdatesOnStartup = AutoCheckBox.IsChecked == true;
        AppState.SaveSettings();
    }

    private async void Update_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        if (_pendingUpdate is { } ready)
        {
            await InstallAsync(ready);
            return;
        }
        await CheckAsync(askToInstall: false);
    }

    /// <summary>
    /// Checks GitHub for a newer build. Called by the button, and once at startup
    /// (with <paramref name="quiet"/> so nothing shows unless there is an update).
    /// </summary>
    public async Task CheckAsync(bool askToInstall, bool quiet = false)
    {
        if (_busy) return;
        _busy = true;
        UpdateButton.IsEnabled = false;
        if (!quiet) UpdateStatusText.Text = "Checking for updates...";
        try
        {
            var update = await Updater.CheckAsync();
            if (update is null)
            {
                if (!quiet) UpdateStatusText.Text = "You're on the latest version.";
                return;
            }

            _pendingUpdate = update;
            var newVersion = $"{update.Version.Major}.{update.Version.Minor}.{update.Version.Build}";
            UpdateButton.Content = $"Update to {newVersion}";
            UpdateStatusText.Text = $"Version {newVersion} is available. Click Update to install it and restart.";
            UpdateFound?.Invoke(newVersion);

            if (askToInstall)
            {
                var answer = MessageBox.Show(Window.GetWindow(this)!,
                    $"Version {newVersion} of Multi-AI Chatbot is available (you have {Updater.CurrentVersionText}).\n\n" +
                    "Update now? The app will restart.",
                    "Update available", MessageBoxButton.YesNo, MessageBoxImage.Information);
                if (answer == MessageBoxResult.Yes)
                {
                    _busy = false;
                    await InstallAsync(update);
                }
            }
        }
        catch (Exception ex)
        {
            if (!quiet) UpdateStatusText.Text = $"Couldn't check for updates: {ex.Message}";
        }
        finally
        {
            _busy = false;
            UpdateButton.IsEnabled = true;
        }
    }

    private async Task<bool> InstallAsync(UpdateInfo update)
    {
        if (_busy) return false;
        _busy = true;
        UpdateButton.IsEnabled = false;
        var progress = new Progress<double>(p => UpdateStatusText.Text = $"Downloading update... {p:P0}");
        UpdateStatusText.Text = "Downloading update...";
        try
        {
            await Updater.DownloadAndInstallAsync(update, progress);
            UpdateStatusText.Text = "Restarting...";
            Application.Current.Shutdown();
            return true;
        }
        catch (Exception ex)
        {
            UpdateStatusText.Text = $"Couldn't install the update: {ex.Message}";
            _busy = false;
            UpdateButton.IsEnabled = true;
            return false;
        }
    }
}
