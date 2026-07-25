using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Markdig;
using Markdig.Extensions.TaskLists;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using MdTable = Markdig.Extensions.Tables.Table;
using MdTableCell = Markdig.Extensions.Tables.TableCell;
using MdTableRow = Markdig.Extensions.Tables.TableRow;

namespace NoteScribe.App.Views;

/// <summary>
/// Renders markdown as native Avalonia controls.
/// </summary>
/// <remarks>
/// <para>
/// Markdig parses to an AST and this walks it; there is no HTML and no WebView anywhere in the
/// path. <c>Avalonia.Controls.Markdown</c> would have been the obvious shortcut but it drags in the
/// commercial <c>AvaloniaUI.Licensing</c> package, which the project deliberately avoids.
/// </para>
/// <para>
/// Every colour, size and spacing value comes from the design-token resources, looked up once per
/// render, so the preview shifts with the palette instead of pinning its own copy of it.
/// </para>
/// </remarks>
public sealed class MarkdownPreview : Decorator
{
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UsePipeTables()
        .UseGridTables()
        .UseTaskLists()
        .UseEmphasisExtras()
        .UseAutoLinks()
        .UseFootnotes()
        .UseListExtras()
        .Build();

    public static readonly StyledProperty<string?> MarkdownProperty =
        AvaloniaProperty.Register<MarkdownPreview, string?>(nameof(Markdown));

    private bool _pending = true;

    /// <summary>The source text. Reparsed and re-rendered whenever it changes.</summary>
    public string? Markdown
    {
        get => GetValue(MarkdownProperty);
        set => SetValue(MarkdownProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == MarkdownProperty)
        {
            _pending = true;
            Rebuild();
        }
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);

