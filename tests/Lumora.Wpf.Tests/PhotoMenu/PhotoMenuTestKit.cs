using System.Reflection;
using System.Windows.Controls;
using Microsoft.Extensions.Logging.Abstractions;
using Lumora.Domain.Enums;
using Lumora.PhotoMenu;
using Lumora.Services.Catalog;
using Lumora.Services.Categories;
using Lumora.Services.Covers;
using Lumora.Services.Export;
using Lumora.Services.Faces.Assignment;
using Lumora.Services.Operations;
using Lumora.Services.Related;
using Lumora.Services.Rotation;
using Lumora.Services.Tags;
using Lumora.Wpf.Tests.Fixtures;
using Lumora.Wpf.Tests.Shell;

namespace Lumora.Wpf.Tests.PhotoMenu;

/// <summary>Dienst-Fake mit einzelnen Antworten je Methodenname; alles andere wie <see cref="NullService"/>.</summary>
public class ScriptedService : NullService
{
    public Dictionary<string, Func<object?[], object?>> Handlers { get; } = new();

    public List<string> Calls { get; } = new();

    public static new (T Service, ScriptedService Script) Create<T>()
        where T : class
    {
        var service = DispatchProxy.Create<T, ScriptedService>();
        return (service, (ScriptedService)(object)service);
    }

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        var name = targetMethod!.Name.StartsWith("get_", StringComparison.Ordinal) ? targetMethod.Name[4..] : targetMethod.Name;
        lock (Calls)
        {
            Calls.Add(name);
        }

        return Handlers.TryGetValue(name, out var handler) ? handler(args ?? []) : base.Invoke(targetMethod, args);
    }
}

/// <summary>Gemeinsame Bausteine der Menü-Tests (ADR-0068).</summary>
internal static class PhotoMenuTestKit
{
    public static PhotoMenuTarget Jpeg(int id = 7, bool missing = false) => new(id, "urlaub.jpg", @"C:\Bibliothek\Reise\urlaub.jpg", ".jpg", MediaType.Image, missing);

    public static PhotoMenuTarget Png(int id = 8) => new(id, "grafik.png", @"C:\Bibliothek\Reise\grafik.png", ".png", MediaType.Image, false);

    public static PhotoMenuTarget Zip(int id = 9) => new(id, "paket.zip", @"C:\Bibliothek\paket.zip", ".zip", MediaType.Archive, false);

    /// <summary>Aktionen mit allen Diensten (Fakes); <paramref name="blocked"/> = versteckte Sitzung.</summary>
    public static PhotoActions Actions(
        bool blocked = false,
        ICoverService? covers = null,
        ITagService? tags = null,
        ScriptedDialogs? dialogs = null,
        IRelatedPhotoSearch? related = null)
    {
        var (export, exportScript) = ScriptedService.Create<IPhotoExportService>();
        exportScript.Handlers["IsBlocked"] = _ => blocked;
        return new PhotoActions(
            dialogs ?? new ScriptedDialogs(),
            NullLogger<PhotoActions>.Instance,
            tags: tags ?? NullService.Create<ITagService>(),
            categories: NullService.Create<ICategoryService>(),
            catalog: NullService.Create<ICatalogUpdateService>(),
            covers: covers ?? NullService.Create<ICoverService>(),
            rotation: NullService.Create<IPhotoRotationService>(),
            export: export,
            fileOps: NullService.Create<IFileOperationService>(),
            faces: NullService.Create<IFaceRecognitionService>(),
            related: related ?? NullService.Create<IRelatedPhotoSearch>());
    }

    /// <summary>Ort mit allen Galerie-Aktionen; <paramref name="status"/> sammelt Meldungen.</summary>
    public static PhotoMenuHost GalleryHost(List<string>? status = null, bool search = true) => new()
    {
        Owner = () => null,
        ShowStatus = text => status?.Add(text),
        Open = () => { },
        Slideshow = () => { },
        ShowArchiveContent = () => { },
        ExtractArchive = () => { },
        ArchiveIntoLibrary = () => { },
        EditTags = () => { },
        EditCategories = () => { },
        FillAlbums = items => items.Add(new MenuItem { Header = "Zu Album hinzufügen" }),
        FillStories = items => items.Add(new MenuItem { Header = "Als Story…" }),
        SetDate = () => { },
        SetPlace = () => { },
        ShowFaces = () => { },
        ApplySearch = search ? _ => Task.CompletedTask : null,
        MoveTo = () => { },
        Recycle = () => { },
    };

    /// <summary>Kopfzeilen der Ebene („—“ für Trenner).</summary>
    public static List<string> Headers(ItemCollection items)
        => items.Cast<object>().Select(i => i is Separator ? "—" : (string)((MenuItem)i).Header).ToList();

    public static MenuItem Item(ItemCollection items, string header)
        => items.OfType<MenuItem>().Single(i => (string)i.Header == header);
}
