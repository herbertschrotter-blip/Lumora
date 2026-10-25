using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Lumora.Services.Covers;
using Lumora.Services.Related;
using SymbolIcon = Wpf.Ui.Controls.SymbolIcon;
using SymbolRegular = Wpf.Ui.Controls.SymbolRegular;

namespace Lumora.PhotoMenu;

/// <summary>
/// Was nur der Ort kann (Galerie/Liste bzw. Lightbox): Öffnen, Diashow, Archiv, Alben, Story, Datum/Ort, Gesichter anzeigen,
/// Verschieben, Papierkorb, Suchergebnis anzeigen. Null = Eintrag entfällt.
/// </summary>
public sealed record PhotoMenuHost
{
    public required Func<Window?> Owner { get; init; }

    public required Action<string> ShowStatus { get; init; }

    public Action? Open { get; init; }

    public Action? Slideshow { get; init; }

    public string SlideshowHeader { get; init; } = "Diashow ab hier";

    public Action? ShowArchiveContent { get; init; }

    public Action? ExtractArchive { get; init; }

    public Action? ArchiveIntoLibrary { get; init; }

    /// <summary>Tag-Dialog (Strg+T) — „Neuer Tag…“ und „Tags bearbeiten…“.</summary>
    public Action? EditTags { get; init; }

    /// <summary>Kategorien-Dialog (Strg+K).</summary>
    public Action? EditCategories { get; init; }

    public Action<ItemCollection>? FillAlbums { get; init; }

    public Action<ItemCollection>? FillStories { get; init; }

    public Action? SetDate { get; init; }

    public Action? SetPlace { get; init; }

    public Action? ShowFaces { get; init; }

    /// <summary>„Bearbeiten ▸ Foto verbessern…“ (ADR-0071): Leiste „Verbessern“ bzw. bei Mehrfachauswahl der Stapel-Dialog; null = ausgegraut.</summary>
    public Action? Enhance { get; init; }

    /// <summary>„Suchen ▸“: Ergebnis als Bedingung in der Galerie zeigen; null = kein „Suchen“.</summary>
    public Func<RelatedPhotos, Task>? ApplySearch { get; init; }

    public Action? MoveTo { get; init; }

    public Action? Recycle { get; init; }

    /// <summary>„Aus der Bibliothek entfernen“ (ADR-0065, 0.5.2): nur wenn die Auswahl fehlende Dateien enthält; entfernt sie aus dem
    /// Katalog, keine Datei wird gelöscht. Null = Eintrag entfällt.</summary>
    public Action? RemoveMissing { get; init; }

    /// <summary>Dateien für Strg+C aus bekannten Daten (ohne Katalog-Abfrage); null = über den Katalog.</summary>
    public Func<IEnumerable<ExternalCandidate>>? ExternalCandidates { get; init; }

    /// <summary>Startordner für „Kopieren nach…“.</summary>
    public string? CopyInitialDirectory { get; init; }
}

/// <summary>
/// Baut das neu gegliederte Rechtsklick-Menü eines Fotos (ADR-0068, Mockup 17) — gleiche Struktur für Galerie-Kachel, Listenzeile
/// und Lightbox: Öffnen/Diashow | Bewerten ▸ · Tags ▸ · Kategorien ▸ · Personen ▸ · Alben ▸ · Story ▸ · Als Titelbild für ▸ |
/// Bearbeiten ▸ · Suchen ▸ | Datei ▸ · In den Papierkorb | Eigenschaften; Archiv-Dateien bekommen „Inhalt anzeigen / Entpacken… /
/// In die Bibliothek…“ ganz oben. Bei Mehrfachauswahl entfallen Einträge, die nur für ein Foto Sinn haben (Öffnen, Personen,
/// Titelbild, Suchen, Umbenennen, Explorer, Pfad, Standard-App, Eigenschaften). Untermenüs mit Katalog-Daten werden erst beim
/// Aufklappen gefüllt (<see cref="FillSubmenuAsync"/>) — das Öffnen des Menüs bleibt sofort.
/// </summary>
public static class PhotoContextMenuBuilder
{
    /// <summary>Platzhalter, damit WPF den Pfeil eines noch leeren Untermenüs zeigt.</summary>
    private const string LoadingText = "Wird geladen…";

