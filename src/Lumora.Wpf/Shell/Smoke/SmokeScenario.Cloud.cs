using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using CommunityToolkit.Mvvm.Messaging;
using Lumora.Domain.Enums;
using Lumora.Lightbox;
using Lumora.Services.Messaging;
using Lumora.Services.Scanning;
using Lumora.Services.Thumbnails;

namespace Lumora.Shell.Smoke;

/// <summary>
/// Rauchtest-Schritt „OneDrive-Platzhalter (simuliert)“ (ADR-0077, vor „Versteckte Bibliothek“) — nur in der Kopie der
/// Testbibliothek: Unterordner <c>Smoke-OneDrive</c> mit einer JPEG-Kopie, die das Attribut „Offline“ trägt (so sieht Lumora
/// einen Platzhalter, der nur online liegt). Einlesen → im Katalog, nie „fehlt“ → Galerie zeigt die Wolken-Marke, Menü kennt
/// „Immer behalten“/„Speicherplatz freigeben“ → Vorschaubild über den Shell-Weg oder „nicht verfügbar“, nie über WIC aus dem
/// Original, die Datei bleibt „nur online“ → Lightbox zeigt den Hinweis „Wird von OneDrive geladen …“ und danach das Bild
/// ohne Fehler → aufräumen (Attribut weg, Ordner in den Test-Papierkorb). Eine echte OneDrive-Prüfung läuft nativ
/// (<c>OneDriveNativeProbeTests</c>); im App-Container liefert die Shell für Platzhalter keine Bilder.
/// </summary>
internal sealed partial class SmokeScenario
{
    private const string CloudFileName = "wolke.jpg";

    private string CloudDir => Path.Combine(_root, "Smoke-OneDrive");

    private async Task CloudPlaceholderAsync()
    {
        Check(_smokeBin is not null && _removalDialogs is not null, "Test-Papierkorb und Fake-Bestätigung registriert");
        RemoveCloudLeftovers();
        _smokeBin!.TestMode = true;
        try
        {
            await CloudPlaceholderCoreAsync().ConfigureAwait(true);
        }
        finally
        {
            _smokeBin.TestMode = false;
            _smokeBin.Clear();
            RemoveCloudLeftovers();
        }
    }

    private async Task CloudPlaceholderCoreAsync()
    {
        // (1) Simulierter Platzhalter einlesen: im Katalog, nicht „fehlt“
        var source = Directory.EnumerateFiles(Path.Combine(_root, "Unsortiert"), "*.jpg").OrderBy(f => f, StringComparer.OrdinalIgnoreCase).First();
        Directory.CreateDirectory(CloudDir);
        var path = Path.Combine(CloudDir, CloudFileName);
        File.Copy(source, path);
        File.SetAttributes(path, File.GetAttributes(path) | FileAttributes.Offline);
        _lastScan = null;
        await _library.RunSmokeScanAsync(_root).ConfigureAwait(true);
        Check(_lastScan is { Mode: ScanMode.Incremental, Outcome: ScanOutcome.Completed, Added: 1, MarkedMissing: 0 }, $"Platzhalter eingelesen (Added={_lastScan?.Added})");
        _mediaCount += 1;
        var id = await DbScalarAsync<int>("SELECT Id FROM Files WHERE LibraryId = $lib AND Name = $n", ("$lib", _libraryId), ("$n", CloudFileName)).ConfigureAwait(true);
        Check(await DbScalarAsync<long>("SELECT Missing FROM Files WHERE Id = $id", ("$id", id)).ConfigureAwait(true) == 0, "Platzhalter gilt nicht als „fehlt“");

        // (2) Galerie im Ordner: Wolken-Marke, Menü-Einträge „Immer behalten“ / „Speicherplatz freigeben“
        var folderId = await DbScalarAsync<int>("SELECT Id FROM Folders WHERE Path = $p", ("$p", CloudDir)).ConfigureAwait(true);
        _navigation.NavigateTo(NavigationTags.Gallery);
        _messenger.Send(new FolderSelectedMessage(_libraryId, folderId, false));
        await WaitUntilAsync(() => !_gallery.IsLoading && _gallery.CurrentPage.Any(i => i.Id == id && i.IsCloudOnly), ShortWait, "Wolken-Marke auf der Kachel").ConfigureAwait(true);
        var item = _gallery.CurrentPage.Single(i => i.Id == id);
        Check(!item.Missing && item.IsCloudFile && _gallery.HasCloudFilesIn([id]), "Kachel: nur online, nicht „fehlt“, Rechtsklick mit „Immer auf diesem Gerät behalten“");
        await SnapshotAsync("onedrive-kachel").ConfigureAwait(true);

        // (3) Vorschaubild: Shell-Weg oder „nicht verfügbar“ — nie WIC aus dem Original; die Datei bleibt „nur online“
        var deadline = DateTime.UtcNow + LongWait;
        string? state;
        while ((state = await DbScalarAsync<string?>(
                   "SELECT Status || '|' || COALESCE(ErrorCode, '') FROM Thumbnails WHERE FileId = $id AND Size = $s AND Kind = 0",
                   ("$id", id),
                   ("$s", ThumbnailService.Size)).ConfigureAwait(true)) is null
               || state.StartsWith($"{(int)ThumbnailStatus.Pending}|", StringComparison.Ordinal))
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new SmokeCheckException($"Timeout: Vorschaubild des Platzhalters ({state ?? "keine Zeile"})");
            }

