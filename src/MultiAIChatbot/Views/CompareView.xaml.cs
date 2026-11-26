using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using MultiAIChatbot.Services;

namespace MultiAIChatbot.Views;

/// <summary>
/// Shows what ChatGPT, Claude and Gemini agree on for one question: the items all three
/// recommended, then those two of the three recommended, each with a picture. Items only one
/// AI mentioned are left out.
/// </summary>
public partial class CompareView : UserControl
{
    public sealed record Pill(string Name, Brush Color, double Opacity, TextDecorationCollection? Decorations, string Tip);
    public sealed record Line(Brush Color, string Text, string Tip);
    /// <summary>A question of the chat, shown as a tab when the chat has more than one.</summary>
    public sealed record QuestionTab(string Text, string Tip, HistoryEntry Entry, bool IsCurrent);

    /// <summary>One card on the page. Its picture arrives later, once it has been looked up.</summary>
    public sealed class Row : INotifyPropertyChanged
    {
        private ImageSource? _image;

        public required string Name { get; init; }
        public required string Rank { get; init; }
        public required string Initial { get; init; }
        public required Brush PlaceholderBrush { get; init; }
        public required string BadgeText { get; init; }
        public required Brush BadgeFill { get; init; }
        public required Brush BadgeForeground { get; init; }
        public required IReadOnlyList<Pill> Pills { get; init; }
        public required IReadOnlyList<Line> Lines { get; init; }

        public ImageSource? Image
        {
            get => _image;
            set { _image = value; Changed(); Changed(nameof(HasImage)); }
        }
        public bool HasImage => _image != null;

        public event PropertyChangedEventHandler? PropertyChanged;
        private void Changed([CallerMemberName] string? name = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    private static readonly Brush AllFill = Frozen("#2622A55B"), AllText = Frozen("#22A55B");
    private static readonly Brush SomeFill = Frozen("#26E0A030"), SomeText = Frozen("#D08A12");

    private HistoryEntry? _entry;

    public CompareView()
    {
        InitializeComponent();
        Theme.Changed += () => { if (_entry != null) Render(); };
    }

    /// <summary>The question being compared (null = none yet).</summary>
    public HistoryEntry? Current => _entry;

    /// <summary>Raised when a different question is shown, so Recent can highlight its chat.</summary>
    public event Action<HistoryEntry?>? CurrentChanged;

    /// <summary>Compares a chat: its newest question that has at least two answers.</summary>
    public void ShowChat(Conversation chat)
    {
        var entries = chat.Entries.Reverse().ToList();
        Show(entries.FirstOrDefault(e => AnswersOf(e).Count >= 2) ?? entries.FirstOrDefault());
    }

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
        Render();
        CurrentChanged?.Invoke(_entry);
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

        // A chat with follow-up questions gets a tab per question.
        var chat = AppState.ChatOf(_entry);
        var tabs = chat == null || chat.Entries.Count < 2
            ? new List<QuestionTab>()
            : chat.Entries.Select((e, i) => new QuestionTab(
                $"Q{i + 1}  " + (e.Prompt.Length > 40 ? e.Prompt[..37].TrimEnd() + "…" : e.Prompt),
                e.Prompt, e, e == _entry)).ToList();
        QuestionTabs.ItemsSource = tabs;
        QuestionTabs.Visibility = tabs.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

        var answers = AnswersOf(_entry);
        var sites = answers.Select(a => a.Site).ToList();
        var items = answers.Count >= 2 ? AnswerComparer.Compare(answers) : new List<AgreedItem>();

        // Only what at least two AIs agree on; things a single AI mentioned are left out.
        var all = items.Where(i => i.Count == answers.Count && answers.Count >= 2).ToList();
        var some = items.Where(i => i.Count >= 2 && i.Count < answers.Count).ToList();

        var names = string.Join(", ", sites.Select(AiSites.DisplayName));
        SummaryText.Text = answers.Count switch
        {
            0 => "No answers were saved for this question yet. Answers are saved when each AI finishes replying.",
            1 => $"Only {names} has an answer saved so far. Comparing needs at least two.",
            _ => $"Compared the answers from {names}." +
                 (answers.Count < 3 ? " The other AI has no saved answer for this question." : ""),
        };

        var three = answers.Count == 3;
        StatsPanel.Visibility = answers.Count >= 2 ? Visibility.Visible : Visibility.Collapsed;
        AllCount.Text = all.Count.ToString();
        AllCaption.Text = three ? "items all 3 agree on" : "items both agree on";
        SomeCount.Text = some.Count.ToString();
        SomeTile.Visibility = three ? Visibility.Visible : Visibility.Collapsed;

        AllHeader.Text = three ? "ALL 3 AGREE" : "BOTH AGREE";
        AllHeaderPanel.Visibility = all.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        AllList.ItemsSource = all.Select((item, i) =>
            ToRow(item, i, sites, three ? "3 of 3" : "2 of 2", AllFill, AllText)).ToList();

        SomeHeader.Text = "2 OF 3 AGREE";
        SomeHeaderPanel.Visibility = three && some.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        SomeList.ItemsSource = some.Select((item, i) =>
            ToRow(item, i, sites, "2 of 3", SomeFill, SomeText)).ToList();

        var anyAgreement = all.Count + some.Count > 0;
        NoMatchText.Visibility = answers.Count >= 2 && !anyAgreement ? Visibility.Visible : Visibility.Collapsed;
        NoMatchText.Text = items.Count == 0
            ? "No list of items was found in the answers. This works best for questions that ask for recommendations or options, like products, places or tools."
            : "The AIs didn't recommend any of the same items.";
        ImageNote.Visibility = anyAgreement ? Visibility.Visible : Visibility.Collapsed;
    }

