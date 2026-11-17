using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Layout;
using Avalonia.Media;

namespace Stramp.App.Controls;

/// <summary>
/// A tiny markdown renderer
/// </summary>
public class MarkdownView : ContentControl
{
    public static readonly StyledProperty<string?> MarkdownProperty =
        AvaloniaProperty.Register<MarkdownView, string?>(nameof(Markdown));

    static MarkdownView()
    {
        MarkdownProperty.Changed.AddClassHandler<MarkdownView>((v, _) => v.Rebuild());
    }

    public string? Markdown
    {
        get => GetValue(MarkdownProperty);
        set => SetValue(MarkdownProperty, value);
    }

    private static readonly IBrush CodeBackground = new SolidColorBrush(Color.FromArgb(40, 128, 128, 128));

    private void Rebuild()
    {
        var panel = new StackPanel { Spacing = 4 };
        var lines = (Markdown ?? "").Replace("\r\n", "\n").Split('\n');

        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            var trimmed = line.TrimStart();

            if (trimmed.StartsWith("```"))
            {
                var code = new List<string>();
                for (i++; i < lines.Length && !lines[i].TrimStart().StartsWith("```"); i++)
                    code.Add(lines[i]);

                panel.Children.Add(new Border
                {
                    Background = CodeBackground,
                    CornerRadius = new CornerRadius(4),
                    Padding = new Thickness(8, 6),
                    Child = new TextBlock
                    {
                        Text = string.Join('\n', code),
                        FontFamily = new FontFamily("Consolas, Cascadia Mono, monospace"),
                        TextWrapping = TextWrapping.Wrap,
                    },
                });
            }
            else if (trimmed.Length == 0)
            {
                continue;
            }
            else if (trimmed[0] == '#')
            {
                var level = trimmed.TakeWhile(c => c == '#').Count();
                if (level <= 6 && trimmed.Length > level && trimmed[level] == ' ')
                {
                    var block = Inline(trimmed[(level + 1)..].Trim());
                    block.FontWeight = FontWeight.Bold;
                    block.FontSize = FontSize * (level switch { 1 => 1.6, 2 => 1.4, 3 => 1.2, _ => 1.05 });
                    block.Margin = new Thickness(0, level <= 2 ? 8 : 6, 0, 2);
                    panel.Children.Add(block);
                    continue;
                }
                panel.Children.Add(Inline(trimmed));
            }
            else if (trimmed.Length > 1 && trimmed[0] is '-' or '*' or '+' && trimmed[1] == ' ')
            {
                var indent = (line.Length - trimmed.Length) / 2;
                var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), Margin = new Thickness(8 + indent * 16, 0, 0, 0) };
                row.Children.Add(new TextBlock { Text = indent > 0 ? "◦ " : "• ", VerticalAlignment = VerticalAlignment.Top });
                var text = Inline(trimmed[2..].Trim());
                Grid.SetColumn(text, 1);
                row.Children.Add(text);
                panel.Children.Add(row);
            }
            else
            {
                panel.Children.Add(Inline(trimmed));
            }
        }

        Content = panel;
    }

    /// <summary>Builds a wrapping TextBlock, turning `code` and **bold** spans into runs.</summary>
    private static TextBlock Inline(string text)
    {
        var tb = new TextBlock { TextWrapping = TextWrapping.Wrap };
        var plain = new System.Text.StringBuilder();

        void Flush()
        {
            if (plain.Length > 0) tb.Inlines!.Add(new Run(plain.ToString()));
            plain.Clear();
        }

        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '`' && text.IndexOf('`', i + 1) is var end && end > i)
            {
                Flush();
                tb.Inlines!.Add(new Run(text[(i + 1)..end])
                {
                    FontFamily = new FontFamily("Consolas, Cascadia Mono, monospace"),
                    Background = CodeBackground,
                });
                i = end;
            }
            else if (text.AsSpan(i).StartsWith("**") && text.IndexOf("**", i + 2, StringComparison.Ordinal) is var bEnd && bEnd > i + 2)
            {
                Flush();
                tb.Inlines!.Add(new Run(text[(i + 2)..bEnd]) { FontWeight = FontWeight.Bold });
                i = bEnd + 1;
            }
            else plain.Append(text[i]);
        }
        Flush();
        return tb;
    }
}
