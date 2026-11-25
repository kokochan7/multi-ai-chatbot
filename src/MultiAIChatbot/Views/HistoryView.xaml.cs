using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using MultiAIChatbot.Services;

namespace MultiAIChatbot.Views;

public partial class HistoryView : UserControl
{
    /// <summary>Raised when the user clicks "Ask again", with the question text.</summary>
    public event Action<string>? AskAgainRequested;

    private readonly ICollectionView _view;

    public HistoryView()
    {
        InitializeComponent();
        _view = CollectionViewSource.GetDefaultView(AppState.History);
        _view.Filter = Matches;
        EntryList.ItemsSource = _view;
        AppState.History.CollectionChanged += (_, _) => UpdateEmptyText();
        UpdateEmptyText();
    }

    private HistoryEntry? Selected => EntryList.SelectedItem as HistoryEntry;

    private bool Matches(object item)
    {
        var q = SearchBox.Text.Trim();
        if (q.Length == 0 || item is not HistoryEntry e) return true;
        return new[] { e.Prompt, e.ChatGpt, e.Claude, e.Gemini }
            .Any(t => t.Contains(q, StringComparison.OrdinalIgnoreCase));
    }

    private void UpdateEmptyText()
    {
        EmptyText.Visibility = AppState.History.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        SearchPlaceholder.Visibility = SearchBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        _view.Refresh();
    }

    private void EntryList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        DetailPanel.DataContext = Selected;
        DetailPanel.Visibility = Selected != null ? Visibility.Visible : Visibility.Collapsed;
        NoSelectionText.Visibility = Selected != null ? Visibility.Collapsed : Visibility.Visible;
    }

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } entry || sender is not Button { Tag: string key }) return;
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
        if (Selected is { } entry) AppState.DeleteHistory(entry);
    }

    private void ClearAll_Click(object sender, RoutedEventArgs e)
    {
        if (AppState.History.Count == 0) return;
        var answer = MessageBox.Show(Window.GetWindow(this)!, "Delete all saved questions and answers?",
            "Clear history", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (answer == MessageBoxResult.Yes) AppState.ClearHistory();
    }

    private void AskAgain_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is { } entry) AskAgainRequested?.Invoke(entry.Prompt);
    }
}
