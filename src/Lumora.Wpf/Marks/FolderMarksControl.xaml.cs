using System.Windows;
using System.Windows.Controls;

namespace Lumora.Marks;

/// <summary>
/// Tag-/Kategorie-Zahlen neben dem Ordnernamen (Mockup 16). <see cref="Marks"/> null (keine Datei mit Tags/Kategorien oder
/// Schalter „Im Ordnerbaum“ aus) → unsichtbar. Tooltip wird in <see cref="OnToolTipOpening"/> gebaut.
/// </summary>
public partial class FolderMarksControl : UserControl
{
    public static readonly DependencyProperty MarksProperty = DependencyProperty.Register(
        nameof(Marks), typeof(FolderMarkSummary), typeof(FolderMarksControl), new PropertyMetadata(null, OnMarksChanged));

    public FolderMarksControl()
    {
        InitializeComponent();
    }

    public FolderMarkSummary? Marks
    {
        get => (FolderMarkSummary?)GetValue(MarksProperty);
        set => SetValue(MarksProperty, value);
    }

    /// <summary>Angezeigte Zahl der Fotos mit Tags („“ = ausgeblendet; Tests).</summary>
    internal string TagCountText => TagPart.Visibility == Visibility.Visible ? TagCount.Text : string.Empty;

    /// <summary>Angezeigte Zahl der Fotos mit Kategorien („“ = ausgeblendet; Tests).</summary>
    internal string CategoryCountText => CategoryPart.Visibility == Visibility.Visible ? CategoryCount.Text : string.Empty;

    private static void OnMarksChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) => ((FolderMarksControl)d).Update();

    private void Update()
    {
        var marks = Marks;
        var hasTags = marks?.HasTags == true;
        var hasCategories = marks?.HasCategories == true;
        TagPart.Visibility = hasTags ? Visibility.Visible : Visibility.Collapsed;
        TagCount.Text = marks?.TagCountText ?? string.Empty;
        CategoryPart.Visibility = hasCategories ? Visibility.Visible : Visibility.Collapsed;
        CategoryPart.Margin = new Thickness(hasTags ? 6 : 0, 0, 0, 0);
        CategoryCount.Text = marks?.CategoryCountText ?? string.Empty;
        var visible = hasTags || hasCategories;
        Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        ToolTip = visible ? MarkToolTips.Placeholder : null;
    }

    protected override void OnToolTipOpening(ToolTipEventArgs e)
    {
        if (Marks is not { } marks)
        {
            e.Handled = true;
            return;
        }

        ToolTip = MarkToolTips.Build(marks.BuildToolTipContent());
        base.OnToolTipOpening(e);
    }
}
