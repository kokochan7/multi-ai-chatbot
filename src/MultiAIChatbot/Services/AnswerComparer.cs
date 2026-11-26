using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace MultiAIChatbot.Services;

/// <summary>One thing the AIs recommended (a product, place, tool…), with which AIs mentioned it.</summary>
public sealed class AgreedItem
{
    public required string Name { get; init; }
    /// <summary>Site key → the line where that AI mentioned it, in the AIs' order.</summary>
    public required IReadOnlyList<(string Site, string Line)> Mentions { get; init; }

    public int Count => Mentions.Count;
    public bool Has(string site) => Mentions.Any(m => m.Site == site);
}

/// <summary>
/// Finds what the AIs agree on: picks the named items out of each answer (list items, bold names,
/// numbered headings, first table column), matches the same item across answers even when
/// it's worded a little differently, and sorts by how many AIs mentioned it.
/// Works on this PC with the saved answers; nothing is sent anywhere.
/// </summary>
public static class AnswerComparer
{
    private static readonly HashSet<string> StopWords = new(StringComparer.Ordinal)
        { "the", "a", "an", "by", "for", "and", "with", "of", "to", "in", "on", "or" };

    // Labels that aren't items in their own right ("**Tip:** …", "- Pros: …").
    private static readonly HashSet<string> Generic = new(StringComparer.OrdinalIgnoreCase)
    {
        "pros", "cons", "why", "note", "notes", "price", "summary", "bottom line", "best for", "tip", "tips",
        "verdict", "overview", "key features", "features", "ingredients", "how to use", "skin type",
        "conclusion", "recommendation", "recommendations", "options", "budget", "why it works", "good for",
        "important", "caveat", "caveats", "example", "examples", "step", "steps", "answer", "short answer",
        "tl;dr", "tldr", "result", "results", "warning", "cost", "where to buy", "availability", "my pick",
    };

