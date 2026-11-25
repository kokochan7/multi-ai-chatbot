using System.Windows;
using System.ComponentModel;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;
using MultiAIChatbot.Services;

namespace MultiAIChatbot;

public partial class MainWindow : Window
{
    private readonly ICollectionView? _recentView;

    public MainWindow()
    {
        InitializeComponent();
        VersionLabel.Text = $"Version {Updater.CurrentVersionText}";
        SourceInitialized += (_, _) => Theme.ApplyTitleBar(this);
        SidebarColumn.Width = new GridLength(AppState.Settings.SidebarWidth);

        _recentView = CollectionViewSource.GetDefaultView(AppState.History);
        _recentView.Filter = MatchesSearch;
        RecentList.ItemsSource = _recentView;
        AppState.History.CollectionChanged += (_, _) => UpdateRecentEmpty();
        UpdateRecentEmpty();

        // Keep the Recent highlight on whatever the History page is showing.
        HistoryPage.CurrentChanged += entry =>
        {
            if (NavHistory.IsChecked == true) RecentList.SelectedItem = entry;
        };

        HistoryPage.AskAgainRequested += prompt =>
        {
            NavChat.IsChecked = true;
            ChatPage.SetPrompt(prompt);
        };

        SettingsPage.UpdateFound += version =>
        {
            UpdateBannerText.Text = $"Update to {version}";
            UpdateBanner.Visibility = Visibility.Visible;
        };

        Loaded += async (_, _) =>
        {
            if (!AppState.Settings.CheckForUpdatesOnStartup) return;
            await SettingsPage.CheckAsync(askToInstall: true, quiet: true);

            // The app often stays open for hours, so keep checking quietly. A new version
            // shows an "Update to …" button in the menu instead of interrupting.
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(20) };
            timer.Tick += async (_, _) =>
            {
                if (AppState.Settings.CheckForUpdatesOnStartup && UpdateBanner.Visibility != Visibility.Visible)
                    await SettingsPage.CheckAsync(askToInstall: false, quiet: true);
            };
            timer.Start();
        };
    }

    private void UpdateRecentEmpty()
    {
        var hasHistory = AppState.History.Count > 0;
        var anyShown = _recentView is { IsEmpty: false };
        RecentEmpty.Text = hasHistory ? "No matches." : "Your questions will show up here.";
        RecentEmpty.Visibility = anyShown ? Visibility.Collapsed : Visibility.Visible;
        ClearAllButton.Visibility = hasHistory ? Visibility.Visible : Visibility.Collapsed;
    }

    private bool MatchesSearch(object item)
    {
        var q = SearchBox.Text.Trim();
        if (q.Length == 0 || item is not HistoryEntry e) return true;
        return new[] { e.Prompt, e.ChatGpt, e.Claude, e.Gemini }
            .Any(t => t.Contains(q, StringComparison.OrdinalIgnoreCase));
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_recentView is null) return;   // fires while the window is being built
        SearchPlaceholder.Visibility = SearchBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        _recentView.Refresh();
        UpdateRecentEmpty();
    }

    private void ClearAll_Click(object sender, RoutedEventArgs e)
    {
        if (AppState.History.Count == 0) return;
        var answer = MessageBox.Show(this, "Delete all saved questions and answers?",
            "Clear history", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (answer == MessageBoxResult.Yes) AppState.ClearHistory();
    }

    private void Nav_Checked(object sender, RoutedEventArgs e)
    {
        // Checked can fire while the window is still being built.
        if (ChatPage is null || HistoryPage is null || SettingsPage is null) return;

        ChatPage.Visibility = sender == NavChat ? Visibility.Visible : Visibility.Collapsed;
        HistoryPage.Visibility = sender == NavHistory ? Visibility.Visible : Visibility.Collapsed;
        SettingsPage.Visibility = sender == NavSettings ? Visibility.Visible : Visibility.Collapsed;
        if (RecentList is null) return;
        if (sender != NavHistory)
        {
            RecentList.SelectedItem = null;
        }
        else
        {
            // Opening History shows the newest question if nothing is picked yet.
            if (HistoryPage.Current is null && AppState.History.Count > 0) HistoryPage.Show(AppState.History[0]);
            RecentList.SelectedItem = HistoryPage.Current;
        }
    }

    /// <summary>Opens the clicked recent question on the History page.</summary>
    private void RecentList_Click(object sender, MouseButtonEventArgs e)
    {
        if ((e.OriginalSource as FrameworkElement)?.DataContext is HistoryEntry entry)
            OpenInHistory(entry);
    }

    private void RecentList_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && RecentList.SelectedItem is HistoryEntry entry)
            OpenInHistory(entry);
    }

    private void OpenInHistory(HistoryEntry entry)
    {
        HistoryPage.Show(entry);
        NavHistory.IsChecked = true;
        RecentList.SelectedItem = entry;
    }

    private async void UpdateBanner_Click(object sender, RoutedEventArgs e)
    {
        UpdateBanner.IsEnabled = false;
        UpdateBannerText.Text = "Updating…";
        if (await SettingsPage.InstallPendingAsync()) return;   // the app restarts
        UpdateBanner.IsEnabled = true;
        UpdateBannerText.Text = "Update failed, see Settings";
    }

    /// <summary>Puts the layout back to how it was when the app was first installed.</summary>
    private void ResetView_Click(object sender, RoutedEventArgs e)
    {
        var s = AppState.Settings;
        s.SidebarWidth = new AppSettings().SidebarWidth;
        SidebarColumn.Width = new GridLength(s.SidebarWidth);
        s.ColumnWidths.Clear();
        s.ColumnOrder = AiSites.AllKeys.ToList();
        AppState.SaveSettings();   // rearranges the columns
        NavChat.IsChecked = true;
        ChatPage.ShrinkPromptBox();
    }

    private void NewChat_Click(object sender, RoutedEventArgs e)
    {
        NavChat.IsChecked = true;
        ChatPage.StartNewChat();
    }

    private void SidebarSplitter_DragCompleted(object sender, DragCompletedEventArgs e)
    {
        AppState.Settings.SidebarWidth = Math.Round(SidebarColumn.ActualWidth);
        AppState.SaveSettingsQuietly();
    }
}
