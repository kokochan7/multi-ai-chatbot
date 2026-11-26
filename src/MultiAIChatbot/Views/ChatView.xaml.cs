using System.Windows;
using System.Windows.Controls;
using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Input;
using Microsoft.Win32;
using MultiAIChatbot.Controls;
using MultiAIChatbot.Services;

namespace MultiAIChatbot.Views;

public partial class ChatView : UserControl
{
    private const double ColumnGap = 6;

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
        AttachmentList.ItemsSource = _attachments;
        _attachments.CollectionChanged += (_, _) =>
            AttachmentList.Visibility = _attachments.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        AppState.SettingsChanged += ApplyLayout;
        AppState.LogOutRequested += LogOutAllAsync;
        Loaded += (_, _) => PromptBox.Focus();
        _findDelay.Tick += (_, _) => { _findDelay.Stop(); _ = RunFindAsync(0); };
        AppState.SettingsChanged += () => { if (FindBar.Visibility == Visibility.Visible) BuildFindCounts(); };

        // Ctrl+V of a picture or copied files attaches them instead of pasting nothing.
        CommandManager.AddPreviewCanExecuteHandler(PromptBox, PromptBox_PreviewCanPaste);
        CommandManager.AddPreviewExecutedHandler(PromptBox, PromptBox_PreviewPaste);
    }

    private readonly List<GridSplitter> _splitters = new();
    private readonly ObservableCollection<Attachment> _attachments = new();

    /// <summary>The chat that new questions belong to in History; New Chat starts another one.</summary>
    private Guid _conversationId = Guid.NewGuid();
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

    // ---- Search all AIs (Ctrl+F) ----

    private readonly System.Windows.Threading.DispatcherTimer _findDelay =
        new() { Interval = TimeSpan.FromMilliseconds(220) };
    private int _findRun;

    /// <summary>
    /// Handles Ctrl+F, F3 / Shift+F3, Ctrl+G and Esc for the search bar. MainWindow passes every key
    /// here while the Chat page is showing, including keys pressed inside a website.
    /// </summary>
    public void HandleFindKey(KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        var mods = Keyboard.Modifiers;
        var ctrl = (mods & ModifierKeys.Control) != 0 && (mods & ModifierKeys.Alt) == 0;
        var shift = (mods & ModifierKeys.Shift) != 0;
        if (key == Key.F && ctrl)
        {
            OpenFind();
        }
        else if (key == Key.F3 || (key == Key.G && ctrl))
        {
            if (FindBar.Visibility != Visibility.Visible) OpenFind();
            else _ = RunFindAsync(shift ? -1 : 1);
        }
        else if (key == Key.Escape && FindBar.Visibility == Visibility.Visible)
        {
            CloseFind();
        }
        else return;
        e.Handled = true;
    }

    /// <summary>Shows the search bar and puts the cursor in it (searching again if it already has text).</summary>
    public void OpenFind()
    {
        var wasOpen = FindBar.Visibility == Visibility.Visible;
        FindBar.Visibility = Visibility.Visible;
        if (!wasOpen) BuildFindCounts();
        Dispatcher.InvokeAsync(() =>
        {
            FindBox.Focus();
            Keyboard.Focus(FindBox);
            FindBox.SelectAll();
        }, System.Windows.Threading.DispatcherPriority.Input);
        if (!wasOpen && FindBox.Text.Length > 0) _ = RunFindAsync(0);
    }

    private void CloseFind()
    {
        _findDelay.Stop();
        _findRun++;
        FindBar.Visibility = Visibility.Collapsed;
        foreach (var pane in AllPanes) _ = pane.ClearFindAsync();
        PromptBox.Focus();
    }

    private void OpenFind_Click(object sender, RoutedEventArgs e) => OpenFind();
    private void FindClose_Click(object sender, RoutedEventArgs e) => CloseFind();
    private void FindNext_Click(object sender, RoutedEventArgs e) => _ = RunFindAsync(1);
    private void FindPrevious_Click(object sender, RoutedEventArgs e) => _ = RunFindAsync(-1);

    private void FindBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        FindPlaceholder.Visibility = FindBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        // Search as you type, after a short pause so each keystroke doesn't search three pages.
        _findDelay.Stop();
        _findDelay.Start();
    }

    private void FindBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            // Enter right after typing runs the pending search first; after that it moves on.
            var pending = _findDelay.IsEnabled;
            _findDelay.Stop();
            _ = RunFindAsync(pending ? 0 : (Keyboard.Modifiers & ModifierKeys.Shift) != 0 ? -1 : 1);
        }
        else if (e.Key == Key.Escape)
        {
            e.Handled = true;
            CloseFind();
        }
    }

    // One count label per shown column, in column order.
    private readonly Dictionary<AiPane, TextBlock> _findCountText = new();

    private void BuildFindCounts()
    {
        FindCounts.Children.Clear();
        _findCountText.Clear();
        foreach (var pane in VisiblePanes)
        {
            var dot = new System.Windows.Shapes.Ellipse { Width = 7, Height = 7, Fill = pane.Accent, Margin = new Thickness(0, 0, 5, 0), VerticalAlignment = VerticalAlignment.Center };
            var label = new TextBlock { FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
            label.SetResourceReference(TextBlock.ForegroundProperty, "TextMuted");
            var chip = new Border
            {
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(8, 2, 8, 2),
                Margin = new Thickness(0, 0, 4, 0),
                Child = new StackPanel { Orientation = Orientation.Horizontal, Children = { dot, label } },
            };
            chip.SetResourceReference(Border.BackgroundProperty, "NavHover");
            FindCounts.Children.Add(chip);
            _findCountText[pane] = label;
            SetFindCount(pane, null, empty: true);
        }
    }

    private void SetFindCount(AiPane pane, (int Count, int Index)? result, bool empty = false)
    {
        if (!_findCountText.TryGetValue(pane, out var label)) return;
        label.Text = empty ? pane.Title
            : result is not { } r ? $"{pane.Title} –"
            : r.Count == 0 ? $"{pane.Title} 0"
            : $"{pane.Title} {r.Index + 1}/{r.Count}";
        label.ToolTip = empty ? null
            : result is null ? "This page couldn't be searched yet"
            : $"{result.Value.Count} match{(result.Value.Count == 1 ? "" : "es")} on {pane.Title}";
        var found = result is { } hit && hit.Count > 0;
        label.SetResourceReference(TextBlock.ForegroundProperty, found ? "TextPrimary" : "TextMuted");
        label.FontWeight = found ? FontWeights.SemiBold : FontWeights.Normal;
    }

    /// <summary>Searches every shown column at once (step 0 = from the top, 1 = next, -1 = previous).</summary>
    private async Task RunFindAsync(int step)
    {
        if (FindBar.Visibility != Visibility.Visible) return;
        var run = ++_findRun;
        var query = FindBox.Text;
        var panes = VisiblePanes.ToList();
        if (panes.Count != _findCountText.Count || panes.Any(p => !_findCountText.ContainsKey(p))) BuildFindCounts();

        if (query.Length == 0)
        {
            foreach (var pane in panes) { _ = pane.ClearFindAsync(); SetFindCount(pane, null, empty: true); }
            FindBar.SetResourceReference(Border.BorderBrushProperty, "Accent");
            return;
        }

        var results = await Task.WhenAll(panes.Select(p => p.FindAsync(query, step)));
        if (run != _findRun) return;   // a newer search already started
        for (var i = 0; i < panes.Count; i++) SetFindCount(panes[i], results[i]);
        var none = results.All(r => r is null || r.Value.Count == 0);
        FindBar.SetResourceReference(Border.BorderBrushProperty, none ? "ErrorText" : "Accent");
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

    // ---- Chat box size ----

    private void PromptBox_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        ShrinkButton.Visibility = PromptBox.ActualHeight > 60 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Shrink_Click(object sender, RoutedEventArgs e) => ShrinkPromptBox();

    /// <summary>
    /// Puts the chat box back to its normal one-line size: removes the blank lines that
    /// made it grow (Shift+Enter) and scrolls back to the start of any longer text.
    /// </summary>
    public void ShrinkPromptBox()
    {
        var trimmed = PromptBox.Text.Trim();
        if (trimmed != PromptBox.Text) PromptBox.Text = trimmed;
        PromptBox.CaretIndex = PromptBox.Text.Length;
        PromptBox.ScrollToHome();
        PromptBox.Focus();
    }

    // ---- Attachments ----

    private void Attach_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Attach files for all AIs",
            Multiselect = true,
            Filter = "All files|*.*|Images|*.png;*.jpg;*.jpeg;*.gif;*.webp;*.bmp|Documents|*.pdf;*.txt;*.md;*.csv;*.docx;*.xlsx;*.pptx",
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) == true) AddFiles(dialog.FileNames);
    }

    private static bool HasFiles(DragEventArgs e) => DroppedFiles.HasFileLikeData(e.Data, fromPaste: false);

    private void PromptBar_PreviewDragOver(object sender, DragEventArgs e)
    {
        if (!HasFiles(e)) return;   // let plain text drags reach the text box as usual
        e.Effects = DragDropEffects.Copy;
        e.Handled = true;
        PromptBarBorder.SetResourceReference(Border.BorderBrushProperty, "Accent");
    }

    private void PromptBar_PreviewDragLeave(object sender, DragEventArgs e)
    {
        PromptBarBorder.SetResourceReference(Border.BorderBrushProperty, "CardBorder");
    }

    private void PromptBar_PreviewDrop(object sender, DragEventArgs e)
    {
        PromptBarBorder.SetResourceReference(Border.BorderBrushProperty, "CardBorder");
        if (!HasFiles(e)) return;
        e.Handled = true;   // stop the text box from typing the picture's address
        _ = AttachFromDataAsync(e.Data, fromPaste: false);
    }

    private static IDataObject? ClipboardFiles()
    {
        try
        {
            var data = Clipboard.GetDataObject();
            return data != null && DroppedFiles.HasFileLikeData(data, fromPaste: true) ? data : null;
        }
        catch { return null; }   // the clipboard can be busy in another app
    }

    private void PromptBox_PreviewCanPaste(object sender, CanExecuteRoutedEventArgs e)
    {
        // The text box only enables Paste for text; turn it on for pictures and files too.
        if (e.Command != ApplicationCommands.Paste || ClipboardFiles() == null) return;
        e.CanExecute = true;
        e.Handled = true;
    }

    private void PromptBox_PreviewPaste(object sender, ExecutedRoutedEventArgs e)
    {
        if (e.Command != ApplicationCommands.Paste) return;
        if (ClipboardFiles() is not { } data) return;
        e.Handled = true;
        _ = AttachFromDataAsync(data, fromPaste: true);
    }

    /// <summary>
    /// Attaches whatever was dropped or pasted: files, a picture from a web page or app, or a
    /// picture's web address (downloaded first). A link to a normal web page is typed as text.
    /// </summary>
    private async Task AttachFromDataAsync(IDataObject data, bool fromPaste)
    {
        var temp = new List<string>();
        try
        {
            var found = DroppedFiles.Extract(data, fromPaste);   // must be read before the drop ends
            var paths = new List<string>(found.Paths);
            temp.AddRange(found.Paths);
            if (found.Urls.Count > 0)
            {
                Mouse.OverrideCursor = Cursors.AppStarting;
                try
                {
                    foreach (var url in found.Urls)
                    {
                        var path = await DroppedFiles.DownloadAsync(url);
                        if (path != null) { paths.Add(path); temp.Add(path); }
                    }
                }
                finally { Mouse.OverrideCursor = null; }
            }

            if (paths.Count > 0) AddFiles(paths);
            else if (!string.IsNullOrWhiteSpace(found.Text)) InsertText(found.Text.Trim());
        }
        catch (Exception ex)
        {
            MessageBox.Show(Window.GetWindow(this)!, "Couldn't attach that: " + ex.Message,
                "Attach files", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        finally
        {
            DroppedFiles.Cleanup(temp);   // only removes the app's own temporary copies
        }
    }

    private void InsertText(string text)
    {
        PromptBox.SelectedText = text;
        PromptBox.CaretIndex = PromptBox.SelectionStart + PromptBox.SelectionLength;
        PromptBox.Focus();
    }

    private void AddFiles(IEnumerable<string> paths)
    {
        var skipped = new List<string>();
        foreach (var path in paths)
        {
            try
            {
                if (Directory.Exists(path)) { skipped.Add($"{Path.GetFileName(path)} (folder)"); continue; }
                var size = new FileInfo(path).Length;
                if (_attachments.Sum(a => a.Size) + size > Attachment.MaxTotalBytes)
                {
                    skipped.Add($"{Path.GetFileName(path)} (over the 25 MB total)");
                    continue;
                }
                if (_attachments.Any(a => a.Name == Path.GetFileName(path) && a.Size == size)) continue;
                _attachments.Add(Attachment.FromFile(path));
            }
            catch (Exception ex)
            {
                skipped.Add($"{Path.GetFileName(path)} ({ex.Message})");
            }
        }
        if (skipped.Count > 0)
            MessageBox.Show(Window.GetWindow(this)!, "These files weren't attached:\n\n" + string.Join("\n", skipped),
                "Attach files", MessageBoxButton.OK, MessageBoxImage.Information);
        PromptBox.Focus();
    }

    private void RemoveAttachment_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: Attachment a }) _attachments.Remove(a);
    }

    private async Task SendToAllAsync()
    {
        var prompt = PromptBox.Text.Trim();
        if (prompt.Length == 0 && _attachments.Count > 0)
        {
            Placeholder.Text = "Type a message to send with your files…";
            PromptBox.Focus();
            return;
        }
        if (prompt.Length == 0 || !SendButton.IsEnabled) return;

        var panes = VisiblePanes.ToList();
        if (panes.Count == 0) return;

        SendButton.IsEnabled = false;
        var entry = AppState.AddHistory(prompt, _conversationId);
        var capture = AppState.Settings.SaveAnswers;

        try
        {
            // Send to every visible site at the same time.
            var files = _attachments.ToList();
            // Each answer shows in History as it's being written, not only once it's done.
            var answerTasks = await Task.WhenAll(panes.Select(p => p.SendPromptAsync(prompt, capture, files,
                capture ? text => ShowPartialAnswer(entry, p.SiteKey, text) : null)));
            PromptBox.Clear();
            _attachments.Clear();

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

    private static DateTime _lastPartialSave = DateTime.MinValue;

    /// <summary>Shows the answer so far in History, saving it to disk every few seconds.</summary>
    private static void ShowPartialAnswer(HistoryEntry entry, string siteKey, string text)
    {
        if (text.Length == 0 || !AppState.History.Contains(entry)) return;
        entry.SetAnswer(siteKey, text);
        if (DateTime.UtcNow - _lastPartialSave > TimeSpan.FromSeconds(5))
        {
            _lastPartialSave = DateTime.UtcNow;
            AppState.SaveHistory();
        }
    }

    private static async Task SaveAnswerWhenReadyAsync(HistoryEntry entry, string siteKey, Task<string> answerTask)
    {
        try
        {
            var text = await answerTask;
            if (!AppState.History.Contains(entry)) return;
            if (text.Length > 0) entry.SetAnswer(siteKey, text);   // else keep whatever arrived live
            AppState.SaveHistory();
        }
        catch
        {
            // Not being able to read an answer is never worth crashing over.
        }
    }

    /// <summary>
    /// Re-reads the answers to one saved question from the AI sites, without asking again.
    /// Works for the chat that is still open on the sites (the one since the last New Chat).
    /// Returns a short result for the History page's button.
    /// </summary>
    public async Task<string> RefreshAnswersAsync(Conversation chat, HistoryEntry entry)
    {
        if (!AppState.Settings.SaveAnswers) return "Saving answers is off";
        if (chat.Id != _conversationId) return "Chat no longer open";
        var index = chat.Entries.IndexOf(entry);
        if (index < 0) return "Couldn't refresh";

        var updated = 0;
        foreach (var pane in AllPanes)
        {
            var text = await pane.ReadAnswerAsync(index, chat.Entries.Count);
            if (text.Length == 0 || text == entry.AnswerFor(pane.SiteKey)) continue;
            entry.SetAnswer(pane.SiteKey, text);
            updated++;
        }
        if (updated > 0) AppState.SaveHistory();
        return updated > 0 ? "Updated" : "Up to date";
    }

    /// <summary>Puts text in the prompt box.</summary>
    public void SetPrompt(string text)
    {
        PromptBox.Text = text;
        PromptBox.CaretIndex = text.Length;
        PromptBox.Focus();
    }

    /// <summary>Opens a fresh chat on every AI site (the New Chat button in the left menu).</summary>
    public void StartNewChat()
    {
        _conversationId = Guid.NewGuid();
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
