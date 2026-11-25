using System.Windows;
using System.Windows.Controls;
using MultiAIChatbot.Services;

namespace MultiAIChatbot.Views;

/// <summary>
/// Shows one past question with all its answers side by side. The list of questions
/// lives in the left menu (Recent), which calls <see cref="Show"/>.
/// </summary>
public partial class HistoryView : UserControl
{
    /// <summary>Raised when the user clicks "Ask again", with the question text.</summary>
    public event Action<string>? AskAgainRequested;

    /// <summary>Raised when the shown question changes (e.g. after Delete), so the menu can follow.</summary>
    public event Action<HistoryEntry?>? CurrentChanged;

    public HistoryEntry? Current { get; private set; }

    public HistoryView()
    {
        InitializeComponent();
        AppState.History.CollectionChanged += (_, _) =>
        {
            if (Current != null && !AppState.History.Contains(Current)) Show(null);
        };
    }

    /// <summary>Shows one question, or the "pick a question" message when null.</summary>
    public void Show(HistoryEntry? entry)
    {
        if (Current == entry) return;
        Current = entry;
        DetailPanel.DataContext = entry;
        DetailPanel.Visibility = entry != null ? Visibility.Visible : Visibility.Collapsed;
        NoSelectionText.Visibility = entry != null ? Visibility.Collapsed : Visibility.Visible;
        CurrentChanged?.Invoke(entry);
    }

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        if (Current is not { } entry || sender is not Button { Tag: string key }) return;
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
        if (Current is not { } entry) return;
        var index = AppState.History.IndexOf(entry);
        AppState.DeleteHistory(entry);
        // Move on to the next question so the page isn't left empty.
        var history = AppState.History;
        Show(history.Count == 0 ? null : history[Math.Clamp(index, 0, history.Count - 1)]);
    }

    private void AskAgain_Click(object sender, RoutedEventArgs e)
    {
        if (Current is { } entry) AskAgainRequested?.Invoke(entry.Prompt);
    }
}
