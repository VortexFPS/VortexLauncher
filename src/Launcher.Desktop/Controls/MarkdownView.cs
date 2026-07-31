using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Media;

namespace Launcher.Desktop.Controls;

/// <summary>Renders the slice of markdown that GitHub release notes actually use: headings,
/// bold/italic, inline and fenced code, bullet and numbered lists, links and horizontal rules.
/// Anything else falls through as literal text, which is the failure mode we want — a construct we
/// do not understand shows up verbatim instead of disappearing.
///
/// This is hand-rolled rather than delegating to Markdown.Avalonia (see the note on
/// <see cref="SafeLinkPolicy"/> for why link handling is the deciding factor): its default
/// hyperlink command shell-executes whatever URL the document carries, so the one behaviour we most
/// need to constrain would have to be overridden anyway, and the package pulls a renderer stack
/// pinned to Avalonia's version into a launcher whose whole job is install/update/play.
///
/// Everything here is a hand-written scanner, no regex — release bodies are attacker-influenced and
/// a backtracking pattern over hostile input is a hang, not a crash.</summary>
public sealed class MarkdownView : Decorator
{
    // Matches MainWindow.axaml, which sets its colours as literals; a theme resource here would be
    // the one indirection in the window.
    private static readonly IBrush BodyBrush = SolidColorBrush.Parse("#aeb7c6");
    private static readonly IBrush HeadingBrush = SolidColorBrush.Parse("#e8ecf4");
    private static readonly IBrush LinkBrush = SolidColorBrush.Parse("#7fd4ff");
    private static readonly IBrush MutedBrush = SolidColorBrush.Parse("#5a6478");
    private static readonly IBrush CodeBrush = SolidColorBrush.Parse("#c8d3e6");
    private static readonly IBrush CodeBackground = SolidColorBrush.Parse("#0d1017");

    private static readonly FontFamily MonoFont =
        new("Cascadia Mono,Consolas,Menlo,DejaVu Sans Mono,monospace");

    private const double BaseFontSize = 13;

    // Hostile-input guards. Nothing bounds the size of a release body, every block becomes a control
    // in the visual tree, and the tree is built on the UI thread — a few megabytes of "- x" would
    // freeze the launcher. Notes past these limits are broken anyway, so truncating loudly beats
    // rendering them.
    private const int MaxInputChars = 64 * 1024;
    private const int MaxBlocks = 500;
    private const int MaxListDepth = 3;
    private const int MaxInlineDepth = 4;
    private const int MaxLinkLabelChars = 200;

    // Every inline scanner looks forward for its closing delimiter, and a delimiter that never
    // arrives costs a scan to the end of the block. Unbounded, "[[[[[…" 64k long is quadratic and
    // hangs the UI thread; bounded, an unclosed span just renders literally. No real span is this
    // long, so the cap only ever fires on input that was already malformed.
    private const int MaxSpanScanChars = 4096;

    /// <summary>The markdown source to render. Plain text renders as a plain paragraph, which is
    /// what keeps the empty-body fallback ("Notes: &lt;url&gt;") working unchanged.</summary>
    public static readonly StyledProperty<string?> MarkdownProperty =
        AvaloniaProperty.Register<MarkdownView, string?>(nameof(Markdown));

    /// <summary>The markdown source to render.</summary>
    public string? Markdown
    {
        get => GetValue(MarkdownProperty);
        set => SetValue(MarkdownProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == MarkdownProperty)
            Child = Render(Markdown);
    }

    private Control Render(string? markdown)
    {
        var panel = new StackPanel { Spacing = 6 };
        if (string.IsNullOrWhiteSpace(markdown))
            return panel;

        var truncated = markdown.Length > MaxInputChars;
        var blocks = ParseBlocks(truncated ? markdown[..MaxInputChars] : markdown, ref truncated);

        for (var i = 0; i < blocks.Count; i++)
            panel.Children.Add(BuildBlock(blocks[i], first: i == 0));

        if (truncated)
            panel.Children.Add(new TextBlock
            {
                Text = "…release notes truncated.",
                Foreground = MutedBrush,
                FontSize = BaseFontSize,
                FontStyle = FontStyle.Italic,
                Margin = new Thickness(0, 6, 0, 0),
            });

        return panel;
    }

