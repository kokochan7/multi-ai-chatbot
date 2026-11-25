using System.Windows;
using System.Windows.Controls;
using MultiAIChatbot.Services;

namespace MultiAIChatbot.Views;

/// <summary>
/// Shows one chat: every question asked in it, each with all its answers side by side.
/// The list of chats lives in the left menu (Recent), which calls <see cref="Show"/>.
/// </summary>
public partial class HistoryView : UserControl
{
    /// <summary>Re-reads one question's answers from the AI sites; returns a short result to show.</summary>
    public Func<Conversation, HistoryEntry, Task<string>>? RefreshRequested { get; set; }

    /// <summary>Raised when the shown chat changes (e.g. after Delete), so the menu can follow.</summary>
    public event Action<Conversation?>? CurrentChanged;

    public Conversation? Current { get; private set; }

    public HistoryView()
    {
        InitializeComponent();
        AppState.Conversations.CollectionChanged += (_, _) =>
        {
            if (Current != null && !AppState.Conversations.Contains(Current)) Show(null);
        };
    }

    /// <summary>Shows one chat, or the "pick a chat" message when null.</summary>
    public void Show(Conversation? chat)
    {
        if (Current == chat) return;
        Current = chat;
        DetailPanel.DataContext = chat;
        DetailPanel.Visibility = chat != null ? Visibility.Visible : Visibility.Collapsed;
        NoSelectionText.Visibility = chat != null ? Visibility.Collapsed : Visibility.Visible;
        TurnsScroller.ScrollToTop();
        CurrentChanged?.Invoke(chat);
    }

    private static HistoryEntry? EntryOf(object sender) => (sender as FrameworkElement)?.DataContext as HistoryEntry;

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        if (EntryOf(sender) is not { } entry || sender is not Button { Tag: string key }) return;
        var text = key switch
        {
            "chatgpt" => entry.ChatGpt,
            "claude" => entry.Claude,
            "gemini" => entry.Gemini,
            _ => ""
        };
        var plain = MarkdownRenderer.ToPlainText(text);
        if (plain.Length > 0 && !string.IsNullOrWhiteSpace(text)) Clipboard.SetText(plain);
    }

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (Current is not { } chat) return;
        var index = AppState.Conversations.IndexOf(chat);
        AppState.DeleteConversation(chat);
        // Move on to the next chat so the page isn't left empty.
        var chats = AppState.Conversations;
        Show(chats.Count == 0 ? null : chats[Math.Clamp(index, 0, chats.Count - 1)]);
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || EntryOf(sender) is not { } entry || Current is not { } chat ||
            RefreshRequested is not { } refresh) return;
        button.IsEnabled = false;
        button.Content = "Refreshing…";
        string result;
        try { result = await refresh(chat, entry); }
        catch { result = "Couldn't refresh"; }
        button.Content = result;
        await Task.Delay(2500);
        button.Content = "Refresh";
        button.IsEnabled = true;
    }
}
