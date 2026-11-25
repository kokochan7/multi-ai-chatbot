using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using MultiAIChatbot.Services;

namespace MultiAIChatbot.Views;

/// <summary>
/// Shows what ChatGPT, Claude and Gemini agree on for one question: the items all three
/// recommended, then those two recommended, then (on request) those only one mentioned.
/// </summary>
public partial class CompareView : UserControl
{
    public sealed record Pill(string Name, Brush Fill, Brush Stroke, Brush TextBrush, string Tip);
    public sealed record Line(string Site, Brush Color, string Text);
    public sealed record Row(string Name, IReadOnlyList<Pill> Pills, IReadOnlyList<Line> Lines);

    private HistoryEntry? _entry;
    private bool _showOnes;

    public CompareView()
    {
        InitializeComponent();
        Theme.Changed += () => { if (_entry != null) Render(); };
    }

    /// <summary>The question being compared (null = none yet).</summary>
    public HistoryEntry? Current => _entry;

    /// <summary>Compares the newest question that has at least two answers.</summary>
    public void ShowLatest()
    {
        var latest = AppState.History.FirstOrDefault(e => AnswersOf(e).Count >= 2) ?? AppState.History.FirstOrDefault();
        Show(latest);
    }

    public void Show(HistoryEntry? entry)
    {
        if (_entry != null) _entry.PropertyChanged -= Entry_PropertyChanged;
        _entry = entry;
        if (_entry != null) _entry.PropertyChanged += Entry_PropertyChanged;
        _showOnes = false;
        Render();
    }

    // Answers that arrive while this page is open update it straight away.
    private void Entry_PropertyChanged(object? sender, PropertyChangedEventArgs e) => Dispatcher.InvokeAsync(Render);

    private static List<(string Site, string Answer)> AnswersOf(HistoryEntry entry) =>
        AppState.Settings.ColumnOrder
            .Select(k => (Site: k, Answer: entry.AnswerFor(k)))
            .Where(a => !string.IsNullOrWhiteSpace(a.Answer))
            .ToList();

    private void Render()
    {
        if (_entry == null)
        {
            ResultPanel.Visibility = Visibility.Collapsed;
            EmptyPanel.Visibility = Visibility.Visible;
            return;
        }
        EmptyPanel.Visibility = Visibility.Collapsed;
        ResultPanel.Visibility = Visibility.Visible;
        QuestionText.Text = _entry.Prompt;

        var answers = AnswersOf(_entry);
        var sites = answers.Select(a => a.Site).ToList();
        var items = answers.Count >= 2 ? AnswerComparer.Compare(answers) : new List<AgreedItem>();

        var all = items.Where(i => i.Count == answers.Count && answers.Count >= 2).ToList();
        var some = items.Where(i => i.Count >= 2 && i.Count < answers.Count).ToList();
        var ones = items.Where(i => i.Count == 1).ToList();

        var names = string.Join(", ", sites.Select(AiSites.DisplayName));
        SummaryText.Text = answers.Count switch
        {
            0 => "No answers were saved for this question yet. Answers are saved when each AI finishes replying.",
            1 => $"Only {names} has an answer saved so far. Comparing needs at least two.",
            _ => $"Compared the answers from {names}." +
                 (answers.Count < 3 ? " The other AI has no saved answer for this question." : ""),
        };

        AllHeader.Text = answers.Count == 3 ? $"ALL 3 AGREE ({all.Count})" : $"BOTH AGREE ({all.Count})";
        AllHeader.Visibility = answers.Count >= 2 ? Visibility.Visible : Visibility.Collapsed;
        AllList.ItemsSource = all.Select(i => ToRow(i, sites)).ToList();

        SomeHeader.Text = $"2 OF 3 AGREE ({some.Count})";
        SomeHeader.Visibility = answers.Count == 3 && some.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        SomeList.ItemsSource = some.Select(i => ToRow(i, sites)).ToList();

        var anyAgreement = all.Count + some.Count > 0;
        NoMatchText.Visibility = answers.Count >= 2 && !anyAgreement ? Visibility.Visible : Visibility.Collapsed;
        NoMatchText.Text = items.Count == 0
            ? "No list of items was found in the answers. This works best for questions that ask for recommendations or options, like products, places or tools."
            : "The AIs didn't recommend any of the same items.";

        OneToggle.Visibility = ones.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        OneToggle.Content = (_showOnes ? "Hide" : "Show") + $" items only one AI mentioned ({ones.Count})";
        OneList.ItemsSource = ones.Select(i => ToRow(i, sites)).ToList();
        OneList.Visibility = _showOnes ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OneToggle_Click(object sender, RoutedEventArgs e)
    {
        _showOnes = !_showOnes;
        Render();
    }

    private Row ToRow(AgreedItem item, List<string> sites)
    {
        var muted = (Brush)FindResource("TextMuted");
        var divider = (Brush)FindResource("Divider");
        var pills = sites.Select(site =>
        {
            var color = BrushOf(AiSites.Color(site));
            var name = AiSites.DisplayName(site);
            return item.Has(site)
                ? new Pill(name, color, color, Brushes.White, $"{name} recommended this")
                : new Pill(name, Brushes.Transparent, divider, muted, $"{name} didn't mention this");
        }).ToList();
        var lines = item.Mentions
            .Select(m => new Line(AiSites.DisplayName(m.Site), BrushOf(AiSites.Color(m.Site)), m.Line))
            .ToList();
        return new Row(item.Name, pills, lines);
    }

    private static Brush BrushOf(string hex)
    {
        var brush = (Brush)new BrushConverter().ConvertFromString(hex)!;
        brush.Freeze();
        return brush;
    }
}
