using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Lumora.Gallery.ArchiveTarget;
using Lumora.Gallery.Archives;
using Lumora.Gallery.Categories;
using Lumora.Gallery.Data;
using Lumora.Gallery.ListView;
using Lumora.Gallery.Services;
using Lumora.Gallery.Tags;
using Lumora.Gallery.ViewModels;
using Lumora.Gallery.Views;
using Lumora.Services.Operations;

namespace Lumora.Gallery;

/// <summary>
/// DI-Registrierung des Galerie-Moduls (Wpf/Gallery): Raw-SQLite-Lesepfad, ViewModel (Singleton — Galerie-Seite
/// und Ordner-Seite teilen den Zustand), View (transient, je Seite eine Instanz), Dialog- und
/// Ordnerauswahl-Dienste sowie „Ins Archiv…“ (<see cref="IArchiveMoveService"/> für Galerie und Lightbox, ADR-0020).
/// Letztere per TryAdd, damit der Rauchtest (SmokeTestRunner.ConfigureServices) seine Fakes vorher setzen kann.
/// Wird in App.xaml.cs mit einer Zeile aufgerufen.
/// </summary>
public static class GalleryServiceCollectionExtensions
{
    public static IServiceCollection AddGallery(this IServiceCollection services)
    {
        services.AddSingleton<GalleryPageSource>();
        services.TryAddSingleton<ISharedGalleryFilter, SharedGalleryFilter>(); // EIN Filter für Galerie, Ordner, Orte (ADR-0055)
        services.AddSingleton<GalleryViewModel>();
        services.AddTransient<GalleryView>();
        services.TryAddSingleton<IGalleryDialogService, GalleryDialogService>();
        services.TryAddSingleton<IFolderPickerService, FolderPickerService>();
        services.AddGalleryArchives();
        services.TryAddSingleton<IArchiveTargetPicker, ArchiveTargetPicker>();
        services.TryAddSingleton<IArchiveMoveService, ArchiveMoveService>();
        // Tags (ADR-0021) und Listenansicht: Tag-Auswahl-Dialog, Ansichts-Einstellungen (Grid/Liste, Spalten)
        services.TryAddSingleton<ITagPickerService, TagPickerService>();
        services.TryAddSingleton<ICategoryPickerService, CategoryPickerService>(); // Strg+K (ADR-0027)
        services.TryAddSingleton<IGalleryViewSettingsStore, GalleryViewSettingsStore>();
        Lumora.Marks.MarksUiServiceCollectionExtensions.AddMarksUi(services); // Tag-/Kategorie-Symbole: Schalter der Sitzung (Mockup 16)
        return services;
    }
}