            await Task.Delay(250).ConfigureAwait(true);
        }

        Check(state == $"{(int)ThumbnailStatus.Generated}|" || state == $"{(int)ThumbnailStatus.Failed}|{ThumbnailErrorCodes.CloudUnavailable}",
            $"Vorschaubild über den Shell-Weg („{state}“: 2 = erzeugt, 3|cloud_unavailable = in dieser Umgebung keins)");
        Check(File.GetAttributes(path).HasFlag(FileAttributes.Offline), "Datei weiterhin „nur online“");

        // (4) Lightbox: Hinweis „Wird von OneDrive geladen …“, danach das Original ohne Fehler
        _gallery.OpenInLightboxCommand.Execute(item);
        await WaitUntilAsync(() => _lightbox.IsOpen, ShortWait, "Lightbox geöffnet").ConfigureAwait(true);
        var window = Application.Current.Windows.OfType<LightboxWindow>().First();
        var vm = window.ViewModel;
        var sawNotice = false;
        await WaitUntilAsync(
            () =>
            {
                sawNotice |= vm.CloudNotice?.StartsWith(LightboxViewModel.CloudLoadingText, StringComparison.Ordinal) == true;
                return vm.CurrentItem?.FileId == id && sawNotice;
            },
            ShortWait,
            "Hinweis „Wird von OneDrive geladen …“").ConfigureAwait(true);
        await SnapshotAsync("onedrive-lightbox", window).ConfigureAwait(true);
        await WaitUntilAsync(() => !vm.IsCloudLoading && !vm.IsLoading && vm.ImageSource is not null && !vm.HasCloudNotice, LongWait, "Original geladen").ConfigureAwait(true);
        Check(vm.ErrorMessage is null && vm.IsCurrentCloudFile, "Lightbox: Original angezeigt, kein Fehler, Datei in OneDrive");
        vm.CloseCommand.Execute(null);
        await WaitUntilAsync(() => !_lightbox.IsOpen, ShortWait, "Lightbox geschlossen").ConfigureAwait(true);

        // (5) Aufräumen: Attribut weg, Ordner in den Test-Papierkorb (Katalog sauber)
        File.SetAttributes(path, FileAttributes.Normal);
        _navigation.NavigateTo(NavigationTags.Folders);
        await WaitUntilAsync(() => !_folders.IsLoading && FindFolderNode(CloudDir) is not null, ShortWait, "Smoke-OneDrive im Ordnerbaum").ConfigureAwait(true);
        await _folders.RecycleFolderCommand.ExecuteAsync(FindFolderNode(CloudDir)).ConfigureAwait(true);
        var left = await DbScalarAsync<long>("SELECT COUNT(*) FROM Files WHERE Name = $n", ("$n", CloudFileName)).ConfigureAwait(true);
        Check(!Directory.Exists(CloudDir) && left == 0, "Aufgeräumt: Ordner weg, keine Katalogzeile mehr");
        _mediaCount -= 1;
        _folders.DismissResultCommand.Execute(null);
        _messenger.Send(new FolderSelectedMessage(_libraryId, null, true));
    }

    /// <summary>Rückstand eines abgebrochenen Laufs: Attribut zurücksetzen, Ordner löschen (nur in der Kopie der Testbibliothek).</summary>
    private void RemoveCloudLeftovers()
    {
        if (!Directory.Exists(CloudDir))
        {
            return;
        }

        foreach (var file in Directory.EnumerateFiles(CloudDir, "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(file, FileAttributes.Normal);
        }

        Directory.Delete(CloudDir, recursive: true);
    }
}
