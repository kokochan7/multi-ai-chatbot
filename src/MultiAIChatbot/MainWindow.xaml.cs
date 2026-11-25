using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using MultiAIChatbot.Services;

namespace MultiAIChatbot;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        VersionLabel.Text = $"Version {Updater.CurrentVersionText}";
        SourceInitialized += (_, _) => Theme.ApplyTitleBar(this);
        SidebarColumn.Width = new GridLength(AppState.Settings.SidebarWidth);

        RecentList.ItemsSource = AppState.History;
        AppState.History.CollectionChanged += (_, _) => UpdateRecentEmpty();
        UpdateRecentEmpty();

        HistoryPage.AskAgainRequested += prompt =>
        {
            NavChat.IsChecked = true;
            ChatPage.SetPrompt(prompt);
        };

        Loaded += async (_, _) =>
        {
            if (AppState.Settings.CheckForUpdatesOnStartup)
                await SettingsPage.CheckAsync(askToInstall: true, quiet: true);
        };
    }

    private void UpdateRecentEmpty()
    {
        RecentEmpty.Visibility = AppState.History.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Nav_Checked(object sender, RoutedEventArgs e)
    {
        // Checked can fire while the window is still being built.
        if (ChatPage is null || HistoryPage is null || SettingsPage is null) return;

        ChatPage.Visibility = sender == NavChat ? Visibility.Visible : Visibility.Collapsed;
        HistoryPage.Visibility = sender == NavHistory ? Visibility.Visible : Visibility.Collapsed;
        SettingsPage.Visibility = sender == NavSettings ? Visibility.Visible : Visibility.Collapsed;
        if (sender != NavHistory && RecentList is not null) RecentList.SelectedItem = null;
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
        NavHistory.IsChecked = true;
        RecentList.SelectedItem = entry;
        HistoryPage.Show(entry);
    }

    private void SidebarSplitter_DragCompleted(object sender, DragCompletedEventArgs e)
    {
        AppState.Settings.SidebarWidth = Math.Round(SidebarColumn.ActualWidth);
        AppState.SaveSettingsQuietly();
    }
}