    public static void Build(ItemCollection items, PhotoMenuContext context, PhotoMenuHost host, PhotoActions actions)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(actions);
        items.Clear();
        var ids = context.FileIds;
        var primary = context.Primary;
        var single = !context.IsMulti;

        // ---- Archiv-Dateien: ganz oben ----
        if (single && primary.IsArchive)
        {
            Add(items, "Inhalt anzeigen", SymbolRegular.DocumentBulletList24, host.ShowArchiveContent);
            Add(items, "Entpacken…", SymbolRegular.FolderZip24, host.ExtractArchive);
            Add(items, "In die Bibliothek…", SymbolRegular.Archive24, host.ArchiveIntoLibrary, PhotoMenuGestures.IntoLibrary);
            Separator(items);
        }

        // ---- Öffnen ----
        if (single && !primary.IsArchive)
        {
            Add(items, "Öffnen", SymbolRegular.Open24, host.Open, PhotoMenuGestures.Open(context));
        }

        Add(items, host.SlideshowHeader, SymbolRegular.Play24, host.Slideshow, PhotoMenuGestures.Slideshow(context));
        Separator(items);

        // ---- Zuordnen ----
        if (actions.HasRating)
        {
            Lazy(items, "Bewerten", SymbolRegular.Star24, menu => FillRatingAsync(menu, context, host, actions));
        }

        if (actions.HasTags)
        {
            Lazy(items, "Tags", SymbolRegular.Tag24, menu => FillTagsAsync(menu, context, host, actions));
        }

        if (actions.HasCategories)
        {
            Lazy(items, "Kategorien", SymbolRegular.Group24, menu => FillCategoriesAsync(menu, context, host, actions));
        }

        if (single && actions.HasFaces && primary.IsImage)
        {
            Lazy(items, "Personen", SymbolRegular.People24, menu => FillPersonsAsync(menu, context, host, actions));
        }

        if (host.FillAlbums is { } fillAlbums)
        {
            Static(items, "Alben", SymbolRegular.Album24, fillAlbums);
        }

        if (host.FillStories is { } fillStories)
        {
            Static(items, "Story", SymbolRegular.BookOpen24, fillStories);
        }

        if (single && actions.HasCovers && !primary.IsArchive)
        {
            Lazy(items, "Als Titelbild für", SymbolRegular.ImageMultiple24, menu => FillCoversAsync(menu, primary.FileId, host, actions));
        }

        Separator(items);

        // ---- Bearbeiten / Suchen ----
        var edit = Sub(items, "Bearbeiten", SymbolRegular.Edit24);
        Add(edit.Items, "Datum setzen…", SymbolRegular.CalendarEdit24, host.SetDate);
        Add(edit.Items, "Ort setzen…", SymbolRegular.Location24, host.SetPlace);
        if (actions.HasRotation)
        {
            Separator(edit.Items);
            var rotatable = single ? primary.CanRotate : true;
            var tip = rotatable ? null : "Drehen geht nur bei JPEG-Fotos (die Ausrichtung steht in der Datei).";
            Add(edit.Items, "Nach links drehen", SymbolRegular.ArrowRotateCounterclockwise24, rotatable ? () => Run(host, () => actions.RotateAsync(ids, clockwise: false)) : null, toolTip: tip, keepDisabled: !rotatable);
            Add(edit.Items, "Nach rechts drehen", SymbolRegular.ArrowRotateClockwise24, rotatable ? () => Run(host, () => actions.RotateAsync(ids, clockwise: true)) : null, toolTip: tip, keepDisabled: !rotatable);
        }

        Separator(edit.Items);
        // Foto verbessern (ADR-0071): eines → Lightbox-Leiste „Verbessern“, mehrere → Stapel-Dialog; Videos/Archive/fehlende nicht
        var enhanceable = single ? primary.IsImage && !primary.IsMissing : true;
        Add(edit.Items, single ? "Foto verbessern…" : $"{ids.Count:N0} Fotos verbessern…", SymbolRegular.Sparkle24,
            enhanceable ? host.Enhance : null,
            toolTip: enhanceable ? "Hochskalieren, Gesichter, Einfärben, Entfernen, Licht — als neue Datei, das Original bleibt" : "Nur Fotos lassen sich verbessern.",
            keepDisabled: true);
        RemoveTrailingSeparators(edit.Items);
        if (edit.Items.Count == 0)
        {
            items.Remove(edit);
        }

