using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Microsoft.Extensions.Logging;
using Lumora.Gallery.Services;
using Lumora.Services.Catalog;
using Lumora.Services.Categories;
using Lumora.Services.Covers;
using Lumora.Services.Database;
using Lumora.Services.Export;
using Lumora.Services.Faces.Assignment;
using Lumora.Services.Library;
using Lumora.Services.Motifs;
using Lumora.Services.Operations;
using Lumora.Services.Related;
using Lumora.Services.Rotation;
using Lumora.Services.Tags;
using Lumora.Shell;
using Lumora.Shell.HiddenLibraries;

namespace Lumora.PhotoMenu;

/// <summary>Tag mit Zustand über die betroffenen Dateien: true = alle, null = teilweise, false = keine.</summary>
public sealed record TagMenuState(TagInfo Tag, bool? State);

/// <summary>Kategorie mit ihren Werten und dem Zustand je Wert (true = alle, null = teilweise, false = keine).</summary>
public sealed record CategoryMenuState(CategoryInfo Category, IReadOnlyList<(CategoryValueInfo Value, bool? State)> Values);

/// <summary>
/// Gemeinsame Aktionen des Rechtsklick-Menüs (ADR-0068, Mockup 17) für Galerie, Liste und Lightbox — WPF-nah, aber ohne Seite:
/// arbeitet nur mit FileIds über die Dienste (Tags, Kategorien, Bewertung, Gesichter, Titelbild, Drehen, Kopieren nach…,
/// Exportieren, Zwischenablage, Explorer, Eigenschaften, „Suchen ▸“). Ergebnisse kommen als kurze Texte zurück, die der Ort selbst
/// anzeigt (Galerie: Ergebniszeile, Lightbox: Statuszeile). Die Dienste senden ihre Nachrichten; Galerie und Lightbox aktualisieren
/// sich darüber. In der versteckten Sitzung gibt es keinen Weg nach außen (ADR-0049).
/// </summary>
public sealed class PhotoActions
{
    private const int ChunkSize = 500;

    private readonly IDialogService _dialogs;
    private readonly ILogger<PhotoActions> _logger;
    private readonly ILibraryContextFactory? _factory;
    private readonly ITagService? _tags;
    private readonly ICategoryService? _categories;
    private readonly ICatalogUpdateService? _catalog;
    private readonly ICoverService? _covers;
    private readonly IPhotoRotationService? _rotation;
    private readonly IPhotoExportService? _export;
    private readonly IFileOperationService? _fileOps;
    private readonly IFaceRecognitionService? _faces;
    private readonly IMotifTermService? _motifs;
    private readonly IRelatedPhotoSearch? _related;
    private readonly IFolderPickerService? _folderPicker;
    private readonly INavigationService? _navigation;
    private readonly ILibraryScope? _scope;
    private readonly Gallery.CaptureDates.CaptureDateActions? _captureDates;
    private readonly Gallery.Places.PlaceActions? _places;
    private readonly Albums.AlbumActions? _albums;

    public PhotoActions(
        IDialogService dialogs,
        ILogger<PhotoActions> logger,
        ILibraryContextFactory? factory = null,
        ITagService? tags = null,
        ICategoryService? categories = null,
        ICatalogUpdateService? catalog = null,
        ICoverService? covers = null,
        IPhotoRotationService? rotation = null,
        IPhotoExportService? export = null,
        IFileOperationService? fileOps = null,
        IFaceRecognitionService? faces = null,
        IMotifTermService? motifs = null,
        IRelatedPhotoSearch? related = null,
        IFolderPickerService? folderPicker = null,
        INavigationService? navigation = null,
        ILibraryScope? scope = null,
        Gallery.CaptureDates.CaptureDateActions? captureDates = null,
        Gallery.Places.PlaceActions? places = null,
        Albums.AlbumActions? albums = null)
    {
        _captureDates = captureDates;
        _places = places;
        _albums = albums;
        _dialogs = dialogs;
        _logger = logger;
        _factory = factory;
        _tags = tags;
        _categories = categories;
        _catalog = catalog;
        _covers = covers;
        _rotation = rotation;
        _export = export;
        _fileOps = fileOps;
        _faces = faces;
        _motifs = motifs;
        _related = related;
        _folderPicker = folderPicker;
        _navigation = navigation;
        _scope = scope;
    }

