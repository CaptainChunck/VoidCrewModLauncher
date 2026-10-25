using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using VoidCrewModLauncher.Core;

namespace VoidCrewModLauncher.Infrastructure;

/// <summary>
/// Enough Markdown for Thunderstore READMEs: headings, lists, quotes, code blocks, bold, italics, inline code and links.
/// Images show their alt text, HTML tags are dropped, tables become plain lines. READMEs come from anyone, so the text
/// and the number of pieces it is drawn with are capped and every pattern has a time limit; a README that runs into a
/// limit shows as plain text.
/// </summary>
public static partial class Markdown
{
    // Frozen: shared by every README shown, and an unfrozen brush would keep each element that used it alive.
    private static readonly Brush Text = Frozen(new SolidColorBrush(Color.FromRgb(0xC8, 0xD7, 0xE6)));
    private static readonly Brush Heading = Brushes.White;
    private static readonly Brush Accent = Frozen(new SolidColorBrush(Color.FromRgb(0xF6, 0xAE, 0x3C)));
    private static readonly Brush Link = Frozen(new SolidColorBrush(Color.FromRgb(0xBD, 0xE7, 0xFB)));
    private static readonly Brush CodeBack = Frozen(new SolidColorBrush(Color.FromArgb(0x60, 0, 0, 0)));
    private static readonly FontFamily BodyFont = new("Segoe UI");
    private static readonly FontFamily CodeFont = new("Consolas");

    /// <summary>The most of a README that is shown, and of each of its lines.</summary>
    private const int MaxLength = 256 * 1024;
    private const int MaxLineLength = 4096;

    /// <summary>
    /// The most lines of a README that are shown: the window lays out every line, and tens of thousands of short ones
    /// took it seconds even as plain text. Real READMEs have a few hundred.
    /// </summary>
    private const int MaxLines = 2000;

    /// <summary>
    /// The most blocks and formatted pieces (bold, links, code, list items...) a README is drawn with. Each one costs the
    /// window time to lay out, and lines run together into one paragraph, so a README of thousands of bold words would
    /// freeze it (one at the limit takes about half a second). Real READMEs use a few hundred.
    /// </summary>
    private const int MaxElements = 4000;

    /// <summary>The time limit of each pattern match.</summary>
    private const int MatchTimeoutMs = 250;