        // Before attach, the token resources are not reachable from this control, so the first
        // render would silently fall back to its hardcoded defaults.
        if (_pending)
        {
            Rebuild();
        }
    }

    private void Rebuild()
    {
        var source = Markdown;

        if (string.IsNullOrWhiteSpace(source))
        {
            Child = new TextBlock
            {
                Text = "Nothing to preview yet.",
                Foreground = Brush("TextMuted", Colors.Gray),
                FontSize = Size("FontSizeMd", 13),
                Margin = new Thickness(0, 4, 0, 0),
            };
            return;
        }

        var theme = new Palette(this);
        var root = new StackPanel { Spacing = 0 };

        try
        {
            var document = Markdig.Markdown.Parse(source, Pipeline);
            foreach (var block in document)
            {
                AddBlock(root.Children, block, theme);
            }
        }
        catch (Exception ex)
        {
            // A preview is never worth taking the page down for.
            root.Children.Add(new TextBlock
            {
                Text = $"Could not render the preview: {ex.Message}",
                Foreground = theme.Danger,
                TextWrapping = TextWrapping.Wrap,
            });
        }

        Child = root;
        _pending = false;
    }

    // ---- blocks ---------------------------------------------------------------------------------

    private void AddBlock(Avalonia.Controls.Controls target, Block block, Palette theme)
    {
        switch (block)
        {
            case HeadingBlock heading:
                target.Add(BuildHeading(heading, theme));
                break;

            case ParagraphBlock paragraph:
                target.Add(BuildParagraph(paragraph, theme));
                break;

            case ListBlock list:
                target.Add(BuildList(list, theme));
                break;

            case QuoteBlock quote:
                target.Add(BuildQuote(quote, theme));
                break;

            case MdTable table:
                target.Add(BuildTable(table, theme));
                break;

            case FencedCodeBlock fenced:
                target.Add(BuildCode(fenced.Lines.ToString(), fenced.Info, theme));
                break;

            case CodeBlock code:
                target.Add(BuildCode(code.Lines.ToString(), null, theme));
                break;

            case ThematicBreakBlock:
                target.Add(new Border
                {
                    Height = 1,
                    Background = theme.BorderDefault,
                    Margin = new Thickness(0, theme.Space5, 0, theme.Space5),
                });
                break;

            case HtmlBlock html:
                target.Add(BuildCode(html.Lines.ToString(), "html", theme));
                break;

            case ContainerBlock container:
                foreach (var child in container)
                {
                    AddBlock(target, child, theme);
                }

                break;

            case LeafBlock leaf when leaf.Inline is not null:
                target.Add(BuildInlineHost(leaf, theme, theme.BodySize, FontWeight.Normal));
                break;
        }
    }

    private Control BuildHeading(HeadingBlock heading, Palette theme)
    {
        var size = heading.Level switch
        {
            1 => theme.DisplaySize,
            2 => theme.XlSize,
            3 => theme.LgSize,
            _ => theme.BodySize,
        };

        var text = BuildInlineHost(heading, theme, size, FontWeight.SemiBold);
        text.Margin = new Thickness(0, heading.Level <= 2 ? theme.Space5 : theme.Space4, 0, theme.Space2);
        text.Foreground = theme.TextPrimary;

        if (heading.Level > 3)
        {
            text.Foreground = theme.TextSecondary;
        }

        if (heading.Level > 2)
        {
            return text;
        }

        // H1/H2 get the rule under them that makes long notes scannable.
        return new StackPanel
        {
            Children =
            {
                text,
                new Border
                {
                    Height = 1,
                    Background = theme.BorderSubtle,
                    Margin = new Thickness(0, theme.Space1, 0, theme.Space2),
                },
            },
        };
    }

    private TextBlock BuildParagraph(ParagraphBlock paragraph, Palette theme)
    {
        var text = BuildInlineHost(paragraph, theme, theme.BodySize, FontWeight.Normal);
        text.Margin = new Thickness(0, 0, 0, theme.Space4);
        return text;
    }

    private TextBlock BuildInlineHost(LeafBlock leaf, Palette theme, double fontSize, FontWeight weight)
    {
        var block = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            TextTrimming = TextTrimming.None,
            FontSize = fontSize,
            FontWeight = weight,
            Foreground = theme.TextPrimary,
            LineHeight = Math.Round(fontSize * 1.55),
        };

        var inlines = new InlineCollection();
        if (leaf.Inline is not null)
        {
            AppendInlines(inlines, leaf.Inline, theme, new InlineStyle(weight, FontStyle.Normal, false, false, false));
        }

        if (inlines.Count == 0)
        {
            inlines.Add(new Run(string.Empty));
        }

        block.Inlines = inlines;
        return block;
    }

    private Control BuildList(ListBlock list, Palette theme, int depth = 0)
    {
        var stack = new StackPanel { Spacing = theme.Space1, Margin = new Thickness(0, 0, 0, theme.Space4) };
        var number = 1;

        if (list.IsOrdered && int.TryParse(list.OrderedStart, out var start))
        {
            number = start;
        }

        foreach (var child in list)
        {
            if (child is not ListItemBlock item)
            {
                continue;
            }

            var (marker, isTask, isChecked) = ResolveMarker(list, item, ref number);

            var row = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("Auto,*"),
                Margin = new Thickness(depth * theme.Space5, 0, 0, 0),
            };

            var bullet = new TextBlock
            {
                Text = marker,
                Margin = new Thickness(0, 0, theme.Space3, 0),
                FontSize = theme.BodySize,
                LineHeight = Math.Round(theme.BodySize * 1.55),
                Foreground = isTask && isChecked ? theme.Success : theme.TextMuted,
                MinWidth = 18,
                TextAlignment = list.IsOrdered ? TextAlignment.Right : TextAlignment.Left,
                TextTrimming = TextTrimming.None,
            };

            Grid.SetColumn(bullet, 0);
            row.Children.Add(bullet);

            var body = new StackPanel { Spacing = 0 };
            foreach (var inner in item)
            {
                if (inner is ListBlock nested)
                {
                    body.Children.Add(BuildList(nested, theme, depth + 1));
                }
                else
                {
                    AddBlock(body.Children, inner, theme);
                }
            }

            // A tight list has one paragraph per item; drop its trailing gap so rows sit together.
            if (!list.IsLoose && body.Children.Count > 0 && body.Children[^1] is TextBlock last)
            {
                last.Margin = new Thickness(0);
            }

            Grid.SetColumn(body, 1);
            row.Children.Add(body);
            stack.Children.Add(row);
        }

        return stack;
    }

    private static (string Marker, bool IsTask, bool IsChecked) ResolveMarker(
        ListBlock list,
        ListItemBlock item,
        ref int number)
    {
        // A task list marks itself with a TaskList inline at the head of the item's first paragraph.
        if (item.Count > 0 &&
            item[0] is ParagraphBlock { Inline: { } inline } &&
            inline.FirstChild is TaskList task)
        {
            return (task.Checked ? "☑" : "☐", true, task.Checked);
        }

        if (!list.IsOrdered)
        {
            return ("•", false, false);
        }

        var marker = $"{number}.";
        number++;
        return (marker, false, false);
    }

    private Control BuildQuote(QuoteBlock quote, Palette theme)
    {
        var body = new StackPanel { Spacing = 0 };
        foreach (var child in quote)
        {
            AddBlock(body.Children, child, theme);
        }

        if (body.Children.Count > 0 && body.Children[^1] is TextBlock last)
        {
            last.Margin = new Thickness(0);
        }

        return new Border
        {
            BorderBrush = theme.Accent,
            BorderThickness = new Thickness(2, 0, 0, 0),
            Background = theme.SurfaceRaised,
            Padding = new Thickness(theme.Space4, theme.Space3, theme.Space4, theme.Space3),
            Margin = new Thickness(0, 0, 0, theme.Space4),
            CornerRadius = new CornerRadius(0, theme.RadiusSm, theme.RadiusSm, 0),
            Child = body,
        };
    }

    private Control BuildCode(string code, string? language, Palette theme)
    {
        var body = new StackPanel { Spacing = theme.Space1 };

        if (!string.IsNullOrWhiteSpace(language))
        {
            body.Children.Add(new TextBlock
            {
                Text = language.Trim(),
                FontSize = theme.XsSize,
                FontFamily = theme.Mono,
                Foreground = theme.TextMuted,
                TextTrimming = TextTrimming.None,
            });
        }

        body.Children.Add(new TextBlock
        {
            Text = code.TrimEnd('\n', '\r'),
            FontFamily = theme.Mono,
            FontSize = theme.SmSize,
            Foreground = theme.TextPrimary,
            TextWrapping = TextWrapping.NoWrap,
            TextTrimming = TextTrimming.None,
            LineHeight = Math.Round(theme.SmSize * 1.5),
        });

        return new Border
        {
            Background = theme.SurfaceSunken,
            BorderBrush = theme.BorderSubtle,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(theme.RadiusMd),
            Padding = new Thickness(theme.Space4, theme.Space3, theme.Space4, theme.Space3),
            Margin = new Thickness(0, 0, 0, theme.Space4),
            Child = new ScrollViewer
            {
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Content = body,
            },
        };
    }

    private Control BuildTable(MdTable table, Palette theme)
    {
        var rows = table.OfType<MdTableRow>().ToList();
        if (rows.Count == 0)
        {
            return new Border();
        }

        var columns = rows.Max(r => r.Count);

        var grid = new Grid();
        for (var c = 0; c < columns; c++)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
        }

        for (var r = 0; r < rows.Count; r++)
        {
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));

            for (var c = 0; c < columns; c++)
            {
                var content = new StackPanel { Spacing = 0 };

                if (c < rows[r].Count && rows[r][c] is MdTableCell cell)
                {
                    foreach (var child in cell)
                    {
                        AddBlock(content.Children, child, theme);
                    }
                }

                if (content.Children.Count > 0 && content.Children[^1] is TextBlock last)
                {
                    last.Margin = new Thickness(0);
                    last.FontWeight = rows[r].IsHeader ? FontWeight.SemiBold : FontWeight.Normal;
                    last.Foreground = rows[r].IsHeader ? theme.TextSecondary : theme.TextPrimary;
                }

                var border = new Border
                {
                    // Hairlines shared between neighbours, so the grid never doubles up.
                    BorderBrush = theme.BorderSubtle,
                    BorderThickness = new Thickness(0, 0, c == columns - 1 ? 0 : 1, r == rows.Count - 1 ? 0 : 1),
                    Background = rows[r].IsHeader ? theme.SurfaceRaised : Brushes.Transparent,
                    Padding = new Thickness(theme.Space3, theme.Space2, theme.Space3, theme.Space2),
                    Child = content,
                };

                Grid.SetRow(border, r);
                Grid.SetColumn(border, c);
                grid.Children.Add(border);
            }
        }

        return new Border
        {
            BorderBrush = theme.BorderDefault,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(theme.RadiusMd),
            ClipToBounds = true,
            Margin = new Thickness(0, 0, 0, theme.Space4),
            HorizontalAlignment = HorizontalAlignment.Left,
            Child = new ScrollViewer
            {
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Content = grid,
            },
        };
    }

    // ---- inlines ---------------------------------------------------------------------------------

    private readonly record struct InlineStyle(FontWeight Weight, FontStyle Style, bool Strike, bool Code, bool Link);

    private void AppendInlines(InlineCollection target, ContainerInline container, Palette theme, InlineStyle style)
    {
        foreach (var inline in container)
        {
            switch (inline)
            {
                case LiteralInline literal:
                    Emit(target, literal.Content.ToString(), theme, style);
                    break;

                case CodeInline code:
                    Emit(target, code.Content, theme, style with { Code = true });
                    break;

                case EmphasisInline emphasis:
                    AppendInlines(target, emphasis, theme, Combine(style, emphasis));
                    break;

                case LinkInline { IsImage: true } image:
                    Emit(target, $"🖼 {DescribeImage(image)}", theme, style with { Code = false, Link = true });
                    break;

                case LinkInline link:
                    AppendInlines(target, link, theme, style with { Link = true });
                    break;

                case AutolinkInline auto:
                    Emit(target, auto.Url, theme, style with { Link = true });
                    break;

                case TaskList:
                    // Drawn as the list item's bullet instead.
                    break;

                case LineBreakInline lineBreak:
                    if (lineBreak.IsHard)
                    {
                        target.Add(new LineBreak());
                    }
                    else
                    {
                        Emit(target, " ", theme, style);
                    }

                    break;

                case HtmlEntityInline entity:
                    Emit(target, entity.Transcoded.ToString(), theme, style);
                    break;

                case HtmlInline:
                    // Raw tags are noise in a rendered view.
                    break;

                case ContainerInline nested:
                    AppendInlines(target, nested, theme, style);
                    break;

                case LeafInline leaf:
                    Emit(target, leaf.ToString() ?? string.Empty, theme, style);
                    break;
            }
        }
    }

    private static string DescribeImage(LinkInline image)
    {
        var label = image.FirstChild is LiteralInline literal ? literal.Content.ToString() : null;
        return string.IsNullOrWhiteSpace(label) ? image.Url ?? "image" : label;
    }

    private static InlineStyle Combine(InlineStyle style, EmphasisInline emphasis) => emphasis.DelimiterChar switch
    {
        '~' when emphasis.DelimiterCount == 2 => style with { Strike = true },
        '=' => style with { Code = true },
        _ when emphasis.DelimiterCount >= 2 => style with { Weight = FontWeight.Bold },
        _ => style with { Style = FontStyle.Italic },
    };

    private void Emit(InlineCollection target, string text, Palette theme, InlineStyle style)
    {
        if (text.Length == 0)
        {
            return;
        }

        var run = new Run(text)
        {
            FontWeight = style.Weight,
            FontStyle = style.Style,
        };

        if (style.Code)
        {
            run.FontFamily = theme.Mono;
            run.FontSize = theme.SmSize;
            run.Foreground = theme.AiAccent;
            run.Background = theme.SurfaceRaised;
        }

        if (style.Link)
        {
            run.Foreground = theme.Accent;
        }

        if (style.Strike)
        {
            run.TextDecorations = TextDecorations.Strikethrough;
            run.Foreground = theme.TextMuted;
        }
        else if (style.Link)
        {
            run.TextDecorations = TextDecorations.Underline;
        }

        target.Add(run);
    }

    // ---- tokens ------------------------------------------------------------------------------------

    private IBrush Brush(string key, Color fallback) =>
        Lookup(key) as IBrush ?? new SolidColorBrush(fallback);

    private double Size(string key, double fallback) =>
        Lookup(key) is double value ? value : fallback;

    private object? Lookup(string key)
    {
        if (this.TryFindResource(key, out var local) && local is not null)
        {
            return local;
        }

        return Application.Current is { } app && app.TryFindResource(key, out var global) ? global : null;
    }

    /// <summary>The token values this render needs, resolved once instead of per node.</summary>
    private sealed class Palette
    {
        public Palette(MarkdownPreview owner)
        {
            TextPrimary = owner.Brush("TextPrimary", Color.Parse("#E6E9EF"));
            TextSecondary = owner.Brush("TextSecondary", Color.Parse("#A2ABBC"));
            TextMuted = owner.Brush("TextMuted", Color.Parse("#6B7688"));
            Accent = owner.Brush("AccentBase", Color.Parse("#4C8DFF"));
            AiAccent = owner.Brush("AiAccent", Color.Parse("#B57CFF"));
            Success = owner.Brush("SuccessBase", Color.Parse("#3FB950"));
            Danger = owner.Brush("DangerBase", Color.Parse("#F05D5D"));
            SurfaceRaised = owner.Brush("SurfaceRaised", Color.Parse("#1B1F27"));
            SurfaceSunken = owner.Brush("SurfaceSunken", Color.Parse("#0F1115"));
            BorderSubtle = owner.Brush("BorderSubtle", Color.Parse("#232833"));
            BorderDefault = owner.Brush("BorderDefault", Color.Parse("#2E3542"));

            XsSize = owner.Size("FontSizeXs", 11);
            SmSize = owner.Size("FontSizeSm", 12);
            BodySize = owner.Size("FontSizeMd", 13);
            LgSize = owner.Size("FontSizeLg", 15);
            XlSize = owner.Size("FontSizeXl", 18);
            DisplaySize = owner.Size("FontSizeDisplay", 22);

            Space1 = owner.Size("Space1", 2);
            Space2 = owner.Size("Space2", 4);
            Space3 = owner.Size("Space3", 8);
            Space4 = owner.Size("Space4", 12);
            Space5 = owner.Size("Space5", 16);

            RadiusSm = owner.Lookup("RadiusSm") is CornerRadius sm ? sm.TopLeft : 3;
            RadiusMd = owner.Lookup("RadiusMd") is CornerRadius md ? md.TopLeft : 5;

            Mono = owner.Lookup("FontFamilyMono") as FontFamily
                   ?? new FontFamily("Cascadia Code,Consolas,monospace");
        }

        public IBrush TextPrimary { get; }

        public IBrush TextSecondary { get; }

        public IBrush TextMuted { get; }

        public IBrush Accent { get; }

        public IBrush AiAccent { get; }

        public IBrush Success { get; }

        public IBrush Danger { get; }

        public IBrush SurfaceRaised { get; }

        public IBrush SurfaceSunken { get; }

        public IBrush BorderSubtle { get; }

        public IBrush BorderDefault { get; }

        public double XsSize { get; }

        public double SmSize { get; }

        public double BodySize { get; }

        public double LgSize { get; }

        public double XlSize { get; }

        public double DisplaySize { get; }

        public double Space1 { get; }

        public double Space2 { get; }

        public double Space3 { get; }

        public double Space4 { get; }

        public double Space5 { get; }

        public double RadiusSm { get; }

        public double RadiusMd { get; }

        public FontFamily Mono { get; }
    }
}
