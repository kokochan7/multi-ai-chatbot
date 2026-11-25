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
        AttachmentList.ItemsSource = _attachments;
        _attachments.CollectionChanged += (_, _) =>
            AttachmentList.Visibility = _attachments.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        AppState.SettingsChanged += ApplyLayout;
        AppState.LogOutRequested += LogOutAllAsync;
        Loaded += (_, _) => PromptBox.Focus();

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
            var answerTasks = await Task.WhenAll(panes.Select(p => p.SendPromptAsync(prompt, capture, files)));
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
