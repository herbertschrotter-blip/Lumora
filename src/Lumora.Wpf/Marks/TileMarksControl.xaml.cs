using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Shapes;
using Lumora.Gallery.Tags;
using Lumora.Gallery.ViewModels;
using Lumora.Services.Tags;

namespace Lumora.Marks;

/// <summary>
/// Tag- und Kategorie-Marke einer Galerie-Kachel (Mockup 16). Sichtbar nur, wenn <see cref="ShowMarks"/> gilt (Schalter
/// „Auf Galerie-Kacheln“) und die Datei Tags oder Kategorie-Werte hat. Die Tag-Marke zeigt bis zu
/// <see cref="MarkTexts.MaxDots"/> Farbpunkte, beide Marken die Anzahl ab 2. Der Tooltip mit den Namen wird erst in
/// <see cref="OnToolTipOpening"/> gebaut.
/// </summary>
public partial class TileMarksControl : UserControl
{
    public static readonly DependencyProperty TagsProperty = DependencyProperty.Register(
        nameof(Tags), typeof(IReadOnlyList<TagInfo>), typeof(TileMarksControl), new PropertyMetadata(null, OnInputChanged));

    public static readonly DependencyProperty CategoriesProperty = DependencyProperty.Register(
        nameof(Categories), typeof(GalleryItemCategories), typeof(TileMarksControl), new PropertyMetadata(null, OnInputChanged));

    public static readonly DependencyProperty ShowMarksProperty = DependencyProperty.Register(
        nameof(ShowMarks), typeof(bool), typeof(TileMarksControl), new PropertyMetadata(true, OnInputChanged));

    private readonly Ellipse[] _dots;

    public TileMarksControl()
    {
        InitializeComponent();
        _dots = [Dot1, Dot2, Dot3];
    }

    /// <summary>Tags der Datei in Anzeigereihenfolge (<see cref="GalleryItem.Tags"/>).</summary>
    public IReadOnlyList<TagInfo>? Tags
    {
        get => (IReadOnlyList<TagInfo>?)GetValue(TagsProperty);
        set => SetValue(TagsProperty, value);
    }

    /// <summary>Kategorie-Werte der Datei (<see cref="GalleryItem.Categories"/>).</summary>
    public GalleryItemCategories? Categories
    {
        get => (GalleryItemCategories?)GetValue(CategoriesProperty);
        set => SetValue(CategoriesProperty, value);
    }

    /// <summary>Schalter „Auf Galerie-Kacheln“ (Einstellungen › Darstellung); aus → nie sichtbar.</summary>
    public bool ShowMarks
    {
        get => (bool)GetValue(ShowMarksProperty);
        set => SetValue(ShowMarksProperty, value);
    }

    /// <summary>Tag-Marke sichtbar (Tests).</summary>
    internal bool HasTagMark => TagMark.Visibility == Visibility.Visible;

    /// <summary>Kategorie-Marke sichtbar (Tests).</summary>
    internal bool HasCategoryMark => CategoryMark.Visibility == Visibility.Visible;

    /// <summary>Anzahl auf der Tag-Marke („“ bei einem Tag).</summary>
    internal string TagCountText => TagCount.Text;

    /// <summary>Anzahl auf der Kategorie-Marke („“ bei einer Kategorie).</summary>
    internal string CategoryCountText => CategoryCount.Text;

    /// <summary>Sichtbare Farbpunkte (Tests).</summary>
    internal int VisibleDots
    {
        get
        {
            var count = 0;
            foreach (var dot in _dots)
            {
                count += dot.Visibility == Visibility.Visible ? 1 : 0;
            }

            return count;
        }
    }

    private static void OnInputChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) => ((TileMarksControl)d).Update();

    private void Update()
    {
        var tags = Tags ?? Array.Empty<TagInfo>();
        var categoryCount = Categories?.Count ?? 0;

        TagMark.Visibility = tags.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        for (var i = 0; i < _dots.Length; i++)
        {
            if (i < tags.Count)
            {
                _dots[i].Fill = TagBrushes.ForHex(tags[i].Color);
                _dots[i].Visibility = Visibility.Visible;
            }
            else
            {
                _dots[i].Visibility = Visibility.Collapsed;
            }
        }

        TagCount.Text = MarkTexts.CountText(tags.Count);
        TagCount.Visibility = TagCount.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

        CategoryMark.Visibility = categoryCount > 0 ? Visibility.Visible : Visibility.Collapsed;
        CategoryMark.Margin = new Thickness(tags.Count > 0 ? 4 : 0, 0, 0, 0);
        CategoryCount.Text = MarkTexts.CountText(categoryCount);
        CategoryCount.Visibility = CategoryCount.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

        var visible = ShowMarks && (tags.Count > 0 || categoryCount > 0);
        Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        ToolTip = visible ? MarkToolTips.Placeholder : null;
    }

    /// <summary>Inhalt des Tooltips aus dem aktuellen Stand (Tests).</summary>
    internal MarkToolTipContent BuildToolTipContent()
        => MarkToolTipContent.ForFile(Tags ?? Array.Empty<TagInfo>(), Categories?.Count ?? 0, Categories?.Labels ?? Array.Empty<string>());

    protected override void OnToolTipOpening(ToolTipEventArgs e)
    {
        var content = BuildToolTipContent();
        if (content.IsEmpty)
        {
            e.Handled = true;
            return;
        }

        ToolTip = MarkToolTips.Build(content);
        base.OnToolTipOpening(e);
    }
}