    private static readonly Regex ListItem = new(@"^([-*+]|\d+[.)])\s+(.*)$", RegexOptions.Compiled);
    private static readonly Regex Heading = new(@"^#{1,6}\s+(.*)$", RegexOptions.Compiled);
    private static readonly Regex Bold = new(@"\*\*(.+?)\*\*", RegexOptions.Compiled);
    private static readonly Regex LeadingBold = new(@"^\s*\*\*(.+?)\*\*", RegexOptions.Compiled);
    private static readonly Regex Link = new(@"\[([^\]]+)\]\([^)]+\)", RegexOptions.Compiled);
    private static readonly Regex NameEnd = new(@"\s+[–—-]\s+|:\s|\s\(|\.\s", RegexOptions.Compiled);
    // "Best premium option: X" -> X (only when the part before the colon is a short label).
    private static readonly Regex LabelPrefix = new(@"^(?<label>[^:]{2,40}):\s+(?<rest>.+)$", RegexOptions.Compiled);
    // "My top recommendation is the X", "I'd recommend the X", "Go with X" -> X
    private static readonly Regex IntroPrefix = new(
        @"^(?:(?:my|our|the)\s+)?(?:(?:top|best|overall|first|main|personal|clear|budget|premium|runner[- ]up)\s+)*" +
        @"(?:pick|recommendation|choice|option|bet|suggestion|winner)s?\s+(?:is|are|would be|has to be)\s+(?:the\s+|an?\s+)?" +
        @"|^(?:i(?:'d|’d| would)?\s+(?:recommend|suggest|pick|choose)|go with|consider|try|get)\s+(?:the\s+|an?\s+)?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    // "X or Y", "X vs Y" name two items.
    private static readonly Regex Alternatives = new(@"\s+(?:or|vs\.?|versus)\s+", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex TableRule = new(@"^\|[\s:|-]+\|$", RegexOptions.Compiled);

    private sealed record Candidate(string Name, string[] Tokens, string Line);

    private sealed class Cluster
    {
        public readonly List<(string Site, string Line)> Mentions = new();
        public readonly List<string[]> Tokens = new();
        public readonly List<string> Names = new();
        public readonly List<int> Positions = new();
    }

    /// <param name="answers">Site key → that AI's answer (Markdown), in display order.</param>
    public static List<AgreedItem> Compare(IEnumerable<(string Site, string Answer)> answers)
    {
        var clusters = new List<Cluster>();
        foreach (var (site, answer) in answers)
        {
            var position = 0;
            foreach (var c in Candidates(answer))
            {
                Cluster? best = null;
                double bestScore = 0;
                foreach (var cluster in clusters)
                {
                    if (cluster.Mentions.Any(m => m.Site == site)) continue;
                    var score = cluster.Tokens.Max(t => Similarity(t, c.Tokens));
                    if (score > bestScore) { bestScore = score; best = cluster; }
                }
                if (best == null || bestScore < 0.6)
                {
                    best = new Cluster();
                    clusters.Add(best);
                }
                best.Mentions.Add((site, c.Line));
                best.Tokens.Add(c.Tokens);
                best.Names.Add(c.Name);
                best.Positions.Add(position++);
            }
        }

        return clusters
            .OrderByDescending(c => c.Mentions.Count)
            .ThenBy(c => c.Positions.Average())
            .Select(c => new AgreedItem
            {
                // The cleanest name: the shortest one that still has at least two words
                // (so "ASUS ROG Swift PG27UCDM" wins over a sentence that mentions it).
                Name = c.Names.Where(n => Tokens(n).Length >= 2).OrderBy(n => n.Length).FirstOrDefault()
                       ?? c.Names.OrderBy(n => n.Length).First(),
                Mentions = c.Mentions,
            })
            .ToList();
    }

    private static IEnumerable<Candidate> Candidates(string? markdown)
    {
        var text = MarkdownRenderer.Clean(markdown);
        var found = new List<(string Name, string Line)>();
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;

            var li = ListItem.Match(line);
            var h = Heading.Match(line);
            if (li.Success)
                found.Add((NameOf(li.Groups[2].Value), line));
            else if (h.Success && (Regex.IsMatch(h.Groups[1].Value, @"^\d+[.)]") || h.Groups[1].Value.Contains("**")))
                found.Add((NameOf(h.Groups[1].Value), line));
            else if (line.StartsWith('|') && !TableRule.IsMatch(line))
                found.Add((NameOf(line.Trim('|').Split('|')[0]), line));
            else
                foreach (Match b in Bold.Matches(line))
                    found.Add((NameOf("**" + b.Groups[1].Value + "**"), line));
        }

        var seen = new HashSet<string>();
        foreach (var (name, line) in found.SelectMany(f => Alternatives.Split(f.Name).Select(n => (Name: Tidy(n), f.Line))))
        {
            var tokens = Tokens(name);
            if (tokens.Length == 0 || tokens.Length > 10 || name.Length > 80 || name.EndsWith('?')) continue;
            if (Generic.Contains(name) || Generic.Contains(string.Join(' ', tokens))) continue;
            if (!seen.Add(string.Join(' ', tokens))) continue;
            yield return new Candidate(name, tokens, Readable(line));
        }
    }

    /// <summary>The item's name: its leading bold text, or the words before a dash, colon or bracket.</summary>
    private static string NameOf(string item)
    {
        var lead = LeadingBold.Match(item);
        var name = lead.Success ? lead.Groups[1].Value : NameEnd.Split(item.Replace("**", "").Replace("__", ""), 2)[0];
        name = Link.Replace(name, "$1").Replace("`", "").Replace("*", "");
        name = Regex.Replace(name, @"^\d+[.)]\s*", "");
        name = name.Trim(' ', '.', ':', ';', ',', '-', '–', '—');

        var label = LabelPrefix.Match(name);
        if (label.Success && label.Groups["label"].Value.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length <= 4)
            name = label.Groups["rest"].Value;
        name = IntroPrefix.Replace(name, "");
        return Tidy(name);
    }

    private static string Tidy(string name) => name.Trim(' ', '.', ':', ';', ',', '-', '–', '—', '!');

    private static string[] Tokens(string name)
    {
        var s = name.ToLowerInvariant().Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder();
        foreach (var ch in s)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.NonSpacingMark) continue;
            if (ch is '®' or '™' or '\'' or '’') continue;
            sb.Append(char.IsLetterOrDigit(ch) ? ch : ' ');
        }
        return sb.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(t => !StopWords.Contains(t)).ToArray();
    }

    /// <summary>1 for the same name; 0.9 when one name is the other plus extra words; otherwise word overlap.</summary>
    private static double Similarity(string[] a, string[] b)
    {
        var setA = new HashSet<string>(a);
        var setB = new HashSet<string>(b);
        if (setA.SetEquals(setB)) return 1;
        var (small, big) = setA.Count <= setB.Count ? (setA, setB) : (setB, setA);
        if (small.Count >= 2 && small.IsSubsetOf(big)) return 0.9;
        var shared = setA.Intersect(setB).Count();
        return (double)shared / setA.Union(setB).Count();
    }

    /// <summary>The source line as plain text, for showing what each AI said about the item.</summary>
    private static string Readable(string line)
    {
        var t = ListItem.Replace(line, "$2");
        t = Regex.Replace(t, @"^#{1,6}\s+", "");
        t = Link.Replace(t, "$1").Replace("**", "").Replace("`", "").Trim('|', ' ');
        t = Regex.Replace(t, @"\s*\|\s*", " · ");
        return t.Length > 220 ? t[..217].TrimEnd() + "…" : t;
    }
}
