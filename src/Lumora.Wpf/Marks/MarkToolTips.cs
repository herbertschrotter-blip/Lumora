using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using Lumora.Gallery.Tags;
using Lumora.Services.Tags;

namespace Lumora.Marks;

/// <summary>Reine Text-Regeln der Tag-/Kategorie-Marken (Mockup 16) — ohne Oberfläche testbar.</summary>
public static class MarkTexts
{
    /// <summary>Höchstens so viele Farbpunkte zeigt die Tag-Marke.</summary>
    public const int MaxDots = 3;

    /// <summary>Höchstens so viele Chips je Abschnitt im Tooltip (Rest als „+ n weitere“).</summary>
    public const int MaxToolTipChips = 12;

    /// <summary>Anzahl auf der Marke erst ab 2 (eine einzelne zeigt nur das Symbol).</summary>
    public static string CountText(int count) => count >= 2 ? count.ToString("N0", CultureInfo.CurrentCulture) : string.Empty;

    /// <summary>Zahl neben dem Ordnernamen (immer, sobald &gt; 0).</summary>
    public static string FolderCountText(int count) => count > 0 ? count.ToString("N0", CultureInfo.CurrentCulture) : string.Empty;

    /// <summary>„86 von 212 Fotos“ (Ordnerbaum-Tooltip).</summary>
    public static string OfFiles(int count, int total)
        => $"{count.ToString("N0", CultureInfo.CurrentCulture)} von {total.ToString("N0", CultureInfo.CurrentCulture)} {(total == 1 ? "Foto" : "Fotos")}";
}

/// <summary>Ein Chip im Tooltip: Name, optional mit Farbpunkt (Tag-Farbe als Hex).</summary>
public readonly record struct MarkChip(string Name, string? Color = null);

/// <summary>
/// Inhalt eines Marken-Tooltips: je Abschnitt (Tags, Kategorien) eine Überschrift und Chips. Ein Abschnitt erscheint, wenn
/// <see cref="ShowTags"/> bzw. <see cref="ShowCategories"/> gilt — auch ohne auflösbare Namen (dann nur die Überschrift).
/// </summary>
public sealed record MarkToolTipContent(
    string TagsHeader,
    IReadOnlyList<MarkChip> Tags,
    string CategoriesHeader,
    IReadOnlyList<MarkChip> Categories,
    bool ShowTags,
    bool ShowCategories)
{
    public bool IsEmpty => !ShowTags && !ShowCategories;

    /// <summary>Kachel/Collage: alle Tags mit Farbpunkt, gesetzte Kategorien als „Kategorie: Wert“.</summary>
    public static MarkToolTipContent ForFile(IReadOnlyList<TagInfo> tags, int categoryCount, IReadOnlyList<string> categoryLabels)
        => new(
            "Tags",
            tags.Select(t => new MarkChip(t.Name, t.Color)).ToList(),
            "Kategorien",
            categoryLabels.Select(l => new MarkChip(l)).ToList(),
            tags.Count > 0,
            categoryCount > 0);
}

/// <summary>
/// Baut den Tooltip der Marken als WPF-Inhalt — erst beim Öffnen (<c>ToolTipOpening</c>), damit 500 Kacheln einer Seite
/// keine Tooltip-Bäume vorhalten. Farben über DynamicResource (hell/dunkel).
/// </summary>
public static class MarkToolTips
{
    /// <summary>Platzhalter, damit WPF <c>ToolTipOpening</c> auslöst; wird dort durch den echten Inhalt ersetzt.</summary>
    public const string Placeholder = "Tags und Kategorien";

    public static FrameworkElement Build(MarkToolTipContent content)
    {
        var panel = new StackPanel { MaxWidth = 320 };
        if (content.ShowTags)
        {
            AddSection(panel, content.TagsHeader, content.Tags);
        }

        if (content.ShowCategories)
        {
            AddSection(panel, content.CategoriesHeader, content.Categories);
        }

        return panel;
    }

    private static void AddSection(Panel panel, string header, IReadOnlyList<MarkChip> chips)
    {
        var title = new TextBlock
        {
            Text = header,
            FontSize = 11,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, panel.Children.Count == 0 ? 0 : 6, 0, 2),
        };
        title.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorTertiaryBrush");
        panel.Children.Add(title);
        if (chips.Count == 0)
        {
            return;
        }

        var wrap = new WrapPanel();
        foreach (var chip in chips.Take(MarkTexts.MaxToolTipChips))
        {
            wrap.Children.Add(CreateChip(chip.Name, chip.Color));
        }

        if (chips.Count > MarkTexts.MaxToolTipChips)
        {
            wrap.Children.Add(CreateChip($"+ {chips.Count - MarkTexts.MaxToolTipChips} weitere", null));
        }

        panel.Children.Add(wrap);
    }

    private static Border CreateChip(string name, string? color)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        if (color is not null)
        {
            row.Children.Add(new Ellipse
            {
                Width = 8,
                Height = 8,
                Margin = new Thickness(0, 0, 5, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Fill = TagBrushes.ForHex(color),
            });
        }

        var text = new TextBlock { Text = name, FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
        text.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorPrimaryBrush");
        row.Children.Add(text);
        var border = new Border
        {
            CornerRadius = new CornerRadius(9),
            Padding = new Thickness(8, 1, 8, 1),
            Margin = new Thickness(0, 2, 4, 2),
            Child = row,
        };
        border.SetResourceReference(Border.BackgroundProperty, "ControlFillColorSecondaryBrush");
        return border;
    }

    /// <summary>Alle Texte eines gebauten Tooltips (Tests).</summary>
    internal static IEnumerable<string> Texts(DependencyObject root)
    {
        if (root is TextBlock block)
        {
            yield return block.Text;
        }

        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
        {
            foreach (var text in Texts(child))
            {
                yield return text;
            }
        }
    }

    /// <summary>Farbe eines Farbpunkts (Tests).</summary>
    internal static IEnumerable<Color> DotColors(DependencyObject root)
    {
        if (root is Ellipse { Fill: SolidColorBrush brush })
        {
            yield return brush.Color;
        }

        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
        {
            foreach (var color in DotColors(child))
            {
                yield return color;
            }
        }
    }
}
