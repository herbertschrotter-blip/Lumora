using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Lumora.Lightbox.Controls;

namespace Lumora.Merge;

/// <summary>
/// Ebene über der Bühne des Fensters „Zusammenfügen“ (Mockup 28, Ansicht 5): das ungeschnittene Panorama liegt im
/// <see cref="ZoomPanControl"/> (Zoom/Verschieben), darüber dunkelt diese Ebene alles außerhalb des Zuschnitts ab, zeichnet die
/// getönte Vorschau in den Zuschnitt (der Regler „Farbe kräftiger“ wirkt sofort) und den Rahmen. Die Lücken sind im
/// Rahmenbild schon schraffiert. Kein Treffer-Test: Mausrad und Ziehen gehen an das Bild.
/// </summary>
public sealed class MergeStageOverlay : FrameworkElement, IZoomPanOverlay
{
    public static readonly DependencyProperty FrameWidthProperty = DependencyProperty.Register(
        nameof(FrameWidth), typeof(int), typeof(MergeStageOverlay), new FrameworkPropertyMetadata(0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty CropProperty = DependencyProperty.Register(
        nameof(Crop), typeof(Int32Rect), typeof(MergeStageOverlay), new FrameworkPropertyMetadata(Int32Rect.Empty, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty CroppedImageProperty = DependencyProperty.Register(
        nameof(CroppedImage), typeof(BitmapSource), typeof(MergeStageOverlay), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ShowFrameProperty = DependencyProperty.Register(
        nameof(ShowFrame), typeof(bool), typeof(MergeStageOverlay), new FrameworkPropertyMetadata(true, FrameworkPropertyMetadataOptions.AffectsRender));

    private static readonly Brush DimBrush = Freeze(new SolidColorBrush(Color.FromArgb(0x99, 0, 0, 0)));
    private static readonly Pen FramePen = Freeze(new Pen(new SolidColorBrush(Color.FromRgb(0x81, 0x8C, 0xF8)), 2) { DashStyle = DashStyles.Solid });
    private Rect _imageRect = Rect.Empty;

    public MergeStageOverlay()
    {
        IsHitTestVisible = false;
    }

    /// <summary>Breite des Rahmenbilds in Pixeln (Maßstab zwischen Bildrechteck und Zuschnitt).</summary>
    public int FrameWidth
    {
        get => (int)GetValue(FrameWidthProperty);
        set => SetValue(FrameWidthProperty, value);
    }

    /// <summary>Zuschnitt in Pixeln des Rahmenbilds.</summary>
    public Int32Rect Crop
    {
        get => (Int32Rect)GetValue(CropProperty);
        set => SetValue(CropProperty, value);
    }

    /// <summary>Getönte Vorschau (genau der Zuschnitt).</summary>
    public BitmapSource? CroppedImage
    {
        get => (BitmapSource?)GetValue(CroppedImageProperty);
        set => SetValue(CroppedImageProperty, value);
    }

    /// <summary>false: nur die Vorschau im Zuschnitt, ohne Abdunkeln und Rahmen.</summary>
    public bool ShowFrame
    {
        get => (bool)GetValue(ShowFrameProperty);
        set => SetValue(ShowFrameProperty, value);
    }

    public void UpdateImageRect(Rect imageRect)
    {
        _imageRect = imageRect;
        InvalidateVisual();
    }

    /// <summary>Lage des Zuschnitts in Koordinaten der Ebene (Tests).</summary>
    internal Rect CropRect
    {
        get
        {
            if (_imageRect.IsEmpty || FrameWidth <= 0 || Crop.IsEmpty)
            {
                return Rect.Empty;
            }

            var scale = _imageRect.Width / FrameWidth;
            return new Rect(_imageRect.X + Crop.X * scale, _imageRect.Y + Crop.Y * scale, Crop.Width * scale, Crop.Height * scale);
        }
    }

    protected override void OnRender(DrawingContext dc)
    {
        var crop = CropRect;
        if (crop.IsEmpty)
        {
            return;
        }

        dc.PushClip(new RectangleGeometry(new Rect(RenderSize)));
        if (ShowFrame)
        {
            var dim = new CombinedGeometry(GeometryCombineMode.Exclude, new RectangleGeometry(_imageRect), new RectangleGeometry(crop));
            dc.DrawGeometry(DimBrush, null, dim);
        }
        else
        {
            dc.DrawRectangle(Brushes.Black, null, _imageRect);
        }

        if (CroppedImage is { } image)
        {
            dc.DrawImage(image, crop);
        }

        if (ShowFrame)
        {
            dc.DrawRectangle(null, FramePen, crop);
        }

        dc.Pop();
    }

    private static T Freeze<T>(T freezable)
        where T : Freezable
    {
        freezable.Freeze();
        return freezable;
    }
}
