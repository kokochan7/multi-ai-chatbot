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
    /// <summary>Raised when the user clicks "Ask again", with the question text.</summary>
    public event Action<string>? AskAgainRequested;

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
        if (text.Length > 0) Clipboard.SetText(text);
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

    private void AskAgain_Click(object sender, RoutedEventArgs e)
    {
        if (EntryOf(sender) is { } entry) AskAgainRequested?.Invoke(entry.Prompt);
    }
}
