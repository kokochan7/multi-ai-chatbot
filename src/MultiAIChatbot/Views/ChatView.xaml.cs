using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using MultiAIChatbot.Controls;
using MultiAIChatbot.Services;

namespace MultiAIChatbot.Views;

public partial class ChatView : UserControl
{
    private const double ColumnGap = 14;

    private AiPane[] AllPanes => new[] { ChatGptPane, ClaudePane, GeminiPane };

    /// <summary>The columns currently shown, left to right.</summary>
    private IEnumerable<AiPane> VisiblePanes =>
        AppState.Settings.ColumnOrder
            .Where(k => !AppState.Settings.HiddenSites.Contains(k))
            .Select(k => AllPanes.First(p => p.SiteKey == k));

    public ChatView()
    {
        InitializeComponent();
        foreach (var pane in AllPanes)
        {
            pane.HeaderDragMoved += Pane_HeaderDragMoved;
            pane.HeaderDropped += Pane_HeaderDropped;
            pane.HeaderDragCancelled += Pane_HeaderDragCancelled;
        }
        ApplyLayout();
        AppState.SettingsChanged += ApplyLayout;
        AppState.LogOutRequested += LogOutAllAsync;
        Loaded += (_, _) => PromptBox.Focus();
    }

    private readonly List<GridSplitter> _splitters = new();
    private AiPane? _dropTarget;

    /// <summary>
    /// Arranges the columns to match Settings: order, which ones are shown, and their widths.
    /// The gaps between columns are draggable dividers.
    /// </summary>
    private void ApplyLayout()
    {
        foreach (var splitter in _splitters) ColumnsGrid.Children.Remove(splitter);
        _splitters.Clear();
        ColumnsGrid.ColumnDefinitions.Clear();
        foreach (var pane in AllPanes) pane.Visibility = Visibility.Collapsed;

        var widths = AppState.Settings.ColumnWidths;
        var col = 0;
        foreach (var pane in VisiblePanes)
        {
            if (col > 0)
            {
                ColumnsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(ColumnGap) });
                var splitter = new GridSplitter
                {
                    Style = (Style)FindResource("PanelSplitter"),
                    ToolTip = "Drag to resize",
                };
                splitter.DragCompleted += (_, _) => SaveColumnWidths();
                Grid.SetColumn(splitter, col);
                ColumnsGrid.Children.Add(splitter);
                _splitters.Add(splitter);
                col++;
            }
            var weight = widths.TryGetValue(pane.SiteKey, out var w) && w > 0.05 && w < 20 ? w : 1;
            ColumnsGrid.ColumnDefinitions.Add(new ColumnDefinition
            {
                Width = new GridLength(weight, GridUnitType.Star),
                MinWidth = 200,
            });
            Grid.SetColumn(pane, col);
            pane.Visibility = Visibility.Visible;
            col++;
        }
    }

    /// <summary>Remembers the column widths after a divider is dragged.</summary>
    private void SaveColumnWidths()
    {
        var panes = VisiblePanes.ToList();
        var actual = panes.Select(p => ColumnsGrid.ColumnDefinitions[Grid.GetColumn(p)].ActualWidth).ToList();
        var average = actual.Average();
        if (average <= 0) return;
        for (var i = 0; i < panes.Count; i++)
            AppState.Settings.ColumnWidths[panes[i].SiteKey] = Math.Round(actual[i] / average, 3);
        AppState.SaveSettingsQuietly();
    }

    // ---- Drag a column's header to reorder ----

    private AiPane? PaneUnder(MouseEventArgs e)
    {
        var x = e.GetPosition(ColumnsGrid).X;
        foreach (var pane in VisiblePanes)
        {
            var left = pane.TranslatePoint(new Point(0, 0), ColumnsGrid).X;
            if (x >= left - ColumnGap / 2 && x <= left + pane.ActualWidth + ColumnGap / 2) return pane;
        }
        return null;
    }

    private void SetDropTarget(AiPane? target)
    {
        if (target == _dropTarget) return;
        _dropTarget?.SetDragHighlight(AiPane.DragHighlight.None);
        _dropTarget = target;
        _dropTarget?.SetDragHighlight(AiPane.DragHighlight.DropTarget);
    }

    private void Pane_HeaderDragMoved(AiPane source, MouseEventArgs e)
    {
        var target = PaneUnder(e);
        SetDropTarget(target == source ? null : target);
    }

    private void Pane_HeaderDropped(AiPane source, MouseEventArgs e)
    {
        var target = PaneUnder(e);
        SetDropTarget(null);
        if (target is null || target == source) return;

        // The dragged column takes the target's place; the others shift over.
        var order = AppState.Settings.ColumnOrder;
        var targetIndex = order.IndexOf(target.SiteKey);
        order.Remove(source.SiteKey);
        order.Insert(Math.Min(targetIndex, order.Count), source.SiteKey);
        AppState.SaveSettings();   // also rearranges the columns via SettingsChanged
    }

    private void Pane_HeaderDragCancelled(AiPane source) => SetDropTarget(null);

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
