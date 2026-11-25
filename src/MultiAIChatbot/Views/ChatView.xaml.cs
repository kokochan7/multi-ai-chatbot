using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using MultiAIChatbot.Controls;
using MultiAIChatbot.Services;

namespace MultiAIChatbot.Views;

public partial class ChatView : UserControl
{
    private const double ColumnGap = 12;

    private AiPane[] AllPanes => new[] { ChatGptPane, ClaudePane, GeminiPane };

    /// <summary>The columns currently shown, left to right.</summary>
    private IEnumerable<AiPane> VisiblePanes =>
        AppState.Settings.ColumnOrder
            .Where(k => !AppState.Settings.HiddenSites.Contains(k))
            .Select(k => AllPanes.First(p => p.SiteKey == k));

    public ChatView()
    {
        InitializeComponent();
        ApplyLayout();
        AppState.SettingsChanged += ApplyLayout;
        AppState.LogOutRequested += LogOutAllAsync;
        Loaded += (_, _) => PromptBox.Focus();
    }

    /// <summary>Arranges the columns to match Settings (order and which ones are shown).</summary>
    private void ApplyLayout()
    {
        ColumnsGrid.ColumnDefinitions.Clear();
        foreach (var pane in AllPanes) pane.Visibility = Visibility.Collapsed;

        var col = 0;
        foreach (var pane in VisiblePanes)
        {
            if (col > 0)
            {
                ColumnsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(ColumnGap) });
                col++;
            }
            ColumnsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            Grid.SetColumn(pane, col);
            pane.Visibility = Visibility.Visible;
            col++;
        }
    }

    private void PromptBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        // Enter sends; Shift+Enter adds a new line.
        if (e.Key == Key.Enter && (Keyboard.Modifiers & ModifierKeys.Shift) == 0)
        {
            e.Handled = true;
            _ = SendToAllAsync();
        }
    }

    private void PromptBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        Placeholder.Visibility = string.IsNullOrEmpty(PromptBox.Text) ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Send_Click(object sender, RoutedEventArgs e) => _ = SendToAllAsync();

    private async Task SendToAllAsync()
    {
        var prompt = PromptBox.Text.Trim();
        if (prompt.Length == 0 || !SendButton.IsEnabled) return;

        var panes = VisiblePanes.ToList();
        if (panes.Count == 0) return;

        SendButton.IsEnabled = false;
        var entry = AppState.AddHistory(prompt);
        var capture = AppState.Settings.SaveAnswers;

        try
        {
            // Send to every visible site at the same time.
            var answerTasks = await Task.WhenAll(panes.Select(p => p.SendPromptAsync(prompt, capture)));
            PromptBox.Clear();

            // Collect the answers in the background as each site finishes.
            for (var i = 0; i < panes.Count; i++)
            {
                if (answerTasks[i] is { } answerTask)
                    _ = SaveAnswerWhenReadyAsync(entry, panes[i].SiteKey, answerTask);
            }
        }
        finally
        {
            SendButton.IsEnabled = true;
            PromptBox.Focus();
        }
    }

    private static async Task SaveAnswerWhenReadyAsync(HistoryEntry entry, string siteKey, Task<string> answerTask)
    {
        try
        {
            var text = await answerTask;
            if (text.Length == 0 || !AppState.History.Contains(entry)) return;
            entry.SetAnswer(siteKey, text);
            AppState.SaveHistory();
        }
        catch
        {
            // Not being able to read an answer is never worth crashing over.
        }
    }

    /// <summary>Puts text in the prompt box (used by History's "Ask again").</summary>
    public void SetPrompt(string text)
    {
        PromptBox.Text = text;
        PromptBox.CaretIndex = text.Length;
        PromptBox.Focus();
    }

    private void NewChat_Click(object sender, RoutedEventArgs e)
    {
        foreach (var pane in AllPanes) pane.GoHome();
        PromptBox.Focus();
    }

    private async Task LogOutAllAsync()
    {
        // All columns share one browser profile, so clearing it once logs out of every site.
        await ChatGptPane.ClearBrowsingDataAsync();
        foreach (var pane in AllPanes) pane.GoHome();
    }
}