    /// <summary>Für Tests: eigener Weg in die Zwischenablage.</summary>
    internal Action<DataObject>? ClipboardSetter { get; set; }

    /// <summary>Für Tests: Eigenschaften-Fenster ersetzen.</summary>
    internal Func<int, Window?, Task>? PropertiesOverride { get; set; }

    public bool HasTags => _tags is not null;

    public bool HasCategories => _categories is not null;

    public bool HasRating => _catalog is not null;

    public bool HasFaces => _faces is not null;

    public bool HasCovers => _covers is not null;

    public bool HasRotation => _rotation is not null;

    public bool HasCopyTo => _fileOps is not null;

    public bool HasExport => _export is not null;

    public bool HasRelatedSearch => _related is not null;

    /// <summary>Versteckte Sitzung: kein Weg nach außen (Zwischenablage, Ziehen, Exportieren, Explorer, andere Programme).</summary>
    public bool IsExternalBlocked => _export?.IsBlocked == true || HiddenSessionPrivacy.IsHiddenSessionActive;

    // ---- Bewerten ------------------------------------------------------------------------------------------------------

    /// <summary>Gemeinsame Bewertung (null = gemischt oder keine) und ob alle Favorit sind.</summary>
    public async Task<(byte? Rating, bool AllRated, bool AllFavorite)> GetRatingStateAsync(IReadOnlyList<int> fileIds)
    {
        if (_factory is null || fileIds.Count == 0)
        {
            return (null, false, false);
        }

        return await Task.Run(() =>
        {
            var ratings = new HashSet<int>();
            var favorites = 0;
            var count = 0;
            using var connection = _factory.OpenSqliteConnection();
            foreach (var chunk in fileIds.Distinct().Chunk(ChunkSize))
            {
                using var cmd = connection.CreateCommand();
                cmd.CommandText = $"SELECT COALESCE(Rating, 0), IsFavorite FROM Files WHERE Id IN ({string.Join(",", chunk.Select(i => i.ToString(CultureInfo.InvariantCulture)))});";
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    count++;
                    ratings.Add(reader.GetInt32(0));
                    favorites += reader.GetBoolean(1) ? 1 : 0;
                }
            }

            byte? rating = ratings.Count == 1 ? (byte)ratings.First() : null;
            return (rating, ratings.Count == 1, count > 0 && favorites == count);
        }).ConfigureAwait(true);
    }

    public async Task<string?> SetRatingAsync(IReadOnlyList<int> fileIds, byte? rating)
    {
        if (_catalog is null || fileIds.Count == 0)
        {
            return null;
        }

        await _catalog.SetRatingAsync(fileIds, rating is 0 ? null : rating).ConfigureAwait(true);
        return rating is null or 0 ? "Bewertung entfernt." : $"{rating} {(rating == 1 ? "Stern" : "Sterne")} gesetzt.";
    }

    public async Task<string?> SetFavoriteAsync(IReadOnlyList<int> fileIds, bool favorite)
    {
        if (_catalog is null || fileIds.Count == 0)
        {
            return null;
        }

        await _catalog.SetFavoriteAsync(fileIds, favorite).ConfigureAwait(true);
        return favorite ? "Als Favorit markiert." : "Favorit entfernt.";
    }

    // ---- Tags und Kategorien -----------------------------------------------------------------------------------------

    public async Task<IReadOnlyList<TagMenuState>> GetTagStatesAsync(IReadOnlyList<int> fileIds)
    {
        if (_tags is null || fileIds.Count == 0)
        {
            return [];
        }

        var all = await _tags.GetAllAsync().ConfigureAwait(true);
        var map = await _tags.GetTagIdsForFilesAsync(fileIds.Distinct().ToList()).ConfigureAwait(true);
        var counts = new Dictionary<int, int>();
        foreach (var ids in map.Values)
        {
            foreach (var id in ids)
            {
                counts[id] = counts.GetValueOrDefault(id) + 1;
            }
        }

        var total = fileIds.Distinct().Count();
        return all.OrderBy(t => t.SortOrder).ThenBy(t => t.Name, StringComparer.CurrentCultureIgnoreCase)
            .Select(t => new TagMenuState(t, counts.GetValueOrDefault(t.Id) switch
            {
                0 => false,
                var n when n >= total => true,
                _ => null,
            }))
            .ToList();
    }

    public async Task<string?> SetTagAsync(IReadOnlyList<int> fileIds, TagInfo tag, bool assign)
    {
        if (_tags is null || fileIds.Count == 0)
        {
            return null;
        }

        if (assign)
        {
            await _tags.AssignAsync(fileIds, [tag.Id]).ConfigureAwait(true);
            return $"Tag „{tag.Name}“ zugewiesen.";
        }

        await _tags.RemoveAsync(fileIds, [tag.Id]).ConfigureAwait(true);
        return $"Tag „{tag.Name}“ entfernt.";
    }

    public async Task<IReadOnlyList<CategoryMenuState>> GetCategoryStatesAsync(IReadOnlyList<int> fileIds)
    {
        if (_categories is null || fileIds.Count == 0)
        {
            return [];
        }

        var all = await _categories.GetAllAsync().ConfigureAwait(true);
        var distinct = fileIds.Distinct().ToList();
        var map = await _categories.GetValuesForFilesAsync(distinct).ConfigureAwait(true);
        var result = new List<CategoryMenuState>();
        foreach (var category in all.OrderBy(c => c.SortOrder))
        {
            var values = new List<(CategoryValueInfo, bool?)>();
            foreach (var value in category.Values.OrderBy(v => v.SortOrder))
            {
                var n = distinct.Count(id => map.TryGetValue(id, out var byCategory) && byCategory.TryGetValue(category.Id, out var v) && v == value.Id);
                values.Add((value, n == 0 ? false : n >= distinct.Count ? true : null));
            }

            result.Add(new CategoryMenuState(category, values));
        }

        return result;
    }

    public async Task<string?> SetCategoryValueAsync(IReadOnlyList<int> fileIds, CategoryInfo category, CategoryValueInfo? value)
    {
        if (_categories is null || fileIds.Count == 0)
        {
            return null;
        }

        await _categories.SetValueAsync(fileIds, category.Id, value?.Id).ConfigureAwait(true);
        return value is null ? $"„{category.Name}“ entfernt." : $"{category.Name}: {value.Name}";
    }

    public void ManageTags() => _navigation?.NavigateTo(NavigationTags.Tags);

    public void ManageCategories() => _navigation?.NavigateTo(NavigationTags.Categories);

    // ---- Personen ------------------------------------------------------------------------------------------------------

    /// <summary>Gesichter im Foto (ohne ignorierte), von links nach rechts.</summary>
    public async Task<IReadOnlyList<FacePersonInfo>> GetFacesAsync(int fileId)
    {
        if (_faces is null)
        {
            return [];
        }

        var map = await _faces.GetFacesForFilesAsync([fileId], CancellationToken.None).ConfigureAwait(true);
        return map.TryGetValue(fileId, out var faces)
            ? faces.Where(f => f.State != Domain.Enums.FaceState.Ignored).OrderBy(f => f.BoxRelative.X).ToList()
            : [];
    }

    /// <summary>Vorschlag bestätigen.</summary>
    public async Task<string?> ConfirmFaceAsync(FacePersonInfo face, int personId, string name)
    {
        if (_faces is null)
        {
            return null;
        }

        await _faces.ConfirmAsync(face.FaceId, personId).ConfigureAwait(true);
        return $"Gesicht als „{name}“ bestätigt.";
    }

    /// <summary>„Unbekannt – zuordnen…“: Name eingeben; vorhandene Person (ohne Groß-/Kleinschreibung) oder neue.</summary>
    public async Task<string?> AssignFaceAsync(FacePersonInfo face)
    {
        if (_faces is null)
        {
            return null;
        }

        var name = await _dialogs.PromptTextAsync(
            "Wer ist das?",
            "Name der Person — eine vorhandene Person mit diesem Namen wird zugeordnet, sonst wird sie neu angelegt.",
            face.SuggestedPersonName ?? string.Empty,
            "Zuordnen").ConfigureAwait(true);
        var trimmed = name?.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            return null;
        }

        var persons = await _faces.GetPersonsAsync(CancellationToken.None).ConfigureAwait(true);
        if (persons.FirstOrDefault(p => string.Equals(p.Name, trimmed, StringComparison.CurrentCultureIgnoreCase)) is { } existing)
        {
            await _faces.ConfirmAsync(face.FaceId, existing.Id).ConfigureAwait(true);
            return $"Gesicht als „{existing.Name}“ bestätigt.";
        }

        await _faces.ConfirmNewPersonAsync(face.FaceId, trimmed).ConfigureAwait(true);
        return $"Neue Person „{trimmed}“ angelegt und bestätigt.";
    }

    // ---- Titelbild -----------------------------------------------------------------------------------------------------

    public Task<IReadOnlyList<CoverTarget>> GetCoverTargetsAsync(int fileId)
        => _covers is null ? Task.FromResult<IReadOnlyList<CoverTarget>>([]) : _covers.GetTargetsAsync(fileId);

    public async Task<string?> SetCoverAsync(CoverTarget target, int fileId)
    {
        if (_covers is null)
        {
            return null;
        }

        await _covers.SetAsync(target, fileId).ConfigureAwait(true);
        return $"Titelbild für {KindText(target.Kind)} „{target.Name}“ gesetzt.";
    }

    /// <summary>„Wieder automatisch wählen lassen…“: welche selbst gewählten Titelbilder dieses Fotos zurück auf automatisch?</summary>
    public async Task<string?> ResetCoversAsync(IReadOnlyList<CoverTarget> manual)
    {
        if (_covers is null || manual.Count == 0)
        {
            return null;
        }

        var list = string.Join(", ", manual.Select(t => $"{KindText(t.Kind)} „{t.Name}“"));
        if (!await _dialogs.ConfirmAsync(
                "Wieder automatisch wählen lassen",
                $"Lumora wählt das Titelbild dann wieder selbst (bestes Gesicht bzw. beste Foto-Güte): {list}.",
                "Automatisch wählen").ConfigureAwait(true))
        {
            return null;
        }

        foreach (var target in manual)
        {
            await _covers.ResetAsync(target.Kind, target.Id).ConfigureAwait(true);
        }

        return manual.Count == 1 ? $"Titelbild für {KindText(manual[0].Kind)} „{manual[0].Name}“ wird wieder automatisch gewählt." : $"{manual.Count} Titelbilder werden wieder automatisch gewählt.";
    }

    public static string KindText(CoverTargetKind kind) => kind switch
    {
        CoverTargetKind.Person => "Person",
        CoverTargetKind.Album => "Album",
        CoverTargetKind.Story => "Story",
        _ => "Ordner",
    };

    // ---- Bearbeiten ----------------------------------------------------------------------------------------------------

    public async Task<string?> RotateAsync(IReadOnlyList<int> fileIds, bool clockwise)
    {
        if (_rotation is null || fileIds.Count == 0)
        {
            return null;
        }

        var result = await _rotation.RotateAsync(fileIds, clockwise).ConfigureAwait(true);
        if (result.Failed > 0)
        {
            await _dialogs.ShowErrorAsync("Drehen", string.Join(Environment.NewLine, result.Errors.Take(20))).ConfigureAwait(true);
        }

        return result.Summary;
    }

    public bool HasCaptureDate => _captureDates is not null;

    public bool HasPlaces => _places is not null;

    public bool HasAlbums => _albums is not null;

    /// <summary>„Datum setzen…“ für Dateien außerhalb der Galerie-Auswahl (Lightbox).</summary>
    public Task<string?> SetDateAsync(IReadOnlyList<int> fileIds, Window? owner)
        => _captureDates is null ? Task.FromResult<string?>(null) : _captureDates.SetForFilesAsync(fileIds, owner);

    /// <summary>„Ort setzen…“ für Dateien außerhalb der Galerie-Auswahl (Lightbox).</summary>
    public async Task<string?> SetPlaceAsync(IReadOnlyList<int> fileIds, bool hasVideos, Window? owner)
    {
        if (_places is null)
        {
            return null;
        }

        var libraries = _scope is null ? (IReadOnlyCollection<int>)[] : (await _scope.GetCurrentAsync().ConfigureAwait(true)).LibraryIds;
        var result = await _places.SetForFilesAsync(fileIds, libraries, hasVideos, owner).ConfigureAwait(true);
        return result?.Text;
    }

    /// <summary>„Zu Album hinzufügen…“ (Name eingeben; neuer Name legt das Album an, Fotos werden nur verknüpft).</summary>
    public async Task<string?> AddToAlbumByNameAsync(IReadOnlyList<int> fileIds)
    {
        if (_albums is null || fileIds.Count == 0)
        {
            return null;
        }

        var name = await _dialogs.PromptTextAsync(
            "Zu Album hinzufügen",
            (fileIds.Count == 1 ? "1 Datei" : fileIds.Count.ToString("N0", CultureInfo.CurrentCulture) + " Dateien")
                + " zu welchem Album? Ein neuer Name legt das Album an — die Fotos werden nur verknüpft, nicht kopiert.",
            string.Empty,
            "Hinzufügen").ConfigureAwait(true);
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        var album = await _albums.Albums.FindByNameAsync(name.Trim()).ConfigureAwait(true) ?? await _albums.Albums.CreateAsync(name.Trim()).ConfigureAwait(true);
        if (album.IsSmart)
        {
            return "„" + album.Name + "“ ist ein intelligentes Album — es füllt sich selbst über seinen Filter.";
        }

        var added = await _albums.Albums.AddFilesAsync(album.Id, fileIds).ConfigureAwait(true);
        if (added > 0)
        {
            Lumora.Services.Usage.UsageReport.Used(fileIds); // Nutzungs-Signale (ADR-0072)
        }
        return added.ToString("N0", CultureInfo.CurrentCulture) + " Datei(en) zu „" + album.Name + "“ hinzugefügt (verknüpft).";
    }

    public void ManageAlbums() => _navigation?.NavigateTo(NavigationTags.Albums);

    // ---- Datei ---------------------------------------------------------------------------------------------------------

    /// <summary>Kopieren (in die Zwischenablage) über den Katalog (fehlende und Archive ausgelassen, versteckt gesperrt).</summary>
    public async Task<string> CopyToClipboardAsync(IReadOnlyList<int> fileIds)
    {
        if (IsExternalBlocked)
        {
            return ExternalFileSet.BlockedText;
        }

        var files = _export is null ? ExternalFileSet.Empty : await _export.GetExternalFilesAsync(fileIds).ConfigureAwait(true);
        return CopyToClipboard(files);
    }

    /// <summary>Kopieren (in die Zwischenablage) aus bekannten Daten (Kacheln, Lightbox).</summary>
    public string CopyToClipboard(IEnumerable<ExternalCandidate> candidates) => CopyToClipboard(ExternalFileTransfer.Plan(candidates, IsExternalBlocked));

    private string CopyToClipboard(ExternalFileSet files)
    {
        try
        {
            var text = ExternalFileTransfer.CopyToClipboard(files, ClipboardSetter);
            if (!files.Blocked)
            {
                Lumora.Services.Usage.UsageReport.Shared(files.FileIds); // Kopieren nach außen (Nutzungs-Signale, ADR-0072)
            }

            return text;
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidOperationException)
        {
            _logger.LogWarning(ex, "Zwischenablage nicht verfügbar");
            return $"Zwischenablage nicht verfügbar: {ex.Message}";
        }
    }

    /// <summary>„Kopieren nach…“: Ordner wählen, protokollierter Copy-Batch mit Rückgängig (ADR-0068).</summary>
    public async Task<string?> CopyToAsync(IReadOnlyList<int> fileIds, string? initialDirectory)
    {
        if (_fileOps is null || fileIds.Count == 0)
        {
            return null;
        }

        var target = _folderPicker?.PickFolder(fileIds.Count == 1 ? "Kopieren nach …" : $"{fileIds.Count:N0} Dateien kopieren nach …", initialDirectory);
        if (string.IsNullOrWhiteSpace(target))
        {
            return null;
        }

        // Versteckte Bibliothek: Kopien nur innerhalb des Containers (ADR-0049)
        var inside = await IsInsideLibraryAsync(target).ConfigureAwait(true);
        if (IsExternalBlocked && !inside)
        {
            return "In der versteckten Bibliothek nur in einen Ordner der Bibliothek — Fotos verlassen den Container nicht.";
        }

        var result = await _fileOps.CopyAsync(fileIds, target).ConfigureAwait(true);
        if (!inside && result.Succeeded > 0)
        {
            // Nutzungs-Signale (ADR-0072): Kopie nach außen = Weitergabe; innerhalb der Bibliothek ist es Ordnen und zählt nicht
            Lumora.Services.Usage.UsageReport.Shared(result.Items.Where(i => i.Success).Select(i => i.FileId).Distinct().ToList());
        }
        if (result.Failed > 0)
        {
            await _dialogs.ShowErrorAsync(
                $"Kopieren: {result.Failed:N0} Fehler",
                string.Join(Environment.NewLine, result.Items.Where(i => !i.Success).Take(20).Select(i => $"{Path.GetFileName(i.SourcePath)}: {i.Error}"))).ConfigureAwait(true);
        }

        return $"{result.Succeeded:N0} Datei(en) kopiert{(result.Failed > 0 ? $", {result.Failed:N0} fehlgeschlagen" : string.Empty)} — Rückgängig in der Aktivitäts-Anzeige.";
    }

    /// <summary>„Exportieren (verkleinert)…“: Ordner außerhalb wählen, JPEG-Kopien ohne Katalog (Aktivitäts-Anzeige).</summary>
    public async Task<string?> ExportAsync(IReadOnlyList<int> fileIds, int maxLongEdge)
    {
        if (_export is null || fileIds.Count == 0)
        {
            return null;
        }

        if (IsExternalBlocked)
        {
            return ExternalFileSet.BlockedText;
        }

        var target = _folderPicker?.PickFolder($"Verkleinert exportieren ({maxLongEdge} px) nach …", null);
        if (string.IsNullOrWhiteSpace(target))
        {
            return null;
        }

        if (await IsInsideLibraryAsync(target).ConfigureAwait(true))
        {
            return "Bitte einen Ordner außerhalb der Bibliothek wählen — Exporte gehören nicht in den Katalog (dafür „Kopieren nach…“).";
        }

        var result = await _export.ExportAsync(fileIds, target, new PhotoExportOptions(maxLongEdge)).ConfigureAwait(true);
        if (result.Exported > 0)
        {
            Lumora.Services.Usage.UsageReport.Shared(fileIds); // Exportieren = Weitergabe (Nutzungs-Signale, ADR-0072)
        }

        return result.Summary;
    }

    /// <summary>Liegt der Ordner unter einer Wurzel einer Bibliothek dieses Katalogs?</summary>
    public async Task<bool> IsInsideLibraryAsync(string directory)
    {
        if (_factory is null)
        {
            return false;
        }

        var full = Path.GetFullPath(directory).TrimEnd('\\', '/');
        var roots = await Task.Run(() =>
        {
            using var connection = _factory.OpenSqliteConnection();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT Path FROM LibraryRoots;";
            var list = new List<string>();
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                list.Add(reader.GetString(0));
            }

            return list;
        }).ConfigureAwait(true);
        return roots.Select(r => Path.GetFullPath(r).TrimEnd('\\', '/'))
            .Any(r => full.Equals(r, StringComparison.OrdinalIgnoreCase) || full.StartsWith(r + "\\", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>„Umbenennen…“ (protokolliert, rückgängig).</summary>
    public async Task<string?> RenameAsync(int fileId, string currentName)
    {
        if (_fileOps is null)
        {
            return null;
        }

        var name = await _dialogs.PromptTextAsync("Umbenennen", $"Neuer Name für „{currentName}“:", currentName, "Umbenennen").ConfigureAwait(true);
        if (string.IsNullOrWhiteSpace(name) || name.Trim() == currentName)
        {
            return null;
        }

        var result = await _fileOps.RenameAsync(fileId, name.Trim()).ConfigureAwait(true);
        return result.Succeeded > 0
            ? $"Umbenannt in „{name.Trim()}“."
            : $"Nicht umbenannt: {result.Items.FirstOrDefault()?.Error ?? "unbekannter Fehler"}";
    }

    public string? ShowInExplorer(string path)
    {
        try
        {
            HiddenSessionPrivacy.ThrowIfShellBlocked(); // versteckte Sitzung: keine Shell-Spuren (ADR-0049)
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
            return null;
        }
        catch (Exception ex)
        {
            return $"Explorer konnte nicht geöffnet werden: {ex.Message}";
        }
    }

    public string? OpenWithDefaultApp(string path)
    {
        try
        {
            HiddenSessionPrivacy.ThrowIfShellBlocked();
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            return null;
        }
        catch (Exception ex)
        {
            return $"Öffnen fehlgeschlagen: {ex.Message}";
        }
    }

    public string CopyPath(string path)
    {
        try
        {
            if (ClipboardSetter is { } setter)
            {
                var data = new DataObject();
                data.SetText(path);
                setter(data);
            }
            else
            {
                Clipboard.SetText(path);
            }

            return "Pfad kopiert.";
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidOperationException)
        {
            return $"Zwischenablage nicht verfügbar: {ex.Message}";
        }
    }

    /// <summary>„Eigenschaften“ (Alt+Enter): eigenes Fenster mit Datei- und EXIF-Angaben.</summary>
    public Task ShowPropertiesAsync(int fileId, Window? owner)
    {
        if (PropertiesOverride is { } custom)
        {
            return custom(fileId, owner);
        }

        if (_factory is null)
        {
            return Task.CompletedTask;
        }

        return PhotoPropertiesWindow.ShowAsync(_factory, _tags, _categories, fileId, owner);
    }

    // ---- Suchen --------------------------------------------------------------------------------------------------------

    public async Task<bool> HasSimilarAsync(int fileId)
    {
        if (_motifs is null)
        {
            return false;
        }

        try
        {
            return await _motifs.HasImageVectorAsync(fileId).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Bild-Vektor von {FileId} nicht lesbar", fileId);
            return false;
        }
    }

    public async Task<RelatedPhotos?> SimilarAsync(int fileId, string name)
    {
        if (_motifs is null || _scope is null)
        {
            return null;
        }

        var scope = await _scope.GetCurrentAsync().ConfigureAwait(true);
        var hits = await _motifs.SearchSimilarAsync(scope.LibraryId, fileId, Lumora.Gallery.ViewModels.GalleryViewModel.ContentSearchLimit).ConfigureAwait(true);
        return hits is null ? null : new RelatedPhotos($"ähnlich wie {name}", hits.Select(h => h.FileId).ToList());
    }

    public Task<RelatedPhotos?> SameDayAsync(int fileId) => _related?.SameDayAsync(fileId) ?? Task.FromResult<RelatedPhotos?>(null);

    public Task<RelatedPhotos?> SamePlaceAsync(int fileId) => _related?.SamePlaceAsync(fileId) ?? Task.FromResult<RelatedPhotos?>(null);

    public Task<RelatedPhotos?> DuplicatesAsync(int fileId) => _related?.DuplicatesAsync(fileId) ?? Task.FromResult<RelatedPhotos?>(null);

    public Task<RelatedPhotos?> SamePersonsAsync(int fileId) => _related?.SamePersonsAsync(fileId) ?? Task.FromResult<RelatedPhotos?>(null);
}
