using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using MultiAIChatbot.Controls;

namespace MultiAIChatbot.Views;

public partial class ChatView : UserControl
{
    private AiPane[] Panes => new[] { ChatGptPane, ClaudePane, GeminiPane };

    public ChatView()
    {
        InitializeComponent();
        Loaded += (_, _) => PromptBox.Focus();
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

        SendButton.IsEnabled = false;
        try
        {
            // All three at once.
            await Task.WhenAll(Panes.Select(p => p.SendPromptAsync(prompt)));
            PromptBox.Clear();
        }
        finally
        {
            SendButton.IsEnabled = true;
            PromptBox.Focus();
        }
    }

    private void NewChat_Click(object sender, RoutedEventArgs e)
    {
        foreach (var pane in Panes) pane.GoHome();
        PromptBox.Focus();
    }
}
