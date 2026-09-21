using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Layout;
using Markdig;
using Markdig.Extensions.Tables;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace EveConsole.Views;

/// <summary>
/// Renders Markdown into themed Avalonia controls.
///
/// <para>Markdig parses; the rendering is ours. A drop-in Markdown control brings its own
/// stylesheet, and a report that arrives with its own colours in an application built entirely on
/// palette tokens looks like something pasted in from elsewhere — which, in a dark theme, usually
/// means unreadable.</para>
///
/// <para>⚠️ Every control produced here is given a class and NO literal colours or sizes. The
/// appearance lives in AgentDocumentView.axaml as DynamicResource setters, so a report follows a
/// theme change like the rest of the application.</para>
///
/// <para>Deliberately a subset: headings, paragraphs, emphasis, code, lists, block quotes, rules
/// and pipe tables. That is what a report is made of. Anything unrecognised falls back to its own
/// plain text rather than vanishing — a document that renders imperfectly is still a document,
/// one that silently drops a section is a wrong answer.</para>
/// </summary>
public static class MarkdownRenderer
{
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UsePipeTables()
        .UseEmphasisExtras()
        .UseAutoLinks()
        .Build();

    public static Control Render(string markdown)
    {
        var panel = new StackPanel { Spacing = 0 };
        try
        {
            var document = Markdig.Markdown.Parse(markdown ?? "", Pipeline);
            foreach (var block in document)
                AddBlock(panel, block);
        }
        catch (Exception ex)
        {
            // ⚠️ Shown, not swallowed. The alternative is a blank tab, which reads as "the agent
            // produced nothing" rather than "this failed to render".
            panel.Children.Add(Text($"This document could not be rendered: {ex.Message}", "md-error"));
            panel.Children.Add(Text(markdown ?? "", "md-codetext"));
        }
        return panel;
    }

    private static void AddBlock(Panel host, Block block)
    {
        switch (block)
        {
            case HeadingBlock h:
                host.Children.Add(Inlined(h.Inline, $"md-h{Math.Clamp(h.Level, 1, 4)}"));
                break;

            case ParagraphBlock p:
                host.Children.Add(Inlined(p.Inline, "md-p"));
                break;

            case ListBlock list:
                AddList(host, list);
                break;

            case QuoteBlock quote:
            {
                var inner = new StackPanel { Spacing = 0 };
                foreach (var child in quote) AddBlock(inner, child);
                var border = new Border { Child = inner };
                border.Classes.Add("md-quote");
                host.Children.Add(border);
                break;
            }

            case Table table:
                host.Children.Add(BuildTable(table));
                break;

            case ThematicBreakBlock:
            {
                var rule = new Border();
                rule.Classes.Add("md-hr");
                host.Children.Add(rule);
                break;
            }

            case CodeBlock code:
            {
                var body = new Border { Child = Text(code.Lines.ToString(), "md-codetext") };
                body.Classes.Add("md-code");
                host.Children.Add(body);
                break;
            }

            case ContainerBlock container:
                foreach (var child in container) AddBlock(host, child);
                break;

            case LeafBlock leaf when leaf.Inline is not null:
                host.Children.Add(Inlined(leaf.Inline, "md-p"));
                break;
        }
    }

    /// <summary>
    /// A list, one row per item, marker in its own column so wrapped text lines up under itself
    /// rather than under the bullet.
    /// </summary>
    private static void AddList(Panel host, ListBlock list)
    {
        var stack = new StackPanel { Spacing = 0 };
        stack.Classes.Add("md-list");

        var number = list.IsOrdered && int.TryParse(list.OrderedStart, out var start) ? start : 1;

        foreach (var item in list.OfType<ListItemBlock>())
        {
            var marker = Text(list.IsOrdered ? $"{number++}." : "•", "md-marker");

            var content = new StackPanel { Spacing = 0 };
            foreach (var child in item) AddBlock(content, child);

            var row = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("Auto,*"),
                VerticalAlignment = VerticalAlignment.Top,
            };
            Grid.SetColumn(marker, 0);
            Grid.SetColumn(content, 1);
            row.Children.Add(marker);
            row.Children.Add(content);
            stack.Children.Add(row);
        }

