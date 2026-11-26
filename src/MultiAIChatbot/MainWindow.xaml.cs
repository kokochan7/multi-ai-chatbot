using System.Windows;
using System.ComponentModel;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
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

        _recentView = CollectionViewSource.GetDefaultView(AppState.Conversations);
        _recentView.Filter = MatchesSearch;
        RecentList.ItemsSource = _recentView;
        RecentList.ContextMenu = _recentMenu;
        AppState.Conversations.CollectionChanged += (_, _) => UpdateRecentEmpty();
        UpdateRecentEmpty();

        // Keep the Recent highlight on whatever the History or Compare page is showing.
        HistoryPage.CurrentChanged += entry =>
        {
            if (NavHistory.IsChecked == true) RecentList.SelectedItem = entry;
        };
        ComparePage.CurrentChanged += entry =>
        {
            if (NavCompare.IsChecked == true) RecentList.SelectedItem = entry == null ? null : AppState.ChatOf(entry);
        };

        HistoryPage.RefreshRequested = ChatPage.RefreshAnswersAsync;
        HistoryPage.CompareRequested += entry =>
        {
            ComparePage.Show(entry);
            _comparePicked = true;
            if (NavCompare.IsChecked == true) _comparePicked = false;
            else NavCompare.IsChecked = true;
        };

        SettingsPage.ShowChatRequested += () => NavChat.IsChecked = true;

        // Ctrl+F / F3 / Esc for "Search all AIs". Keys pressed inside a website arrive as KeyDown
        // from the WebView2 control, so listen to both the preview and the normal key event.
        PreviewKeyDown += Window_FindKeyDown;
        AddHandler(KeyDownEvent, new KeyEventHandler(Window_FindKeyDown));

        SettingsPage.UpdateFound += version =>
        {
            UpdateBannerText.Text = $"Update to {version}";
            UpdateBanner.Visibility = Visibility.Visible;
        };

        Loaded += async (_, _) =>
        {
            // Check once when the app starts. No periodic checks while it stays open.
            if (!AppState.Settings.CheckForUpdatesOnStartup) return;
            await SettingsPage.CheckAsync(askToInstall: true, quiet: true);
        };
    }

    private void UpdateRecentEmpty()
    {
        if (RecentEmpty is null || ClearAllButton is null) return;
        var hasHistory = AppState.Conversations.Count > 0;
        bool anyShown;
        try { anyShown = _recentView is { IsEmpty: false }; }
        catch { anyShown = hasHistory; }
        RecentEmpty.Text = hasHistory ? "No matches." : "Your questions will show up here.";
        RecentEmpty.Visibility = anyShown ? Visibility.Collapsed : Visibility.Visible;
        ClearAllButton.Visibility = hasHistory ? Visibility.Visible : Visibility.Collapsed;
    }

    private bool MatchesSearch(object item)
    {
        var q = SearchBox.Text.Trim();
        if (q.Length == 0 || item is not Conversation chat) return true;
        return chat.Entries.Any(e => new[] { e.Prompt, e.ChatGpt, e.Claude, e.Gemini }
            .Any(t => t.Contains(q, StringComparison.OrdinalIgnoreCase)));
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
        if (AppState.Conversations.Count == 0) return;
        var answer = MessageBox.Show(this, "Delete all saved questions and answers?",
            "Clear history", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (answer == MessageBoxResult.Yes) AppState.ClearHistory();
    }

    private bool _comparePicked;

    private void Nav_Checked(object sender, RoutedEventArgs e)
    {
        // Checked can fire while the window is still being built.
        if (ChatPage is null || HistoryPage is null || SettingsPage is null || ComparePage is null) return;

        ChatPage.Visibility = sender == NavChat ? Visibility.Visible : Visibility.Collapsed;
        HistoryPage.Visibility = sender == NavHistory ? Visibility.Visible : Visibility.Collapsed;
        SettingsPage.Visibility = sender == NavSettings ? Visibility.Visible : Visibility.Collapsed;
        ComparePage.Visibility = sender == NavCompare ? Visibility.Visible : Visibility.Collapsed;
        // From the menu, Compare shows the latest question; History's Compare link picks one itself.
        if (sender == NavCompare && !_comparePicked) ComparePage.ShowLatest();
        _comparePicked = false;
        if (RecentList is null) return;
        if (sender == NavCompare)
        {
            RecentList.SelectedItem = ComparePage.Current is { } compared ? AppState.ChatOf(compared) : null;
        }
        else if (sender != NavHistory)
        {
            RecentList.SelectedItem = null;
        }
        else
        {
            // Opening History shows the newest question if nothing is picked yet.
            if (HistoryPage.Current is null && AppState.Conversations.Count > 0) HistoryPage.Show(AppState.Conversations[0]);
            RecentList.SelectedItem = HistoryPage.Current;
        }
    }

    /// <summary>
    /// Opens the clicked recent chat: on the Compare page when that's open, otherwise on History.
    /// </summary>
    private void RecentList_Click(object sender, MouseButtonEventArgs e)
    {
        if (IsInsideTextBox(e.OriginalSource as DependencyObject)) return;
        if ((e.OriginalSource as FrameworkElement)?.DataContext is Conversation entry && !entry.IsRenaming)
            OpenChat(entry);
    }

    private void Window_FindKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Handled || ChatPage is null || ChatPage.Visibility != Visibility.Visible) return;
        // Esc while renaming a Recent chat belongs to the rename box, not the search bar.
        if (e.Key == Key.Escape && Keyboard.FocusedElement is DependencyObject focused && !ChatPage.IsAncestorOf(focused)) return;
        ChatPage.HandleFindKey(e);
    }

    private void RecentList_KeyDown(object sender, KeyEventArgs e)
    {
        if (RecentList.SelectedItem is not Conversation entry || entry.IsRenaming) return;
        if (e.Key == Key.Enter) OpenChat(entry);
        else if (e.Key == Key.F2) StartRename(entry);
    }

    private void OpenChat(Conversation chat)
    {
        if (NavCompare.IsChecked == true)
        {
            ComparePage.ShowChat(chat);
            RecentList.SelectedItem = chat;
        }
        else
        {
            OpenInHistory(chat);
        }
    }

    private static bool IsInsideTextBox(DependencyObject? d)
    {
        while (d != null)
        {
            if (d is TextBox) return true;
            d = d is System.Windows.Media.Visual or System.Windows.Media.Media3D.Visual3D
                ? System.Windows.Media.VisualTreeHelper.GetParent(d)
                : LogicalTreeHelper.GetParent(d);
        }
        return false;
    }

    private readonly ContextMenu _recentMenu = new();

    /// <summary>Fills the right-click menu for the chat under the mouse (nothing for empty space).</summary>
    private void RecentList_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if ((e.OriginalSource as FrameworkElement)?.DataContext is not Conversation chat)
        {
            e.Handled = true;
            return;
        }
        _recentMenu.SetResourceReference(Control.BackgroundProperty, "CardBackground");
        _recentMenu.SetResourceReference(Control.ForegroundProperty, "TextPrimary");
        _recentMenu.SetResourceReference(Control.BorderBrushProperty, "CardBorder");
        _recentMenu.Items.Clear();
        _recentMenu.Items.Add(MenuItemFor("Rename", "\uE8AC", chat, RecentRename_Click));
        _recentMenu.Items.Add(MenuItemFor(chat.PinMenuText, "\uE718", chat, RecentPin_Click));
        _recentMenu.Items.Add(new Separator());
        var delete = MenuItemFor("Delete chat", "\uE74D", chat, RecentDelete_Click);
        delete.SetResourceReference(Control.ForegroundProperty, "ErrorText");
        _recentMenu.Items.Add(delete);
    }

    private MenuItem MenuItemFor(string header, string glyph, Conversation chat, RoutedEventHandler click)
    {
        var icon = new TextBlock { Text = glyph, FontSize = 13 };
        icon.SetResourceReference(TextBlock.FontFamilyProperty, "IconFont");
        var item = new MenuItem { Header = header, Icon = icon, DataContext = chat };
        item.Click += click;
        return item;
    }

    // Right-click > Rename: edit the chat's name right in the Recent list.
    private void RecentRename_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not Conversation chat) return;
        StartRename(chat);
    }

    /// <summary>Starts editing a chat's name in the Recent list and puts the cursor in the box.</summary>
    private void StartRename(Conversation chat)
    {
        foreach (var other in AppState.Conversations) other.IsRenaming = false;
        chat.IsRenaming = true;
        Dispatcher.InvokeAsync(() =>
        {
            if (RecentList.ItemContainerGenerator.ContainerFromItem(chat) is not DependencyObject container) return;
            if (FindChild<TextBox>(container) is not { } box) return;
            box.Text = chat.Title;
            box.Focus();
            box.SelectAll();
        }, System.Windows.Threading.DispatcherPriority.Loaded);
    }

    private static T? FindChild<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(parent, i);
            if (child is T found) return found;
            if (FindChild<T>(child) is { } deeper) return deeper;
        }
        return null;
    }

    // Enter saves the new name, Esc cancels (the rename box sits inside the Recent list).
    private void RecentList_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.OriginalSource is not TextBox { DataContext: Conversation chat } box || !chat.IsRenaming) return;
        if (e.Key == Key.Enter) { FinishRename(chat, box.Text); RecentList.Focus(); e.Handled = true; }
        else if (e.Key == Key.Escape) { chat.IsRenaming = false; RecentList.Focus(); e.Handled = true; }
    }

    // Clicking elsewhere saves the new name.
    private void RecentList_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (e.OriginalSource is TextBox { DataContext: Conversation chat } box && chat.IsRenaming)
            FinishRename(chat, box.Text);
    }

    private static void FinishRename(Conversation chat, string text)
    {
        chat.IsRenaming = false;
        var name = text.Trim();
        // Clearing the name (or typing the first question again) goes back to using the first question.
        var first = chat.Entries.Count > 0 ? chat.Entries[0].Prompt : "";
        chat.CustomTitle = name.Length == 0 || name == first ? null : name;
        AppState.SaveChats();
    }

    // Right-click > Pin to top / Unpin.
    private void RecentPin_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not Conversation chat) return;
        AppState.SetPinned(chat, !chat.Pinned);
    }

    /// <summary>Right-click > Delete chat on a Recent item: removes the chat, its answers and its pictures.</summary>
    private void RecentDelete_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not Conversation chat) return;
        var title = chat.Title.Length > 80 ? chat.Title[..77].TrimEnd() + "…" : chat.Title;
        var answer = MessageBox.Show(this,
            $"Delete \"{title}\"?\n\nIts questions, answers and pictures will be removed from this PC.",
            "Delete chat", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (answer != MessageBoxResult.Yes) return;

        var index = AppState.Conversations.IndexOf(chat);
        var wasOpen = HistoryPage.Current == chat;
        var comparedHere = ComparePage.Current is { } compared && chat.Entries.Contains(compared);
        AppState.DeleteConversation(chat);

        var chats = AppState.Conversations;
        if (wasOpen)
        {
            var next = chats.Count == 0 ? null : chats[Math.Clamp(index, 0, chats.Count - 1)];
            HistoryPage.Show(next);
            if (NavHistory.IsChecked == true) RecentList.SelectedItem = next;
        }
        if (comparedHere) ComparePage.ShowLatest();
        if (NavCompare.IsChecked == true)
            RecentList.SelectedItem = ComparePage.Current is { } c ? AppState.ChatOf(c) : null;
    }

    private void OpenInHistory(Conversation entry)
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

/// <summary>Visible when false, collapsed when true.</summary>
public sealed class InverseBoolToVisibility : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture) =>
        value is true ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture) =>
        throw new NotSupportedException();
}
