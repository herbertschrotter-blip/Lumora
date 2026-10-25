using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using Lumora.Services.Categories;
using Lumora.Services.Database;
using Lumora.Services.Tags;

namespace Lumora.PhotoMenu;

/// <summary>
/// Fenster „Eigenschaften“ einer Datei (ADR-0068, Alt+Enter im Rechtsklick-Menü): Datei- und EXIF-Angaben, nur lesend.
/// Nicht modal — mehrere Fenster für verschiedene Fotos sind möglich. Esc und Enter schließen.
/// </summary>
public sealed partial class PhotoPropertiesWindow : Lumora.Shell.Dialogs.AppFluentWindow
{
    public PhotoPropertiesWindow(PhotoProperties properties)
    {
        InitializeComponent();
        Properties = properties;
        DataContext = properties;
    }

    public PhotoProperties Properties { get; }

    /// <summary>Liest die Angaben und zeigt das Fenster; null-Ergebnis (Datei nicht im Katalog) zeigt nichts.</summary>
    public static async Task ShowAsync(ILibraryContextFactory factory, ITagService? tags, ICategoryService? categories, int fileId, Window? owner)
    {
        var properties = await PhotoPropertiesReader.ReadAsync(factory, tags, categories, fileId).ConfigureAwait(true);
        if (properties is null)
        {
            return;
        }

        var window = new PhotoPropertiesWindow(properties);
        if (owner is { IsVisible: true })
        {
            window.Owner = owner;
            window.WindowStartupLocation = WindowStartupLocation.CenterOwner;
        }
        else
        {
            window.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }

        window.Show();
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key is Key.Escape)
        {
            e.Handled = true;
            Close();
        }
    }
}