    public static FlowDocument ToDocument(string? markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown))
        {
            FlowDocument empty = NewDocument();
            empty.Blocks.Add(new Paragraph(new Run(Loc.T("This mod has no README."))) { Foreground = Brushes.Gray });
            return empty;
        }
        if (markdown.Length > MaxLength) markdown = markdown[..MaxLength];
        markdown = FirstLines(markdown, MaxLines);
        try
        {
            return Render(markdown);
        }
        catch (Exception e) when (e is RegexMatchTimeoutException or TooManyPartsException)
        {
            AppLog.Warn(e is TooManyPartsException
                ? "A README has too many parts to draw; it shows as plain text."
                : "A README took too long to read; it shows as plain text.");
            FlowDocument plain = NewDocument();
            plain.Blocks.Add(new Paragraph(new Run(markdown.Replace("\r\n", "\n"))));
            return plain;
        }
    }

    private static Brush Frozen(Brush brush)
    {
        brush.Freeze();
        return brush;
    }

    /// <summary>The text up to its <paramref name="count"/>th line break (all of it when it has fewer lines).</summary>
    private static string FirstLines(string text, int count)
    {
        int end = -1;
        for (int line = 0; line < count; line++)
        {
            end = text.IndexOf('\n', end + 1);
            if (end < 0) return text;
        }
        return text[..end];
    }

    private static FlowDocument NewDocument() => new()
    {
        FontFamily = BodyFont,
        FontSize = 14,
        Foreground = Text,
        PagePadding = new Thickness(0),
        TextAlignment = TextAlignment.Left,
    };

    /// <summary>Thrown when a README needs more than <see cref="MaxElements"/> pieces.</summary>
    private sealed class TooManyPartsException : Exception;

    /// <summary>Counts the pieces a README is drawn with.</summary>
    private sealed class Parts
    {
        private int _left = MaxElements;

        public void Add(int count = 1)
        {
            _left -= count;
            if (_left < 0) throw new TooManyPartsException();
        }
    }

    private static FlowDocument Render(string markdown)
    {
        FlowDocument document = NewDocument();
        string[] lines = StripComments(markdown.Replace("\r\n", "\n")).Split('\n');
        var parts = new Parts();
        List? list = null;
        Paragraph? lastItem = null;
        var paragraph = new List<string>();
        var quote = new List<string>();
        bool inCode = false;
        var code = new List<string>();

        void FlushParagraph()
        {
            if (paragraph.Count == 0) return;
            parts.Add();
            var block = new Paragraph { Margin = new Thickness(0, 0, 0, 10), LineHeight = 20 };
            AddInlines(block.Inlines, string.Join(" ", paragraph), parts);
            document.Blocks.Add(block);
            paragraph.Clear();
        }

        void FlushQuote()
        {
            if (quote.Count == 0) return;
            parts.Add();
            var block = new Paragraph
            {
                BorderBrush = Accent,
                BorderThickness = new Thickness(3, 0, 0, 0),
                Padding = new Thickness(10, 2, 0, 2),
                Margin = new Thickness(0, 0, 0, 10),
            };
            AddInlines(block.Inlines, string.Join(" ", quote), parts);
            document.Blocks.Add(block);
            quote.Clear();
        }

        void FlushCode()
        {
            parts.Add();
            document.Blocks.Add(new Paragraph(new Run(string.Join("\n", code)))
            {
                FontFamily = CodeFont,
                FontSize = 12.5,
                Background = CodeBack,
                Padding = new Thickness(8),
                Margin = new Thickness(0, 0, 0, 10),
            });
            code.Clear();
        }

        void EndBlocks()
        {
            FlushParagraph();
            FlushQuote();
            list = null;
            lastItem = null;
        }

        foreach (string raw in lines)
        {
            string line = (raw.Length > MaxLineLength ? raw[..MaxLineLength] : raw).TrimEnd();
            if (line.TrimStart().StartsWith("```", StringComparison.Ordinal))
            {
                if (inCode)
                {
                    FlushCode();
                }
                else
                {
                    EndBlocks();
                }
                inCode = !inCode;
                continue;
            }
            if (inCode)
            {
                code.Add(line);
                continue;
            }

            string trimmed = StripHtml(line).Trim();
            if (trimmed.Length == 0)
            {
                EndBlocks();
                continue;
            }

            if (ReadHeading(trimmed) is { } heading)
            {
                EndBlocks();
                parts.Add();
                int level = heading.Level;
                var block = new Paragraph
                {
                    FontSize = level switch { 1 => 22, 2 => 19, _ => 16 },
                    FontWeight = FontWeights.Bold,
                    Foreground = level <= 2 ? Heading : Accent,
                    Margin = new Thickness(0, level <= 2 ? 10 : 6, 0, 6),
                };
                AddInlines(block.Inlines, heading.Text, parts);
                document.Blocks.Add(block);
                continue;
            }

            Match bullet = BulletLine().Match(line);
            if (bullet.Success)
            {
                FlushParagraph();
                FlushQuote();
                if (list == null)
                {
                    parts.Add();
                    list = new List { MarkerStyle = TextMarkerStyle.Disc, Margin = new Thickness(0, 0, 0, 10), Padding = new Thickness(18, 0, 0, 0) };
                    document.Blocks.Add(list);
                }
                parts.Add();
                lastItem = new Paragraph { Margin = new Thickness(0, 0, 0, 3) };
                AddInlines(lastItem.Inlines, StripHtml(bullet.Groups[2].Value), parts);
                var listItem = new ListItem(lastItem);
                if (bullet.Groups[1].Length >= 2) listItem.Margin = new Thickness(16, 0, 0, 0);
                list.ListItems.Add(listItem);
                continue;
            }

            if (trimmed.StartsWith('>'))
            {
                FlushParagraph();
                list = null;
                lastItem = null;
                quote.Add(trimmed.TrimStart('>', ' '));
                continue;
            }

            // A line right under a list item belongs to that item (Markdown's lazy continuation).
            if (lastItem != null)
            {
                AddInlines(lastItem.Inlines, " " + trimmed, parts);
                continue;
            }

            if (TableRule().IsMatch(trimmed)) continue;
            if (trimmed.StartsWith('|')) trimmed = string.Join("  ·  ", trimmed.Trim('|').Split('|').Select(c => c.Trim()));
            if (trimmed is "---" or "***" or "___")
            {
                EndBlocks();
                continue;
            }
            FlushQuote();
            paragraph.Add(trimmed);
        }
        // A code block that is never closed runs to the end.
        if (inCode && code.Count > 0) FlushCode();
        FlushParagraph();
        FlushQuote();
        return document;
    }

    /// <summary>"## Title ##" as (2, "Title"); null when the line isn't a heading. Read by hand: a pattern was slow on long lines.</summary>
    private static (int Level, string Text)? ReadHeading(string line)
    {
        int level = 0;
        while (level < line.Length && line[level] == '#') level++;
        if (level is 0 or > 6 || level == line.Length || !char.IsWhiteSpace(line[level])) return null;
        string text = line[level..].Trim();
        // Closing #s, after a space.
        int end = text.Length;
        while (end > 0 && text[end - 1] == '#') end--;
        if (end == 0) return (level, "");
        if (end < text.Length && char.IsWhiteSpace(text[end - 1])) text = text[..end].TrimEnd();
        return (level, text);
    }

    /// <summary>Drops &lt;!-- comments --&gt;; an unclosed one is left as it is. One pass, however the text is made.</summary>
    private static string StripComments(string text)
    {
        int start = text.IndexOf("<!--", StringComparison.Ordinal);
        if (start < 0) return text;
        var kept = new System.Text.StringBuilder(text.Length);
        int position = 0;
        while (start >= 0)
        {
            int end = text.IndexOf("-->", start + 4, StringComparison.Ordinal);
            if (end < 0) break;
            kept.Append(text, position, start - position);
            position = end + 3;
            start = text.IndexOf("<!--", position, StringComparison.Ordinal);
        }
        kept.Append(text, position, text.Length - position);
        return kept.ToString();
    }

    private static void AddInlines(InlineCollection inlines, string text, Parts parts)
    {
        int position = 0;
        foreach (Match match in InlineToken().Matches(text))
        {
            // The text before it and the piece itself (a link or bold text is two elements, counted as one).
            parts.Add(match.Index > position ? 2 : 1);
            if (match.Index > position) inlines.Add(new Run(text[position..match.Index]));
            if (match.Groups["img"].Success)
            {
                string alt = match.Groups["imgalt"].Value;
                if (alt.Length > 0) inlines.Add(new Run($"[{alt}]") { Foreground = Brushes.Gray });
            }
            else if (match.Groups["link"].Success)
            {
                string url = match.Groups["url"].Value;
                var link = new Hyperlink(new Run(match.Groups["linktext"].Value)) { Foreground = Link, Cursor = System.Windows.Input.Cursors.Hand };
                if (WebLink.IsWebPage(url, out _))
                {
                    link.ToolTip = url;
                    link.Click += (_, _) => Format.OpenUrl(url);
                }
                inlines.Add(link);
            }
            else if (match.Groups["bold"].Success)
            {
                inlines.Add(new Bold(new Run(match.Groups["boldtext"].Value)) { Foreground = Brushes.White });
            }
            else if (match.Groups["italic"].Success)
            {
                inlines.Add(new Italic(new Run(match.Groups["italictext"].Value)));
            }
            else if (match.Groups["code"].Success)
            {
                inlines.Add(new Run(match.Groups["codetext"].Value) { FontFamily = CodeFont, Background = CodeBack, Foreground = Brushes.White });
            }
            position = match.Index + match.Length;
        }
        if (position < text.Length)
        {
            parts.Add();
            inlines.Add(new Run(text[position..]));
        }
    }

    private static string StripHtml(string text) => HtmlTag().Replace(text, "");

    [GeneratedRegex(@"^(\s*)[-*+]\s+(.*)$", RegexOptions.None, MatchTimeoutMs)]
    private static partial Regex BulletLine();

    [GeneratedRegex(@"^\|?\s*:?-{3,}:?\s*(\|\s*:?-{3,}:?\s*)*\|?$", RegexOptions.None, MatchTimeoutMs)]
    private static partial Regex TableRule();

    // [^<>]: a run of "<a" without ">" is read once, not once per "<".
    [GeneratedRegex(@"</?[a-zA-Z][^<>]*>", RegexOptions.None, MatchTimeoutMs)]
    private static partial Regex HtmlTag();

    // Link and image parts can't hold "[" or "]" (runs of "[" or "[a](" that never close are read once), and the atomic
    // groups don't go back over what they took.
    [GeneratedRegex(@"(?<img>!\[(?<imgalt>(?>[^\[\]]*))\]\((?>[^)\[\]]*)\))|(?<link>\[(?<linktext>(?>[^\[\]]+))\]\((?<url>(?>[^)\s\[\]]+))(?>[^)\[\]]*)\))|(?<bold>\*\*(?<boldtext>(?>[^*]+))\*\*|__(?<boldtext>(?>[^_]+))__)|(?<code>`(?<codetext>(?>[^`]+))`)|(?<italic>(?<![\w*])\*(?<italictext>[^*\s](?>[^*]*))\*(?![\w*]))", RegexOptions.None, MatchTimeoutMs)]
    private static partial Regex InlineToken();
}
