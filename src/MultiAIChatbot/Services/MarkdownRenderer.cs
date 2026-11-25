using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace MultiAIChatbot.Services;

/// <summary>
/// Shows a saved answer (Markdown) as formatted text: headings, bold and italic, lists,
/// links, code, quotes and tables. Also tidies answers saved by older versions, which
/// kept bits of the sites' screens ("Claude responded:", "Searched the web", icon boxes).
/// </summary>
public static class MarkdownRenderer
{
    /// <summary>Attached property: set it on a RichTextBox to show that Markdown in it.</summary>
    public static readonly DependencyProperty MarkdownProperty = DependencyProperty.RegisterAttached(
        "Markdown", typeof(string), typeof(MarkdownRenderer), new PropertyMetadata(null, OnMarkdownChanged));

    public static string? GetMarkdown(DependencyObject d) => (string?)d.GetValue(MarkdownProperty);
    public static void SetMarkdown(DependencyObject d, string? value) => d.SetValue(MarkdownProperty, value);

    private static void OnMarkdownChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is RichTextBox box) box.Document = Render((string?)e.NewValue, box);
    }

    /// <summary>The answer as plain readable text, for the Copy button.</summary>
    public static string ToPlainText(string? markdown)
    {
        var doc = Render(markdown, null);
        return new TextRange(doc.ContentStart, doc.ContentEnd).Text.Trim();
    }

    // ---- Cleaning ----

    private static readonly Regex Glyphs = new("[-□�]", RegexOptions.Compiled);

    /// <summary>Removes screen clutter that older versions saved along with the answer.</summary>
    public static string Clean(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        var lines = text.Replace("\r\n", "\n").Split('\n').ToList();
        var junk = new bool[lines.Count];
        for (var i = 0; i < lines.Count; i++)
        {
            var raw = lines[i];
            var t = Glyphs.Replace(raw, "").Trim();
            var glyphOnly = raw.Trim().Length > 0 && t.Length == 0;
            if (glyphOnly || t.StartsWith("Claude responded:", StringComparison.Ordinal) ||
                t is "Searched the web" or "Thinking" or "Show thinking" or "Copy code" or "Copy")
            {
                junk[i] = true;
                // Source chips sit as one-word lines right next to an icon box ("goat", "nike").
                if (glyphOnly)
                {
                    if (i > 0 && IsChipWord(lines[i - 1])) junk[i - 1] = true;
                    if (i + 1 < lines.Count && IsChipWord(lines[i + 1])) junk[i + 1] = true;
                }
            }
            lines[i] = Glyphs.Replace(raw, "").TrimEnd();
        }
        var kept = lines.Where((_, i) => !junk[i]);
        return Regex.Replace(string.Join("\n", kept), "\n{3,}", "\n\n").Trim();
    }

    private static bool IsChipWord(string line)
    {
        var t = Glyphs.Replace(line, "").Trim();
        return t.Length is > 0 and <= 24 && !t.Contains(' ') && !Regex.IsMatch(t, "[.!?:,;]$");
    }

    // ---- Rendering ----

    private static readonly FontFamily CodeFont = new("Cascadia Mono, Consolas, Courier New");
    private static readonly Regex ListItem = new(@"^(\s*)([-*+]|\d+[.)])\s+(.*)$", RegexOptions.Compiled);
    private static readonly Regex Heading = new(@"^(#{1,6})\s+(.*)$", RegexOptions.Compiled);
    private static readonly Regex InlinePattern = new(
        @"(?<code>`[^`]+`)" +
        @"|(?<bold>\*\*(?<bt>.+?)\*\*|__(?<bt2>.+?)__)" +
        @"|(?<link>\[(?<lt>[^\]]+)\]\((?<lu>https?://[^)\s]+)\))" +
        @"|(?<italic>(?<![\w*])\*(?!\s)(?<it>[^*]+?)(?<!\s)\*(?![\w*])|(?<![\w_])_(?!\s)(?<it2>[^_]+?)(?<!\s)_(?![\w_]))" +
        @"|(?<url>https?://[^\s)\]]+[^\s)\].,;:!?])",
        RegexOptions.Compiled);

    public static FlowDocument Render(string? markdown, Control? host)
    {
        var doc = new FlowDocument { PagePadding = new Thickness(0) };
        if (host != null)
        {
            doc.FontFamily = host.FontFamily;
            doc.FontSize = host.FontSize;
        }
        doc.LineHeight = doc.FontSize * 1.45;   // a bit of air between lines; taller lines still fit
        doc.SetResourceReference(TextElement.ForegroundProperty, "TextPrimary");

        var text = Clean(markdown);
        if (text.Length == 0)
        {
            var empty = new Paragraph(new Run("No answer was saved for this question.")) { FontStyle = FontStyles.Italic };
            empty.SetResourceReference(TextElement.ForegroundProperty, "TextMuted");
            doc.Blocks.Add(empty);
            return doc;
        }

        var lines = text.Split('\n');
        var para = new StringBuilder();

        void FlushParagraph()
        {
            if (para.Length == 0) return;
            var p = NewParagraph();
            AddInlines(p.Inlines, para.ToString().TrimEnd('\n'));
            doc.Blocks.Add(p);
            para.Clear();
        }

        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            var trimmed = line.Trim();

            if (trimmed.Length == 0) { FlushParagraph(); continue; }

            // ```code```
            if (trimmed.StartsWith("```"))
            {
                FlushParagraph();
                var code = new StringBuilder();
                for (i++; i < lines.Length && !lines[i].Trim().StartsWith("```"); i++) code.AppendLine(lines[i]);
                var p = new Paragraph(new Run(code.ToString().TrimEnd('\r', '\n')))
                {
                    FontFamily = CodeFont,
                    FontSize = Math.Max(11, doc.FontSize - 1.5),
                    Padding = new Thickness(10, 8, 10, 8),
                    Margin = new Thickness(0, 2, 0, 10),
                };
                p.SetResourceReference(TextElement.BackgroundProperty, "NavHover");
                doc.Blocks.Add(p);
                continue;
            }

            // # Heading
            var h = Heading.Match(trimmed);
            if (h.Success)
            {
                FlushParagraph();
                var level = h.Groups[1].Value.Length;
                var p = NewParagraph();
                p.FontWeight = FontWeights.SemiBold;
                p.FontSize = doc.FontSize + (level switch { 1 => 5, 2 => 3, 3 => 1.5, _ => 0.5 });
                p.Margin = new Thickness(0, doc.Blocks.Count == 0 ? 0 : 6, 0, 6);
                AddInlines(p.Inlines, h.Groups[2].Value.TrimEnd('#', ' '));
                doc.Blocks.Add(p);
                continue;
            }

            // ---
            if (Regex.IsMatch(trimmed, @"^(-{3,}|\*{3,}|_{3,})$"))
            {
                FlushParagraph();
                var p = new Paragraph
                {
                    Margin = new Thickness(0, 4, 0, 12), FontSize = 1, LineHeight = 1,
                    LineStackingStrategy = LineStackingStrategy.BlockLineHeight, BorderThickness = new Thickness(0, 0, 0, 1),
                };
                p.SetResourceReference(Block.BorderBrushProperty, "Divider");
                doc.Blocks.Add(p);
                continue;
            }

            // | table |
            if (trimmed.StartsWith('|') && trimmed.EndsWith('|'))
            {
                FlushParagraph();
                var rows = new List<string>();
                for (; i < lines.Length && lines[i].Trim().StartsWith('|'); i++) rows.Add(lines[i].Trim());
                i--;
                doc.Blocks.Add(BuildTable(rows));
                continue;
            }

            // > quote
            if (trimmed.StartsWith('>'))
            {
                FlushParagraph();
                var quote = new StringBuilder();
                for (; i < lines.Length && lines[i].Trim().StartsWith('>'); i++)
                    quote.AppendLine(lines[i].Trim().TrimStart('>').TrimStart());
                i--;
                var p = NewParagraph();
                p.Padding = new Thickness(10, 0, 0, 0);
                p.BorderThickness = new Thickness(3, 0, 0, 0);
                p.SetResourceReference(Block.BorderBrushProperty, "Divider");
                p.SetResourceReference(TextElement.ForegroundProperty, "TextMuted");
                AddInlines(p.Inlines, quote.ToString().TrimEnd());
                doc.Blocks.Add(p);
                continue;
            }

            // - item / 1. item
            var li = ListItem.Match(line);
            if (li.Success)
            {
                FlushParagraph();
                var depth = li.Groups[1].Value.Replace("\t", "  ").Length / 2;
                var marker = li.Groups[2].Value;
                var bullet = char.IsDigit(marker[0]) ? marker.TrimEnd(')').TrimEnd('.') + "." : depth == 0 ? "•" : "◦";
                var item = new StringBuilder(li.Groups[3].Value);
                // Lines indented under the item continue it.
                while (i + 1 < lines.Length && lines[i + 1].Length > 0 && char.IsWhiteSpace(lines[i + 1][0]) &&
                       !ListItem.IsMatch(lines[i + 1]))
                    item.Append('\n').Append(lines[++i].Trim());

                var indent = 18 + depth * 18;
                var p = NewParagraph();
                p.Margin = new Thickness(indent, 0, 0, 4);
                p.TextIndent = -16;
                var mark = new Run(bullet + " ");
                mark.SetResourceReference(TextElement.ForegroundProperty, "TextMuted");
                p.Inlines.Add(mark);
                AddInlines(p.Inlines, item.ToString());
                doc.Blocks.Add(p);
                // A little space after the whole list.
                if (i + 1 >= lines.Length || lines[i + 1].Trim().Length == 0) p.Margin = new Thickness(indent, 0, 0, 10);
                continue;
            }

            para.Append(trimmed).Append('\n');
        }
        FlushParagraph();
        return doc;
    }

    private static Paragraph NewParagraph() => new() { Margin = new Thickness(0, 0, 0, 10) };

    private static Table BuildTable(List<string> rows)
    {
        var table = new Table { CellSpacing = 0, Margin = new Thickness(0, 2, 0, 12) };
        var group = new TableRowGroup();
        table.RowGroups.Add(group);
        var first = true;
        foreach (var row in rows)
        {
            var cells = Regex.Split(row.Trim().Trim('|'), @"(?<!\\)\|").Select(c => c.Trim().Replace("\\|", "|")).ToList();
            if (cells.All(c => Regex.IsMatch(c, @"^:?-{2,}:?$"))) continue;   // the |---| line
            while (table.Columns.Count < cells.Count) table.Columns.Add(new TableColumn());
            var tr = new TableRow();
            foreach (var cell in cells)
            {
                var p = new Paragraph { Margin = new Thickness(0) };
                if (first) p.FontWeight = FontWeights.SemiBold;
                AddInlines(p.Inlines, cell);
                var tc = new TableCell(p) { Padding = new Thickness(8, 5, 8, 5), BorderThickness = new Thickness(0, 0, 0, 1) };
                tc.SetResourceReference(TableCell.BorderBrushProperty, "Divider");
                tr.Cells.Add(tc);
            }
            group.Rows.Add(tr);
            first = false;
        }
        return table;
    }

    /// <summary>Adds text with **bold**, *italic*, `code`, [links](https://…) and bare links.</summary>
    private static void AddInlines(InlineCollection target, string text)
    {
        var pos = 0;
        foreach (Match m in InlinePattern.Matches(text))
        {
            if (m.Index > pos) AddText(target, text[pos..m.Index]);
            pos = m.Index + m.Length;

            if (m.Groups["code"].Success)
            {
                var run = new Run(m.Value.Trim('`')) { FontFamily = CodeFont };
                run.SetResourceReference(TextElement.BackgroundProperty, "NavHover");
                run.SetResourceReference(TextElement.ForegroundProperty, "AccentText");
                target.Add(run);
            }
            else if (m.Groups["bold"].Success)
            {
                var bold = new Bold();
                AddInlines(bold.Inlines, m.Groups["bt"].Success ? m.Groups["bt"].Value : m.Groups["bt2"].Value);
                target.Add(bold);
            }
            else if (m.Groups["link"].Success)
            {
                target.Add(MakeLink(m.Groups["lt"].Value, m.Groups["lu"].Value));
            }
            else if (m.Groups["italic"].Success)
            {
                var italic = new Italic();
                AddInlines(italic.Inlines, m.Groups["it"].Success ? m.Groups["it"].Value : m.Groups["it2"].Value);
                target.Add(italic);
            }
            else if (m.Groups["url"].Success)
            {
                target.Add(MakeLink(m.Value, m.Value));
            }
        }
        if (pos < text.Length) AddText(target, text[pos..]);
    }

    private static void AddText(InlineCollection target, string text)
    {
        var parts = text.Split('\n');
        for (var i = 0; i < parts.Length; i++)
        {
            if (i > 0) target.Add(new LineBreak());
            if (parts[i].Length > 0) target.Add(new Run(parts[i]));
        }
    }

    private static Inline MakeLink(string text, string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return new Run(text);
        var link = new Hyperlink(new Run(text)) { NavigateUri = uri, ToolTip = url, Cursor = System.Windows.Input.Cursors.Hand };
        link.SetResourceReference(TextElement.ForegroundProperty, "AccentText");
        link.RequestNavigate += (_, e) =>
        {
            try { Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true }); } catch { }
            e.Handled = true;
        };
        return link;
    }
}