        host.Children.Add(stack);
    }

    /// <summary>
    /// A pipe table as a Grid of bordered cells.
    ///
    /// <para>A Grid rather than a DataGrid: this is a fixed piece of a document, not something to
    /// sort or select. The agent's <c>show_table</c> tool is the place for data meant to be worked
    /// with, and it opens a real grid with copy and CSV export.</para>
    /// </summary>
    private static Control BuildTable(Table table)
    {
        var rows = table.OfType<TableRow>().ToList();
        var columnCount = rows.Count == 0 ? 0 : rows.Max(r => r.Count);
        if (columnCount == 0) return new StackPanel();

        var grid = new Grid();
        grid.Classes.Add("md-table");
        for (int c = 0; c < columnCount; c++)
            grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
        for (int r = 0; r < rows.Count; r++)
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));

        for (int r = 0; r < rows.Count; r++)
        {
            var row = rows[r];
            for (int c = 0; c < row.Count; c++)
            {
                var content = new StackPanel { Spacing = 0 };
                if (row[c] is TableCell cell)
                    foreach (var child in cell) AddBlock(content, child);

                var box = new Border { Child = content };
                box.Classes.Add(row.IsHeader ? "md-th" : "md-td");
                Grid.SetRow(box, r);
                Grid.SetColumn(box, c);
                grid.Children.Add(box);
            }
        }

        // Scrolls on its own so a wide table never widens the document.
        return new ScrollViewer
        {
            Content                        = grid,
            HorizontalScrollBarVisibility  = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility    = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
        };
    }

    private static SelectableTextBlock Inlined(ContainerInline? inline, string cls)
    {
        var block = new SelectableTextBlock { TextWrapping = Avalonia.Media.TextWrapping.Wrap };
        block.Classes.Add(cls);
        if (inline is not null)
            foreach (var child in inline)
                AddInline(block.Inlines!, child);
        return block;
    }

    private static void AddInline(InlineCollection host, Markdig.Syntax.Inlines.Inline inline)
    {
        switch (inline)
        {
            case LiteralInline literal:
                host.Add(new Run(literal.Content.ToString()));
                break;

            case EmphasisInline emphasis:
            {
                // ** and __ are bold, * and _ italic; ~~ is strikethrough from UseEmphasisExtras.
                Span span = emphasis.DelimiterChar == '~'  ? new Span()
                          : emphasis.DelimiterCount >= 2   ? new Bold()
                          :                                  new Italic();
                if (emphasis.DelimiterChar == '~') span.Classes.Add("md-strike");
                foreach (var child in emphasis) AddInline(span.Inlines, child);
                host.Add(span);
                break;
            }

            case CodeInline code:
            {
                var run = new Run(code.Content);
                run.Classes.Add("md-codespan");
                host.Add(run);
                break;
            }

            case LinkInline link:
            {
                var span = new Span();
                span.Classes.Add("md-link");
                foreach (var child in link) AddInline(span.Inlines, child);
                // Nothing inside it produced text, so show the target — a link that renders as
                // nothing at all is worse than one that renders as its URL.
                if (span.Inlines.Count == 0) span.Inlines.Add(new Run(link.Url ?? ""));
                host.Add(span);
                break;
            }

            case LineBreakInline lineBreak:
                host.Add(lineBreak.IsHard ? new LineBreak() : new Run(" "));
                break;

            case ContainerInline container:
                foreach (var child in container) AddInline(host, child);
                break;

            default:
                if (inline.ToString() is { Length: > 0 } text) host.Add(new Run(text));
                break;
        }
    }

    private static SelectableTextBlock Text(string text, string cls)
    {
        var block = new SelectableTextBlock
        {
            Text         = text,
            TextWrapping = Avalonia.Media.TextWrapping.Wrap,
        };
        block.Classes.Add(cls);
        return block;
    }
}