        if (single && host.ApplySearch is not null && actions.HasRelatedSearch && !primary.IsArchive)
        {
            Lazy(items, "Suchen", SymbolRegular.Search24, menu => FillSearchAsync(menu, primary, host, actions));
        }

        Separator(items);

        // ---- Datei ----
        var file = Sub(items, "Datei", SymbolRegular.Document24);
        var blocked = actions.IsExternalBlocked;
        Add(file.Items, "Kopieren (in die Zwischenablage)", SymbolRegular.Copy24,
            blocked ? null : () => CopyToClipboard(context, host, actions), PhotoMenuGestures.Copy,
            toolTip: blocked ? Lumora.Services.Export.ExternalFileSet.BlockedText : "Dateien kopieren — in Explorer, Mail & Co. einfügen (immer als Kopie)",
            keepDisabled: blocked);
        if (actions.HasCopyTo)
        {
            Add(file.Items, "Kopieren nach…", SymbolRegular.DocumentCopy24, () => Run(host, () => actions.CopyToAsync(ids, host.CopyInitialDirectory)));
        }

        Add(file.Items, "Verschieben nach…", SymbolRegular.FolderArrowRight24, host.MoveTo);
        if (single && actions.HasCopyTo)
        {
            Add(file.Items, "Umbenennen…", SymbolRegular.Rename24, () => Run(host, () => actions.RenameAsync(primary.FileId, primary.Name)));
        }

        if (!primary.IsArchive || context.IsMulti)
        {
            Add(file.Items, "In die Bibliothek…", SymbolRegular.Archive24, host.ArchiveIntoLibrary, PhotoMenuGestures.IntoLibrary);
        }

        if (single)
        {
            Separator(file.Items);
            Add(file.Items, "Im Explorer anzeigen", SymbolRegular.FolderOpen24, () => Show(host, actions.ShowInExplorer(primary.FullPath)));
            Add(file.Items, "Pfad kopieren", SymbolRegular.Clipboard24, () => host.ShowStatus(actions.CopyPath(primary.FullPath)));
            Add(file.Items, "Mit Standard-App öffnen", SymbolRegular.WindowNew24, () => Show(host, actions.OpenWithDefaultApp(primary.FullPath)));
        }

        if (actions.HasExport)
        {
            Separator(file.Items);
            var export = Sub(file.Items, "Exportieren (verkleinert)", SymbolRegular.ArrowExportUp24);
            if (blocked)
            {
                export.IsEnabled = false;
                export.ToolTip = Lumora.Services.Export.ExternalFileSet.BlockedText;
                ToolTipService.SetShowOnDisabled(export, true);
            }

            foreach (var size in Lumora.Services.Export.PhotoExportOptions.Sizes)
            {
                var edge = size;
                Add(export.Items, $"Längste Kante {edge:N0} px…", null, () => Run(host, () => actions.ExportAsync(ids, edge)));
            }
        }

        RemoveTrailingSeparators(file.Items);
        Add(items, "In den Papierkorb", SymbolRegular.Delete24, host.Recycle, PhotoMenuGestures.Recycle);
        if (host.RemoveMissing is not null)
        {
            Add(items, "Aus der Bibliothek entfernen", SymbolRegular.DismissCircle24, host.RemoveMissing,
                toolTip: "Fehlende Dateien aus der Bibliothek entfernen — es wird keine Datei gelöscht");
        }

        if (single)
        {
            Separator(items);
            Add(items, "Eigenschaften", SymbolRegular.Info24, () => _ = ShowPropertiesAsync(primary.FileId, host, actions), PhotoMenuGestures.Properties);
        }