    // ---- block level ----------------------------------------------------------------------

    private enum BlockKind
    {
        Paragraph,
        Heading,
        Code,
        Bullet,
        Ordered,
        Rule,
    }

    private sealed record Block(BlockKind Kind, string Text, int Level = 0, int Indent = 0, int Number = 0);

    private static List<Block> ParseBlocks(string text, ref bool truncated)
    {
        var blocks = new List<Block>();
        var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var paragraph = new StringBuilder();
        var counters = new int[MaxListDepth + 1];
        var inItem = false;
        var line = 0;

        void FlushParagraph()
        {
            if (paragraph.Length == 0)
                return;
            blocks.Add(new Block(BlockKind.Paragraph, paragraph.ToString()));
            paragraph.Clear();
        }

        void ResetOrdered()
        {
            Array.Clear(counters);
        }

        while (line < lines.Length && blocks.Count < MaxBlocks)
        {
            var raw = lines[line];
            var indent = ListLevel(raw);
            var trimmed = raw.Trim();

            if (IsFence(trimmed, out var fenceChar, out var fenceLength))
            {
                FlushParagraph();
                ResetOrdered();
                inItem = false;
                line++;
                var code = new StringBuilder();
                while (line < lines.Length && !IsClosingFence(lines[line].Trim(), fenceChar, fenceLength))
                {
                    code.Append(lines[line]).Append('\n');
                    line++;
                }
                if (line < lines.Length)
                    line++; // the closing fence itself
                blocks.Add(new Block(BlockKind.Code, code.ToString().TrimEnd('\n')));
                continue;
            }

            if (trimmed.Length == 0)
            {
                FlushParagraph();
                // Deliberately not resetting the ordered counters: a numbered list with blank lines
                // between the items is still one list, and restarting it at 1 is the bug this avoids.
                inItem = false;
                line++;
                continue;
            }

            if (IsRule(trimmed))
            {
                FlushParagraph();
                ResetOrdered();
                inItem = false;
                blocks.Add(new Block(BlockKind.Rule, ""));
                line++;
                continue;
            }

            if (TryHeading(trimmed, out var level, out var heading))
            {
                FlushParagraph();
                ResetOrdered();
                inItem = false;
                blocks.Add(new Block(BlockKind.Heading, heading, Level: level));
                line++;
                continue;
            }

            if (TryBullet(trimmed, out var bullet))
            {
                FlushParagraph();
                // Only this level and deeper. A bullet nested under "1." is part of that item, not
                // the end of the numbered list, so clearing every counter restarts the parent at 1 —
                // "1. / - sub / 2." rendered as 1, •, 1.
                Array.Clear(counters, indent, counters.Length - indent);
                inItem = true;
                blocks.Add(new Block(BlockKind.Bullet, bullet, Indent: indent));
                line++;
                continue;
            }

            if (TryOrdered(trimmed, out var ordered))
            {
                FlushParagraph();
                for (var deeper = indent + 1; deeper <= MaxListDepth; deeper++)
                    counters[deeper] = 0;
                counters[indent]++;
                inItem = true;
                blocks.Add(new Block(BlockKind.Ordered, ordered, Indent: indent, Number: counters[indent]));
                line++;
                continue;
            }

            // Lazy continuation: an unindented line straight after a list item belongs to that item.
            if (inItem && paragraph.Length == 0 && blocks.Count > 0)
            {
                blocks[^1] = blocks[^1] with { Text = blocks[^1].Text + " " + trimmed };
                line++;
                continue;
            }

            ResetOrdered();
            if (paragraph.Length > 0)
                paragraph.Append(' ');
            paragraph.Append(trimmed);
            line++;
        }

        if (blocks.Count < MaxBlocks)
            FlushParagraph();
        if (line < lines.Length)
            truncated = true;

        return blocks;
    }

    private Control BuildBlock(Block block, bool first) => block.Kind switch
    {
        BlockKind.Heading => Heading(block, first),
        BlockKind.Code => CodeFence(block.Text),
        BlockKind.Rule => new Border
        {
            Height = 1,
            Background = MutedBrush,
            Opacity = 0.5,
            Margin = new Thickness(0, 6, 0, 6),
        },
        BlockKind.Bullet => ListItem(block, "•"),
        BlockKind.Ordered => ListItem(block, $"{block.Number}."),
        _ => Paragraph(block.Text, BaseFontSize, BodyBrush),
    };

