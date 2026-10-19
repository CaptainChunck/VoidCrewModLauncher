using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;

namespace VoidCrewModLauncher.Infrastructure;

/// <summary>
/// Enough Markdown for Thunderstore READMEs: headings, lists, quotes, code blocks, bold, italics, inline code and links.
/// Images show their alt text, HTML tags are dropped, tables become plain lines.
/// </summary>
public static partial class Markdown
{
    private static readonly Brush Text = new SolidColorBrush(Color.FromRgb(0xC8, 0xD7, 0xE6));
    private static readonly Brush Heading = Brushes.White;
    private static readonly Brush Accent = new SolidColorBrush(Color.FromRgb(0xF6, 0xAE, 0x3C));
    private static readonly Brush Link = new SolidColorBrush(Color.FromRgb(0xBD, 0xE7, 0xFB));
    private static readonly Brush CodeBack = new SolidColorBrush(Color.FromArgb(0x60, 0, 0, 0));

    public static FlowDocument ToDocument(string? markdown)
    {
        var document = new FlowDocument
        {
            FontFamily = new FontFamily("Segoe UI"),
            FontSize = 14,
            Foreground = Text,
            PagePadding = new Thickness(0),
            TextAlignment = TextAlignment.Left,
        };
        if (string.IsNullOrWhiteSpace(markdown))
        {
            document.Blocks.Add(new Paragraph(new Run("This mod has no README.")) { Foreground = Brushes.Gray });
            return document;
        }

        string[] lines = HtmlComment().Replace(markdown.Replace("\r\n", "\n"), "").Split('\n');
        List? list = null;
        Paragraph? lastItem = null;
        var paragraph = new List<string>();
        var quote = new List<string>();
        bool inCode = false;
        var code = new List<string>();

        void FlushParagraph()
        {
            if (paragraph.Count == 0) return;
            var block = new Paragraph { Margin = new Thickness(0, 0, 0, 10), LineHeight = 20 };
            AddInlines(block.Inlines, string.Join(" ", paragraph));
            document.Blocks.Add(block);
            paragraph.Clear();
        }

        void FlushQuote()
        {
            if (quote.Count == 0) return;
            var block = new Paragraph
            {
                BorderBrush = Accent,
                BorderThickness = new Thickness(3, 0, 0, 0),
                Padding = new Thickness(10, 2, 0, 2),
                Margin = new Thickness(0, 0, 0, 10),
            };
            AddInlines(block.Inlines, string.Join(" ", quote));
            document.Blocks.Add(block);
            quote.Clear();
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
            string line = raw.TrimEnd();
            if (line.TrimStart().StartsWith("```", StringComparison.Ordinal))
            {
                if (inCode)
                {
                    document.Blocks.Add(new Paragraph(new Run(string.Join("\n", code)))
                    {
                        FontFamily = new FontFamily("Consolas"),
                        FontSize = 12.5,
                        Background = CodeBack,
                        Padding = new Thickness(8),
                        Margin = new Thickness(0, 0, 0, 10),
                    });
                    code.Clear();
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

            Match heading = HeadingLine().Match(trimmed);
            if (heading.Success)
            {
                EndBlocks();
                int level = heading.Groups[1].Length;
                var block = new Paragraph
                {
                    FontSize = level switch { 1 => 22, 2 => 19, _ => 16 },
                    FontWeight = FontWeights.Bold,
                    Foreground = level <= 2 ? Heading : Accent,
                    Margin = new Thickness(0, level <= 2 ? 10 : 6, 0, 6),
                };
                AddInlines(block.Inlines, heading.Groups[2].Value);
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
                    list = new List { MarkerStyle = TextMarkerStyle.Disc, Margin = new Thickness(0, 0, 0, 10), Padding = new Thickness(18, 0, 0, 0) };
                    document.Blocks.Add(list);
                }
                lastItem = new Paragraph { Margin = new Thickness(0, 0, 0, 3) };
                AddInlines(lastItem.Inlines, StripHtml(bullet.Groups[2].Value));
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
                AddInlines(lastItem.Inlines, " " + trimmed);
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
        FlushParagraph();
        FlushQuote();
        return document;
    }

    private static void AddInlines(InlineCollection inlines, string text)
    {
        int position = 0;
        foreach (Match match in InlineToken().Matches(text))
        {
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
                if (Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp))
                {
                    link.ToolTip = url;
                    link.Click += (_, _) => Format.Open(url);
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
                inlines.Add(new Run(match.Groups["codetext"].Value) { FontFamily = new FontFamily("Consolas"), Background = CodeBack, Foreground = Brushes.White });
            }
            position = match.Index + match.Length;
        }
        if (position < text.Length) inlines.Add(new Run(text[position..]));
    }

    private static string StripHtml(string text) => HtmlTag().Replace(text, "");

    [GeneratedRegex(@"^(#{1,6})\s+(.*?)\s*#*$")]
    private static partial Regex HeadingLine();

    [GeneratedRegex(@"^(\s*)[-*+]\s+(.*)$")]
    private static partial Regex BulletLine();

    [GeneratedRegex(@"^\|?\s*:?-{3,}:?\s*(\|\s*:?-{3,}:?\s*)*\|?$")]
    private static partial Regex TableRule();

    [GeneratedRegex(@"<!--.*?-->", RegexOptions.Singleline)]
    private static partial Regex HtmlComment();

    [GeneratedRegex(@"</?[a-zA-Z][^>]*>")]
    private static partial Regex HtmlTag();

    [GeneratedRegex(@"(?<img>!\[(?<imgalt>[^\]]*)\]\([^)]*\))|(?<link>\[(?<linktext>[^\]]+)\]\((?<url>[^)\s]+)[^)]*\))|(?<bold>\*\*(?<boldtext>[^*]+)\*\*|__(?<boldtext>[^_]+)__)|(?<code>`(?<codetext>[^`]+)`)|(?<italic>(?<![\w*])\*(?<italictext>[^*\s][^*]*)\*(?![\w*]))")]
    private static partial Regex InlineToken();
}