    private Row ToRow(AgreedItem item, int index, List<string> sites, string badge, Brush badgeFill, Brush badgeText)
    {
        var pills = sites.Select(site =>
        {
            var name = AiSites.DisplayName(site);
            return item.Has(site)
                ? new Pill(name, BrushOf(AiSites.Color(site)), 1, null, $"{name} recommended this")
                : new Pill(name, BrushOf(AiSites.Color(site)), 0.4, TextDecorations.Strikethrough, $"{name} didn't mention this");
        }).ToList();
        var lines = item.Mentions
            .Select(m => new Line(BrushOf(AiSites.Color(m.Site)), m.Line, $"{AiSites.DisplayName(m.Site)}: {m.Line}"))
            .ToList();

        var row = new Row
        {
            Name = item.Name,
            Rank = $"{index + 1}.",
            Initial = item.Name.FirstOrDefault(char.IsLetterOrDigit).ToString().ToUpperInvariant().Trim('\0'),
            PlaceholderBrush = Placeholder(item.Name),
            BadgeText = badge,
            BadgeFill = badgeFill,
            BadgeForeground = badgeText,
            Pills = pills,
            Lines = lines,
        };

        var image = ProductImages.GetAsync(item.Name);
        if (image.IsCompletedSuccessfully) row.Image = image.Result;
        else image.ContinueWith(t => { if (t.IsCompletedSuccessfully && t.Result != null) row.Image = t.Result; },
                                TaskScheduler.FromCurrentSynchronizationContext());
        return row;
    }

    private void QuestionTab_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is QuestionTab { IsCurrent: false } tab) Show(tab.Entry);
    }

    private void Search_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string name }) return;
        try
        {
            Process.Start(new ProcessStartInfo("https://www.bing.com/search?q=" + Uri.EscapeDataString(name))
                { UseShellExecute = true });
        }
        catch { /* no default browser */ }
    }

    // A soft two-tone gradient that's always the same for the same name, so placeholders don't flicker.
    private static readonly (string, string)[] Gradients =
    {
        ("#6D5DF5", "#9B8CFF"), ("#2F80ED", "#56CCF2"), ("#11998E", "#38EF7D"),
        ("#F2994A", "#F2C94C"), ("#EB5757", "#F78CA0"), ("#8E54E9", "#4776E6"),
    };

    private static Brush Placeholder(string name)
    {
        var hash = 0;
        foreach (var ch in name.ToLowerInvariant()) hash = hash * 31 + ch;
        var (a, b) = Gradients[(hash & 0x7fffffff) % Gradients.Length];
        var brush = new LinearGradientBrush(
            (Color)ColorConverter.ConvertFromString(a), (Color)ColorConverter.ConvertFromString(b), 45);
        brush.Freeze();
        return brush;
    }

    private static Brush BrushOf(string hex) => Frozen(hex);

    private static Brush Frozen(string hex)
    {
        var brush = (Brush)new BrushConverter().ConvertFromString(hex)!;
        brush.Freeze();
        return brush;
    }
}