    private Control Heading(Block block, bool first)
    {
        var size = block.Level switch { 1 => 18d, 2 => 16d, 3 => 14d, _ => BaseFontSize };
        var text = Paragraph(block.Text, size, HeadingBrush);
        text.FontWeight = FontWeight.Bold;
        text.Margin = new Thickness(0, first ? 0 : 8, 0, 0);
        return text;
    }

    private Control ListItem(Block block, string marker)
    {
        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*"),
            Margin = new Thickness(block.Indent * 14, 0, 0, 0),
        };

        var glyph = new TextBlock
        {
            Text = marker,
            Foreground = MutedBrush,
            FontSize = BaseFontSize,
            Margin = new Thickness(0, 0, 6, 0),
        };
        Grid.SetColumn(glyph, 0);

        var body = Paragraph(block.Text, BaseFontSize, BodyBrush);
        Grid.SetColumn(body, 1);

        grid.Children.Add(glyph);
        grid.Children.Add(body);
        return grid;
    }

    private static Control CodeFence(string code) => new Border
    {
        Background = CodeBackground,
        CornerRadius = new CornerRadius(4),
        Padding = new Thickness(10, 8),
        Margin = new Thickness(0, 2, 0, 2),
        // Fenced code is the one block that must not wrap, so it scrolls sideways rather than
        // reflowing a command line into something that would not paste back.
        Child = new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = new TextBlock
            {
                Text = code,
                FontFamily = MonoFont,
                FontSize = BaseFontSize - 1,
                Foreground = CodeBrush,
                TextWrapping = TextWrapping.NoWrap,
            },
        },
    };

    private TextBlock Paragraph(string text, double fontSize, IBrush foreground)
    {
        var block = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            FontSize = fontSize,
            Foreground = foreground,
        };
        AppendInlines(block.Inlines!, text, fontSize, 0);
        return block;
    }

    // ---- inline level ---------------------------------------------------------------------

    private void AppendInlines(InlineCollection target, string text, double fontSize, int depth)
    {
        var buffer = new StringBuilder();
        var i = 0;

        void Flush()
        {
            if (buffer.Length == 0)
                return;
            target.Add(new Run(buffer.ToString()));
            buffer.Clear();
        }

        while (i < text.Length)
        {
            var c = text[i];

            if (c == '\\' && i + 1 < text.Length && IsEscapable(text[i + 1]))
            {
                buffer.Append(text[i + 1]);
                i += 2;
                continue;
            }

            if (c == '`')
            {
                var ticks = RunLength(text, i, '`');
                var close = IndexOfRun(text, i + ticks, '`', ticks);
                if (close > i)
                {
                    Flush();
                    target.Add(new Run(text[(i + ticks)..close].Trim())
                    {
                        FontFamily = MonoFont,
                        Foreground = CodeBrush,
                        Background = CodeBackground,
                    });
                    i = close + ticks;
                }
                else
                {
                    // Same rule as emphasis below: a span that never closes stays literal, whole run
                    // included, rather than letting a shorter re-scan invent a span that is not there.
                    buffer.Append(c, ticks);
                    i += ticks;
                }
                continue;
            }

            // Images render as their alt text and nothing else. Resolving ![](https://…) would have
            // the launcher fetch an attacker-chosen URL the moment a player opens the notes pane,
            // which is a beacon: it leaks that this machine updated, and its IP, with no click.
            if (c == '!' && i + 1 < text.Length && text[i + 1] == '[' &&
                TryLink(text, i + 1, out var alt, out _, out var afterImage))
            {
                Flush();
                var altText = LabelText(alt);
                if (altText.Length > 0)
                    target.Add(new Run(altText) { Foreground = MutedBrush, FontStyle = FontStyle.Italic });
                i = afterImage;
                continue;
            }

            if (c == '[' && TryLink(text, i, out var label, out var href, out var afterLink))
            {
                Flush();
                AppendLink(target, LabelText(label), href, fontSize);
                i = afterLink;
                continue;
            }

            if (c == '<')
            {
                var close = LimitedIndexOf(text, '>', i + 1);
                if (close > i + 1 && SafeLinkPolicy.TryParse(text[(i + 1)..close], out var angle))
                {
                    Flush();
                    AppendLink(target, angle.ToString(), angle.ToString(), fontSize);
                    i = close + 1;
                    continue;
                }
            }

            if ((c == 'h' || c == 'H') && TryBareUrl(text, i, out var bare, out var afterBare))
            {
                Flush();
                AppendLink(target, bare, bare, fontSize);
                i = afterBare;
                continue;
            }

            if (c == '*' || c == '_')
            {
                if (depth < MaxInlineDepth &&
                    TryEmphasis(text, i, c, out var inner, out var width, out var afterEmphasis))
                {
                    Flush();
                    var span = new Span();
                    if (width >= 2)
                        span.FontWeight = FontWeight.Bold;
                    if (width != 2)
                        span.FontStyle = FontStyle.Italic;
                    AppendInlines(span.Inlines, inner, fontSize, depth + 1);
                    target.Add(span);
                    i = afterEmphasis;
                    continue;
                }

                // Emphasis that does not close is emphasis we misread, so the whole delimiter run
                // goes through verbatim. Emitting one marker and re-scanning from the next would let
                // "**oops" turn its second asterisk into a working italic opener.
                var literal = RunLength(text, i, c);
                buffer.Append(c, literal);
                i += literal;
                continue;
            }

            buffer.Append(c);
            i++;
        }

        Flush();
    }

    private void AppendLink(InlineCollection target, string label, string href, double fontSize)
    {
        // A link whose target we will not open is not shown as a link. Rendering it as inert text
        // is the point: the player never gets a clickable affordance for a scheme we refused, and
        // the text is still there so nothing silently vanishes from the notes.
        if (!SafeLinkPolicy.TryParse(href, out var uri))
        {
            target.Add(new Run(label));
            return;
        }

        var shown = label.Length > MaxLinkLabelChars ? label[..MaxLinkLabelChars] + "…" : label;
        var link = new HyperlinkButton
        {
            // NavigateUri is deliberately left unset. Avalonia's HyperlinkButton hands NavigateUri
            // straight to ILauncher — the OS shell — for ANY scheme it can parse, which is exactly
            // the hole this whole class exists to close. Going through Click instead means the only
            // URI that can ever reach the shell is one SafeLinkPolicy already accepted.
            Content = shown.Length == 0 ? uri.ToString() : shown,
            Foreground = LinkBrush,
            FontSize = fontSize,
            Padding = new Thickness(0),
            Margin = new Thickness(0),
            MinWidth = 0,
            MinHeight = 0,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Cursor = new Cursor(StandardCursorType.Hand),
        };
        // The label is attacker-controlled and free to lie about where it goes, so the real
        // destination is always one hover away.
        ToolTip.SetTip(link, uri.ToString());
        link.Click += (_, _) => SafeLinkPolicy.Open(link, uri);

        target.Add(new InlineUIContainer(link));
    }

    // ---- scanners -------------------------------------------------------------------------

    private static bool IsFence(string trimmed, out char fence, out int length)
    {
        fence = '\0';
        length = 0;
        if (trimmed.Length < 3 || (trimmed[0] != '`' && trimmed[0] != '~'))
            return false;
        fence = trimmed[0];
        length = RunLength(trimmed, 0, fence);
        return length >= 3;
    }

    private static bool IsClosingFence(string trimmed, char fence, int length) =>
        trimmed.Length >= length && RunLength(trimmed, 0, fence) >= length &&
        trimmed.TrimEnd(fence).Length == 0;

    private static bool IsRule(string trimmed)
    {
        var marker = trimmed[0];
        if (marker != '-' && marker != '*' && marker != '_')
            return false;
        var count = 0;
        foreach (var c in trimmed)
        {
            if (c == marker)
                count++;
            else if (c != ' ' && c != '\t')
                return false;
        }
        return count >= 3;
    }

    private static bool TryHeading(string trimmed, out int level, out string text)
    {
        level = RunLength(trimmed, 0, '#');
        text = "";
        if (level is < 1 or > 6 || level >= trimmed.Length || trimmed[level] != ' ')
            return false;

        text = trimmed[level..].Trim();

        // A trailing run of '#' only closes the heading when a space precedes it, which is what
        // keeps "## Fixes for C#" from rendering as "Fixes for C".
        var closing = text.Length;
        while (closing > 0 && text[closing - 1] == '#')
            closing--;
        if (closing < text.Length && closing > 0 && text[closing - 1] == ' ')
            text = text[..closing].TrimEnd();

        return true;
    }

    private static bool TryBullet(string trimmed, out string text)
    {
        text = "";
        if (trimmed.Length < 2 || (trimmed[0] != '-' && trimmed[0] != '*' && trimmed[0] != '+'))
            return false;
        if (trimmed[1] != ' ' && trimmed[1] != '\t')
            return false;
        text = trimmed[2..].Trim();
        return true;
    }

    private static bool TryOrdered(string trimmed, out string text)
    {
        text = "";
        var digits = 0;
        while (digits < trimmed.Length && char.IsAsciiDigit(trimmed[digits]))
            digits++;
        if (digits is 0 or > 9 || digits + 1 >= trimmed.Length)
            return false;
        if (trimmed[digits] != '.' && trimmed[digits] != ')')
            return false;
        if (trimmed[digits + 1] != ' ' && trimmed[digits + 1] != '\t')
            return false;
        text = trimmed[(digits + 2)..].Trim();
        return true;
    }

    private static bool TryLink(string s, int start, out string label, out string href, out int next)
    {
        label = "";
        href = "";
        next = start;

        var close = LimitedIndexOf(s, ']', start + 1);
        if (close < 0)
            return false;

        // A '[' inside the label means the first ']' is probably not ours. The badge idiom
        // [![alt](img)](target) nests one, and it is all over release notes; stopping at the first
        // ']' there makes the badge's IMAGE url the thing the player clicks and spills the rest of
        // the construct into the page as literal text. Only balance when a nested bracket is
        // actually present — the scan below is a scalar loop and the common label has none.
        if (close > start + 1 && s.IndexOf('[', start + 1, close - start - 1) >= 0)
        {
            close = -1;
            var brackets = 0;
            var labelLimit = Math.Min(s.Length, start + MaxSpanScanChars);
            for (var scan = start; scan < labelLimit; scan++)
            {
                if (s[scan] == '[')
                    brackets++;
                else if (s[scan] == ']' && --brackets == 0)
                {
                    close = scan;
                    break;
                }
            }
        }

        if (close < 0 || close + 1 >= s.Length || s[close + 1] != '(')
            return false;

        // Parens inside the destination have to balance, not stop at the first ')': Wikipedia links
        // like .../Quake_(video_game) are the common legitimate case, and truncating one is how a
        // link that should have been rendered ends up mangled instead.
        var depth = 0;
        var end = -1;
        var limit = Math.Min(s.Length, close + 1 + MaxSpanScanChars);
        for (var scan = close + 1; scan < limit; scan++)
        {
            if (s[scan] == '(')
                depth++;
            else if (s[scan] == ')' && --depth == 0)
            {
                end = scan;
                break;
            }
        }
        if (end < 0)
            return false;

        label = s[(start + 1)..close];
        href = s[(close + 2)..end].Trim();

        // [text](url "title") — the title is not rendered, but it must not end up in the href.
        var space = href.IndexOf(' ');
        if (space > 0)
            href = href[..space];
        href = href.Trim('<', '>');

        next = end + 1;
        return true;
    }

    /// <summary>Reduces a bracket label — a link's text, or an image's alt — to what gets shown.
    /// Autolinks deliberately skip this: their label IS the destination, and rewriting it is how the
    /// text a player reads starts drifting from where the click actually goes.</summary>
    private static string LabelText(string label)
    {
        // The overwhelmingly common label is plain text; skip the walk entirely for it.
        if (!label.Contains("!["))
            return label;

        // The image rule holds inside a link label too, and this is where it matters most: a badge
        // is [![alt](img)](target), so the label carries a url the launcher must still never fetch
        // and never make clickable. Alt text only, same as a standalone image.
        var text = new StringBuilder(label.Length);
        var i = 0;
        while (i < label.Length)
        {
            if (label[i] == '!' && i + 1 < label.Length && label[i + 1] == '[' &&
                TryLink(label, i + 1, out var alt, out _, out var after))
            {
                text.Append(LabelText(alt));
                i = after;
                continue;
            }

            text.Append(label[i]);
            i++;
        }
        return text.ToString();
    }

    private static bool TryBareUrl(string s, int start, out string url, out int next)
    {
        url = "";
        next = start;

        // GitHub's generated notes always end with a bare "Full Changelog: https://…", so a plain
        // URL has to linkify or the most common line in the pane is dead text.
        if (start > 0 && (char.IsLetterOrDigit(s[start - 1]) || s[start - 1] == '/'))
            return false;
        var rest = s.AsSpan(start);
        if (!rest.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            !rest.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            return false;

        var end = start;
        while (end < s.Length && !char.IsWhiteSpace(s[end]) && s[end] != '<' && s[end] != '>')
            end++;

        // Trailing sentence punctuation belongs to the prose, not the URL. A closing paren only
        // counts as punctuation when it has no opener inside the URL — Wikipedia links need it.
        while (end > start)
        {
            var last = s[end - 1];
            if (last is '.' or ',' or ';' or ':' or '!' or '?' or '"' or '\'')
                end--;
            else if (last == ')' && Count(s, start, end, ')') > Count(s, start, end, '('))
                end--;
            else
                break;
        }

        var candidate = s[start..end];
        if (!SafeLinkPolicy.TryParse(candidate, out _))
            return false;

        url = candidate;
        next = end;
        return true;
    }

    /// <summary><paramref name="width"/> comes back as the delimiter length: 1 italic, 2 bold,
    /// 3 both.</summary>
    private static bool TryEmphasis(string s, int start, char marker, out string inner, out int width, out int next)
    {
        inner = "";
        next = start;

        width = Math.Min(RunLength(s, start, marker), 3);
        var contentStart = start + width;
        if (contentStart >= s.Length || char.IsWhiteSpace(s[contentStart]))
            return false;

        // snake_case identifiers and query strings are everywhere in release notes, so an
        // underscore only opens emphasis at a word boundary. Asterisks have no such problem.
        if (marker == '_' && start > 0 && char.IsLetterOrDigit(s[start - 1]))
            return false;

        var close = IndexOfRun(s, contentStart, marker, width);
        if (close < 0 || close == contentStart)
            return false;
        if (marker == '_' && close + width < s.Length && char.IsLetterOrDigit(s[close + width]))
            return false;

        inner = s[contentStart..close];
        next = close + width;
        return true;
    }

    private static int LimitedIndexOf(string s, char c, int from) =>
        from >= s.Length ? -1 : s.IndexOf(c, from, Math.Min(MaxSpanScanChars, s.Length - from));

    private static int RunLength(string s, int start, char c)
    {
        var n = 0;
        while (start + n < s.Length && s[start + n] == c)
            n++;
        return n;
    }

    /// <summary>Finds the next run of exactly <paramref name="length"/> <paramref name="c"/>.</summary>
    /// <remarks>Exact, not "at least": it is what lets a one-marker span close past a longer run, so
    /// "*a **b** c*" italicises the whole thing instead of stopping at the first inner marker.</remarks>
    private static int IndexOfRun(string s, int from, char c, int length)
    {
        var limit = Math.Min(s.Length - length + 1, from + MaxSpanScanChars);
        for (var i = from; i < limit; i++)
        {
            if (s[i] != c)
                continue;
            var run = RunLength(s, i, c);
            if (run == length)
                return i;
            i += run - 1;
        }
        return -1;
    }

    private static int Count(string s, int start, int end, char c)
    {
        var n = 0;
        for (var i = start; i < end; i++)
            if (s[i] == c)
                n++;
        return n;
    }

    private static int ListLevel(string raw)
    {
        var width = 0;
        foreach (var c in raw)
        {
            if (c == ' ')
                width++;
            else if (c == '\t')
                width += 4;
            else
                break;
        }
        return Math.Min(width / 2, MaxListDepth);
    }

    private static bool IsEscapable(char c) => !char.IsLetterOrDigit(c) && !char.IsWhiteSpace(c);
}