        RemoveTrailingSeparators(items);
    }

    /// <summary>Untermenü füllen (beim Aufklappen; Tests rufen es direkt). Wirkungslos bei statischen Untermenüs.</summary>
    public static Task FillSubmenuAsync(MenuItem menu)
        => menu.Tag is Func<MenuItem, Task> fill ? fill(menu) : Task.CompletedTask;

    /// <summary>Eintrag mit diesem Text (auch in Untermenüs, nur bereits gefüllte); null, wenn es ihn nicht gibt.</summary>
    public static MenuItem? Find(ItemCollection items, string header)
    {
        foreach (var item in items.OfType<MenuItem>())
        {
            if (string.Equals(item.Header as string, header, StringComparison.Ordinal))
            {
                return item;
            }

            if (Find(item.Items, header) is { } nested)
            {
                return nested;
            }
        }

        return null;
    }

    // ---- Untermenüs mit Katalog-Daten ------------------------------------------------------------------------------------

    private static async Task FillRatingAsync(MenuItem menu, PhotoMenuContext context, PhotoMenuHost host, PhotoActions actions)
    {
        var (rating, allRated, allFavorite) = await actions.GetRatingStateAsync(context.FileIds).ConfigureAwait(true);
        menu.Items.Clear();
        for (byte stars = 1; stars <= 5; stars++)
        {
            var value = stars;
            var item = Add(menu.Items, $"{new string('★', stars)}  {stars} {(stars == 1 ? "Stern" : "Sterne")}", null,
                () => Run(host, () => actions.SetRatingAsync(context.FileIds, value)), PhotoMenuGestures.Rating(context, stars));
            item.IsChecked = allRated && rating == stars;
        }

        var none = Add(menu.Items, "Keine Sterne", null, () => Run(host, () => actions.SetRatingAsync(context.FileIds, null)), PhotoMenuGestures.Rating(context, 0));
        none.IsChecked = allRated && rating is 0;
        Separator(menu.Items);
        var favorite = Add(menu.Items, "Favorit", SymbolRegular.Heart24, () => Run(host, () => actions.SetFavoriteAsync(context.FileIds, !allFavorite)), PhotoMenuGestures.Favorite(context));
        favorite.IsChecked = allFavorite;
    }

    private static async Task FillTagsAsync(MenuItem menu, PhotoMenuContext context, PhotoMenuHost host, PhotoActions actions)
    {
        var states = await actions.GetTagStatesAsync(context.FileIds).ConfigureAwait(true);
        menu.Items.Clear();
        if (states.Count == 0)
        {
            menu.Items.Add(new MenuItem { Header = "Noch keine Tags angelegt", IsEnabled = false });
        }

        foreach (var state in states)
        {
            var tag = state.Tag;
            var assign = state.State != true;
            menu.Items.Add(new MenuItem
            {
                Header = state.State is null ? $"{tag.Name}  (teilweise)" : tag.Name,
                IsCheckable = true,
                IsChecked = state.State == true,
                Icon = new Border { Width = 12, Height = 12, CornerRadius = new CornerRadius(6), Background = TagBrush(tag.Color) },
                Command = new ActionCommand(() => Run(host, () => actions.SetTagAsync(context.FileIds, tag, assign))),
            });
        }

        Separator(menu.Items);
        Add(menu.Items, "Neuer Tag…", SymbolRegular.Add24, host.EditTags, PhotoMenuGestures.Tags);
        Add(menu.Items, "Tags verwalten…", SymbolRegular.Settings24, actions.ManageTags);
    }

    private static async Task FillCategoriesAsync(MenuItem menu, PhotoMenuContext context, PhotoMenuHost host, PhotoActions actions)
    {
        var groups = await actions.GetCategoryStatesAsync(context.FileIds).ConfigureAwait(true);
        menu.Items.Clear();
        if (groups.Count == 0)
        {
            menu.Items.Add(new MenuItem { Header = "Noch keine Kategorien angelegt", IsEnabled = false });
        }

        foreach (var group in groups)
        {
            var category = group.Category;
            var current = group.Values.FirstOrDefault(v => v.State == true);
            var mixed = group.Values.Count(v => v.State != false) > 1 || group.Values.Any(v => v.State is null);
            var parent = new MenuItem
            {
                Header = current.Value is not null ? $"{category.Name}: {current.Value.Name}" : mixed ? $"{category.Name}: (gemischt)" : category.Name,
                Icon = Icon(SymbolRegular.Group24),
            };
            foreach (var (value, state) in group.Values)
            {
                var chosen = value;
                parent.Items.Add(new MenuItem
                {
                    Header = state is null ? $"{value.Name}  (teilweise)" : value.Name,
                    IsCheckable = true,
                    IsChecked = state == true,
                    Command = new ActionCommand(() => Run(host, () => actions.SetCategoryValueAsync(context.FileIds, category, chosen))),
                });
            }

            parent.Items.Add(new Separator());
            parent.Items.Add(new MenuItem
            {
                Header = "Kein Wert",
                FontStyle = FontStyles.Italic,
                Command = new ActionCommand(() => Run(host, () => actions.SetCategoryValueAsync(context.FileIds, category, null))),
            });
            menu.Items.Add(parent);
        }

        Separator(menu.Items);
        Add(menu.Items, "Kategorien zuweisen…", SymbolRegular.Edit24, host.EditCategories, PhotoMenuGestures.Categories);
        Add(menu.Items, "Kategorien verwalten…", SymbolRegular.Settings24, actions.ManageCategories);
    }

    private static async Task FillPersonsAsync(MenuItem menu, PhotoMenuContext context, PhotoMenuHost host, PhotoActions actions)
    {
        var faces = await actions.GetFacesAsync(context.Primary.FileId).ConfigureAwait(true);
        menu.Items.Clear();
        if (faces.Count == 0)
        {
            menu.Items.Add(new MenuItem { Header = "Keine Gesichter erkannt", IsEnabled = false });
        }

        foreach (var face in faces)
        {
            var current = face;
            if (face.State == Domain.Enums.FaceState.Confirmed && face.PersonName is { } name)
            {
                menu.Items.Add(new MenuItem { Header = name, IsCheckable = true, IsChecked = true, StaysOpenOnClick = true, ToolTip = "Bestätigt" });
            }
            else if (face.State == Domain.Enums.FaceState.Suggested && face.SuggestedPersonId is { } suggested && face.SuggestedPersonName is { } suggestedName)
            {
                var item = Sub(menu.Items, $"Vorschlag: {suggestedName}", SymbolRegular.PersonQuestionMark24);
                Add(item.Items, $"Ist {suggestedName}", SymbolRegular.Checkmark24, () => Run(host, () => actions.ConfirmFaceAsync(current, suggested, suggestedName)));
                Add(item.Items, "Andere Person – zuordnen…", SymbolRegular.PersonEdit24, () => Run(host, () => actions.AssignFaceAsync(current)));
            }
            else
            {
                Add(menu.Items, "Unbekannt – zuordnen…", SymbolRegular.PersonAdd24, () => Run(host, () => actions.AssignFaceAsync(current)));
            }
        }

        if (host.ShowFaces is { } showFaces)
        {
            Separator(menu.Items);
            Add(menu.Items, "Gesichter anzeigen", SymbolRegular.ScanPerson24, showFaces, PhotoMenuGestures.Faces(context));
        }
    }

    /// <summary>„Als Titelbild für ▸“ — nur, was zu diesem Foto passt; Häkchen = schon Titelbild (ADR-0068).</summary>
    public static async Task FillCoversAsync(MenuItem menu, int fileId, PhotoMenuHost host, PhotoActions actions)
    {
        IReadOnlyList<CoverTarget> targets;
        try
        {
            targets = await actions.GetCoverTargetsAsync(fileId).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            menu.Items.Clear();
            menu.Items.Add(new MenuItem { Header = $"Nicht lesbar: {ex.Message}", IsEnabled = false });
            return;
        }

        menu.Items.Clear();
        AddCoverGroup(menu.Items, "Personen im Foto", targets.Where(t => t.Kind == CoverTargetKind.Person), SymbolRegular.Person24, fileId, host, actions);
        AddCoverGroup(menu.Items, "Alben", targets.Where(t => t.Kind == CoverTargetKind.Album), SymbolRegular.Album24, fileId, host, actions);
        AddCoverGroup(menu.Items, "Storys", targets.Where(t => t.Kind == CoverTargetKind.Story), SymbolRegular.BookOpen24, fileId, host, actions);
        AddCoverGroup(menu.Items, "Ordner", targets.Where(t => t.Kind == CoverTargetKind.Folder), SymbolRegular.Folder24, fileId, host, actions);
        if (menu.Items.Count == 0)
        {
            menu.Items.Add(new MenuItem { Header = "Nichts, wofür dieses Foto Titelbild sein kann", IsEnabled = false });
            return;
        }

        var manual = targets.Where(t => t.IsManual).ToList();
        Separator(menu.Items);
        var reset = Add(menu.Items, "Wieder automatisch wählen lassen…", SymbolRegular.Sparkle24,
            manual.Count > 0 ? () => Run(host, () => actions.ResetCoversAsync(manual)) : null,
            toolTip: manual.Count > 0 ? null : "Für dieses Foto gibt es kein selbst gewähltes Titelbild.", keepDisabled: manual.Count == 0);
        reset.Tag = manual;
    }

    private static void AddCoverGroup(ItemCollection items, string title, IEnumerable<CoverTarget> targets, SymbolRegular icon, int fileId, PhotoMenuHost host, PhotoActions actions)
    {
        var list = targets.ToList();
        if (list.Count == 0)
        {
            return;
        }

        items.Add(new MenuItem { Header = title, IsEnabled = false, FontSize = 11, FontWeight = FontWeights.SemiBold });
        foreach (var target in list)
        {
            var chosen = target;
            var item = new MenuItem
            {
                Header = target.Name,
                Icon = Icon(icon),
                IsCheckable = true,
                IsChecked = target.IsCurrent,
                ToolTip = target.IsCurrent ? (target.IsManual ? "Schon Titelbild (selbst gewählt)" : "Schon Titelbild (automatisch gewählt)") : null,
                Command = new ActionCommand(() => Run(host, () => actions.SetCoverAsync(chosen, fileId))),
                Tag = target,
            };
            items.Add(item);
        }
    }

    private static async Task FillSearchAsync(MenuItem menu, PhotoMenuTarget primary, PhotoMenuHost host, PhotoActions actions)
    {
        var similar = await actions.HasSimilarAsync(primary.FileId).ConfigureAwait(true);
        menu.Items.Clear();
        if (similar)
        {
            Add(menu.Items, "Ähnliche Fotos", SymbolRegular.ImageSearch24, () => Search(host, () => actions.SimilarAsync(primary.FileId, primary.Name), "Kein Bild-Vektor für dieses Foto."));
        }

        Add(menu.Items, "Gleicher Tag", SymbolRegular.CalendarDay24, () => Search(host, () => actions.SameDayAsync(primary.FileId), "Dieses Foto hat kein Aufnahmedatum."));
        Add(menu.Items, "Gleicher Ort", SymbolRegular.Location24, () => Search(host, () => actions.SamePlaceAsync(primary.FileId), "Dieses Foto hat keinen Ort."));
        Add(menu.Items, "Doppelte zeigen", SymbolRegular.DocumentCopy24, () => Search(host, () => actions.DuplicatesAsync(primary.FileId), "Noch kein Fingerabdruck für dieses Foto (Doppelte werden im Hintergrund berechnet)."));
        Add(menu.Items, "Gleiche Person(en)", SymbolRegular.People24, () => Search(host, () => actions.SamePersonsAsync(primary.FileId), "Auf diesem Foto ist keine Person bestätigt."));
    }

    // ---- Hilfen ------------------------------------------------------------------------------------------------------

    private static void CopyToClipboard(PhotoMenuContext context, PhotoMenuHost host, PhotoActions actions)
    {
        if (host.ExternalCandidates is { } candidates)
        {
            host.ShowStatus(actions.CopyToClipboard(candidates()));
            return;
        }

        Run(host, async () => await actions.CopyToClipboardAsync(context.FileIds).ConfigureAwait(true));
    }

    private static async Task ShowPropertiesAsync(int fileId, PhotoMenuHost host, PhotoActions actions)
    {
        try
        {
            await actions.ShowPropertiesAsync(fileId, host.Owner()).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            host.ShowStatus($"Eigenschaften nicht lesbar: {ex.Message}");
        }
    }

    private static void Search(PhotoMenuHost host, Func<Task<RelatedPhotos?>> query, string emptyText)
    {
        _ = SearchAsync();

        async Task SearchAsync()
        {
            try
            {
                var result = await query().ConfigureAwait(true);
                if (result is null)
                {
                    host.ShowStatus(emptyText);
                    return;
                }

                await host.ApplySearch!(result).ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                host.ShowStatus($"Suche fehlgeschlagen: {ex.Message}");
            }
        }
    }

    private static void Show(PhotoMenuHost host, string? text)
    {
        if (text is not null)
        {
            host.ShowStatus(text);
        }
    }

    /// <summary>Asynchrone Aktion ausführen, Ergebnis bzw. Fehler als Statuszeile.</summary>
    internal static void Run(PhotoMenuHost host, Func<Task<string?>> action)
    {
        _ = RunAsync();

        async Task RunAsync()
        {
            try
            {
                if (await action().ConfigureAwait(true) is { } text)
                {
                    host.ShowStatus(text);
                }
            }
            catch (Exception ex)
            {
                host.ShowStatus($"Nicht ausgeführt: {ex.Message}");
            }
        }
    }

    private static MenuItem Add(ItemCollection items, string header, SymbolRegular? icon, Action? action, string? gesture = null, string? toolTip = null, bool keepDisabled = false)
    {
        var item = new MenuItem
        {
            Header = header,
            InputGestureText = gesture ?? string.Empty,
            Icon = icon is { } symbol ? Icon(symbol) : null,
            ToolTip = toolTip,
        };
        if (action is not null)
        {
            item.Command = new ActionCommand(action);
        }
        else
        {
            item.IsEnabled = false;
            if (toolTip is not null)
            {
                ToolTipService.SetShowOnDisabled(item, true);
            }
        }

        if (action is null && !keepDisabled)
        {
            // Ohne Aktion am Ort entfällt der Eintrag ganz (z. B. „Öffnen“ in der Lightbox)
            return item;
        }

        items.Add(item);
        return item;
    }

    private static MenuItem Sub(ItemCollection items, string header, SymbolRegular icon)
    {
        var item = new MenuItem { Header = header, Icon = Icon(icon) };
        items.Add(item);
        return item;
    }

    private static void Static(ItemCollection items, string header, SymbolRegular icon, Action<ItemCollection> fill)
    {
        var item = Sub(items, header, icon);
        fill(item.Items);
        if (item.Items.Count == 0)
        {
            items.Remove(item);
        }
    }

    private static void Lazy(ItemCollection items, string header, SymbolRegular icon, Func<MenuItem, Task> fill)
    {
        var item = Sub(items, header, icon);
        item.Items.Add(new MenuItem { Header = LoadingText, IsEnabled = false });
        item.Tag = fill;
        item.SubmenuOpened += async (sender, e) =>
        {
            if (ReferenceEquals(e.OriginalSource, sender) && sender is MenuItem menu)
            {
                await FillSubmenuAsync(menu).ConfigureAwait(true);
            }
        };
    }

    private static void Separator(ItemCollection items)
    {
        if (items.Count > 0 && items[^1] is not System.Windows.Controls.Separator)
        {
            items.Add(new Separator());
        }
    }

    private static void RemoveTrailingSeparators(ItemCollection items)
    {
        while (items.Count > 0 && items[^1] is System.Windows.Controls.Separator)
        {
            items.RemoveAt(items.Count - 1);
        }
    }

    private static SymbolIcon Icon(SymbolRegular symbol) => new() { Symbol = symbol };

    private static Brush TagBrush(string? color)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(color) && ColorConverter.ConvertFromString(color) is Color c)
            {
                var brush = new SolidColorBrush(c);
                brush.Freeze();
                return brush;
            }
        }
        catch (FormatException)
        {
        }

        return Brushes.Gray;
    }

    /// <summary>Einfacher Befehl für Menüeinträge (kein CanExecute).</summary>
    private sealed class ActionCommand(Action execute) : System.Windows.Input.ICommand
    {
        public event EventHandler? CanExecuteChanged
        {
            add { }
            remove { }
        }

        public bool CanExecute(object? parameter) => true;

        public void Execute(object? parameter) => execute();
    }
}
