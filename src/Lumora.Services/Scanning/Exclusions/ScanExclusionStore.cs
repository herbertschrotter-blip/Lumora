using System.Text.Json;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.EntityFrameworkCore;
using Lumora.Data;
using Lumora.Domain.Entities;
using Lumora.Services.Database;
using Lumora.Services.Messaging;

namespace Lumora.Services.Scanning.Exclusions;

/// <summary>Ein ausgeschlossener Ordner einer Bibliothek (ADR-0066): gespeichert relativ zu seiner Wurzel, für die Anzeige aufgelöst.</summary>
/// <param name="RootId">Wurzel (LibraryRoots.Id), unter der der Ordner liegt.</param>
/// <param name="RelativePath">Pfad relativ zur Wurzel („2020\Privat“).</param>
/// <param name="FullPath">Voller Pfad mit dem aktuellen Wurzelpfad.</param>
/// <param name="RootName">Name der Wurzel (Anzeige).</param>
public sealed record ScanExclusionFolder(int LibraryId, int RootId, string RelativePath, string FullPath, string RootName);

/// <summary>
/// Ausschlüsse beim Einlesen (ADR-0066) in der Settings-Tabelle des aktuellen Katalogs — eine versteckte Sitzung hat ihren
/// eigenen Katalog und damit ihre eigene Liste (ADR-0049), nichts liegt außerhalb.
/// <list type="bullet">
/// <item><c>scan.excludedNames</c>: allgemeine Liste (JSON-Array von Ordnernamen) für alle Bibliotheken des Katalogs. Fehlt der
/// Eintrag (Neuinstallation, bestehender Katalog vor 0.4.33), gelten die Standardnamen <c>.thumbs</c> und <c>.temp</c>.</item>
/// <item><c>scan.excludedFolders.{LibraryId}</c>: Ordner dieser Bibliothek (JSON-Array <c>{"root":Id,"path":"relativ"}</c>) —
/// gehört der Bibliothek, nicht der Gruppe (ADR-0048). Einträge einer entfernten Wurzel werden ignoriert.</item>
/// </list>
/// Jede Änderung sendet <see cref="ScanExclusionsChangedMessage"/>.
/// </summary>
public interface IScanExclusionStore
{
    /// <summary>Allgemeine Liste (Standard: .thumbs, .temp).</summary>
    Task<IReadOnlyList<string>> GetFolderNamesAsync(CancellationToken ct = default);

    /// <summary>Allgemeine Liste ersetzen (Namen werden geprüft und ohne Dubletten gespeichert; leer ist erlaubt).</summary>
    Task SetFolderNamesAsync(IReadOnlyList<string> names, CancellationToken ct = default);

    /// <summary>Ausgeschlossene Ordner der Bibliothek (nur solche unter einer noch eingetragenen Wurzel).</summary>
    Task<IReadOnlyList<ScanExclusionFolder>> GetFoldersAsync(int libraryId, CancellationToken ct = default);

    /// <summary>
    /// Ordner ausschließen; false, wenn er (oder ein Elternordner) schon ausgeschlossen ist. Ausschlüsse darunter bleiben stehen.
    /// Wurzel bzw. Ordner außerhalb → Ausnahme.
    /// </summary>
    Task<bool> AddFolderAsync(int libraryId, string folderPath, CancellationToken ct = default);

    /// <summary>Ausschluss aufheben; false, wenn der Ordner nicht ausgeschlossen war.</summary>
    Task<bool> RemoveFolderAsync(int libraryId, string folderPath, CancellationToken ct = default);

    /// <summary>
    /// Nach Umbenennen/Verschieben eines Ordners: Ausschlüsse unterhalb von <paramref name="oldPath"/> ziehen nach
    /// <paramref name="newPath"/> um (auch unter eine andere Wurzel derselben Bibliothek).
    /// </summary>
    Task MoveFoldersAsync(int libraryId, string oldPath, string newPath, CancellationToken ct = default);

    /// <summary>Alle Regeln der Bibliothek zum Prüfen.</summary>
    Task<ScanExclusionSet> LoadSetAsync(int libraryId, CancellationToken ct = default);
}

/// <inheritdoc cref="IScanExclusionStore"/>
public sealed class ScanExclusionStore : IScanExclusionStore
{
    public const string NamesKey = "scan.excludedNames";
    public const string FoldersKeyPrefix = "scan.excludedFolders.";

    /// <summary>Standard der allgemeinen Liste: Vorschau- und Arbeitsordner der alten PowerShell-Viewer V1/V2.</summary>
    public static IReadOnlyList<string> DefaultFolderNames { get; } = [".thumbs", ".temp"];

    private static readonly char[] InvalidNameChars = ['\\', '/', ':', '*', '?', '"', '<', '>', '|'];

    private readonly ILibraryContextFactory _factory;
    private readonly IMessenger _messenger;
    private readonly SemaphoreSlim _lock = new(1, 1);

    /// <param name="messenger">Optional (Tests); produktiv WeakReferenceMessenger.Default.</param>
    public ScanExclusionStore(ILibraryContextFactory factory, IMessenger? messenger = null)
    {
        _factory = factory;
        _messenger = messenger ?? WeakReferenceMessenger.Default;
    }

    public static string FoldersKey(int libraryId) => FoldersKeyPrefix + libraryId.ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>Prüft einen Ordnernamen der allgemeinen Liste; liefert ihn getrimmt oder wirft mit verständlicher Meldung.</summary>
    public static string NormalizeName(string? name)
    {
        var trimmed = (name ?? string.Empty).Trim();
        if (trimmed.Length == 0)
        {
            throw new ArgumentException("Bitte einen Ordnernamen eingeben.");
        }

        if (trimmed is "." or ".." || trimmed.IndexOfAny(InvalidNameChars) >= 0 || trimmed.Any(char.IsControl))
        {
            throw new ArgumentException($"„{trimmed}“ ist kein gültiger Ordnername (nur der Name, ohne \\ / : * ? \" < > |).");
        }

        if (trimmed.Length > 255)
        {
            throw new ArgumentException("Der Ordnername ist zu lang.");
        }

        return trimmed;
    }

    public async Task<IReadOnlyList<string>> GetFolderNamesAsync(CancellationToken ct = default)
    {
        await using var ctx = _factory.Create();
        return await ReadNamesAsync(ctx, ct).ConfigureAwait(false);
    }

    public async Task SetFolderNamesAsync(IReadOnlyList<string> names, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(names);
        var clean = new List<string>();
        foreach (var name in names)
        {
            var n = NormalizeName(name);
            if (!clean.Contains(n, StringComparer.OrdinalIgnoreCase))
            {
                clean.Add(n);
            }
        }

        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var ctx = _factory.Create();
            await UpsertAsync(ctx, NamesKey, JsonSerializer.Serialize(clean), ct).ConfigureAwait(false);
            await ctx.SaveChangesAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _lock.Release();
        }

        Send(null);
    }

    public async Task<IReadOnlyList<ScanExclusionFolder>> GetFoldersAsync(int libraryId, CancellationToken ct = default)
    {
        await using var ctx = _factory.Create();
        var roots = await LoadRootsAsync(ctx, libraryId, ct).ConfigureAwait(false);
        var entries = await ReadEntriesAsync(ctx, libraryId, ct).ConfigureAwait(false);
        return Resolve(libraryId, entries, roots);
    }

    public async Task<bool> AddFolderAsync(int libraryId, string folderPath, CancellationToken ct = default)
    {
        var full = ScanExclusionSet.Normalize(folderPath);
        if (full.Length == 0)
        {
            throw new ArgumentException("Ungültiger Ordnerpfad.", nameof(folderPath));
        }

        bool added;
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var ctx = _factory.Create();
            var roots = await LoadRootsAsync(ctx, libraryId, ct).ConfigureAwait(false);
            var root = RootOf(roots, full)
                ?? throw new InvalidOperationException("Der Ordner liegt unter keiner Wurzel dieser Bibliothek.");
            if (string.Equals(ScanExclusionSet.Normalize(root.Path), full, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("Eine Wurzel der Bibliothek lässt sich nicht ausschließen — Wurzeln trägst du unter Einstellungen › Bibliotheken & Eingänge aus.");
            }

            var entries = await ReadEntriesAsync(ctx, libraryId, ct).ConfigureAwait(false);
            var resolved = Resolve(libraryId, entries, roots);
            if (resolved.Any(e => ScanExclusionSet.IsInside(full, ScanExclusionSet.Normalize(e.FullPath))))
            {
                return false; // schon ausgeschlossen (selbst oder über einen Elternordner)
            }

            // Ausschlüsse darunter bleiben stehen (überflüssig, solange der neue gilt) — hebt man den neuen wieder auf (auch über
            // „Rückgängig“), gelten sie weiter
            var kept = resolved.Select(e => new Entry(e.RootId, e.RelativePath)).ToList();
            kept.Add(new Entry(root.Id, Relative(root.Path, full)));
            await WriteEntriesAsync(ctx, libraryId, kept, ct).ConfigureAwait(false);
            added = true;
        }
        finally
        {
            _lock.Release();
        }

        if (added)
        {
            Send(libraryId);
        }

        return added;
    }

    public async Task<bool> RemoveFolderAsync(int libraryId, string folderPath, CancellationToken ct = default)
    {
        var full = ScanExclusionSet.Normalize(folderPath);
        bool removed;
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var ctx = _factory.Create();
            var roots = await LoadRootsAsync(ctx, libraryId, ct).ConfigureAwait(false);
            var entries = await ReadEntriesAsync(ctx, libraryId, ct).ConfigureAwait(false);
            var resolved = Resolve(libraryId, entries, roots);
            var kept = resolved.Where(e => !string.Equals(ScanExclusionSet.Normalize(e.FullPath), full, StringComparison.OrdinalIgnoreCase))
                .Select(e => new Entry(e.RootId, e.RelativePath))
                .ToList();
            removed = kept.Count != resolved.Count;
            if (removed || kept.Count != entries.Count)
            {
                await WriteEntriesAsync(ctx, libraryId, kept, ct).ConfigureAwait(false); // entfernt dabei verwaiste Einträge
            }
        }
        finally
        {
            _lock.Release();
        }

        if (removed)
        {
            Send(libraryId);
        }

        return removed;
    }

    public async Task MoveFoldersAsync(int libraryId, string oldPath, string newPath, CancellationToken ct = default)
    {
        var from = ScanExclusionSet.Normalize(oldPath);
        var to = ScanExclusionSet.Normalize(newPath);
        var changed = false;
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var ctx = _factory.Create();
            var entries = await ReadEntriesAsync(ctx, libraryId, ct).ConfigureAwait(false);
            if (entries.Count == 0)
            {
                return;
            }

            var roots = await LoadRootsAsync(ctx, libraryId, ct).ConfigureAwait(false);
            var result = new List<Entry>();
            foreach (var e in Resolve(libraryId, entries, roots))
            {
                var full = ScanExclusionSet.Normalize(e.FullPath);
                if (!ScanExclusionSet.IsInside(full, from))
                {
                    result.Add(new Entry(e.RootId, e.RelativePath));
                    continue;
                }

                var moved = to + full[from.Length..];
                if (RootOf(roots, moved) is { } root && !string.Equals(ScanExclusionSet.Normalize(root.Path), moved, StringComparison.OrdinalIgnoreCase))
                {
                    result.Add(new Entry(root.Id, Relative(root.Path, moved)));
                }

                changed = true; // verschoben oder (Ziel unter keiner Wurzel bzw. selbst Wurzel) entfallen
            }

            if (changed)
            {
                await WriteEntriesAsync(ctx, libraryId, result, ct).ConfigureAwait(false);
            }
        }
        finally
        {
            _lock.Release();
        }

        if (changed)
        {
            Send(libraryId);
        }
    }

    public async Task<ScanExclusionSet> LoadSetAsync(int libraryId, CancellationToken ct = default)
    {
        await using var ctx = _factory.Create();
        var names = await ReadNamesAsync(ctx, ct).ConfigureAwait(false);
        var roots = await LoadRootsAsync(ctx, libraryId, ct).ConfigureAwait(false);
        var entries = await ReadEntriesAsync(ctx, libraryId, ct).ConfigureAwait(false);
        var folders = Resolve(libraryId, entries, roots);
        return new ScanExclusionSet(names, folders.Select(f => f.FullPath), roots.Select(r => r.Path));
    }

    // ------------------------------------------------------------------ Helfer

    private sealed record Entry(int Root, string Path);

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private static async Task<IReadOnlyList<string>> ReadNamesAsync(LibraryContext ctx, CancellationToken ct)
    {
        var value = await ctx.Settings.AsNoTracking().Where(s => s.Key == NamesKey).Select(s => s.Value).FirstOrDefaultAsync(ct).ConfigureAwait(false);
        if (value is null)
        {
            return DefaultFolderNames;
        }

        try
        {
            return (JsonSerializer.Deserialize<List<string>>(value) ?? []).Where(n => !string.IsNullOrWhiteSpace(n)).Select(n => n.Trim()).ToList();
        }
        catch (JsonException)
        {
            return DefaultFolderNames; // beschädigter Eintrag: lieber die Standardnamen als gar nichts
        }
    }

    private static async Task<List<Entry>> ReadEntriesAsync(LibraryContext ctx, int libraryId, CancellationToken ct)
    {
        var key = FoldersKey(libraryId);
        var value = await ctx.Settings.AsNoTracking().Where(s => s.Key == key).Select(s => s.Value).FirstOrDefaultAsync(ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(value))
        {
            return [];
        }

        try
        {
            return (JsonSerializer.Deserialize<List<Entry>>(value, JsonOptions) ?? []).Where(e => !string.IsNullOrWhiteSpace(e.Path)).ToList();
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static async Task WriteEntriesAsync(LibraryContext ctx, int libraryId, List<Entry> entries, CancellationToken ct)
    {
        var key = FoldersKey(libraryId);
        if (entries.Count == 0)
        {
            await ctx.Settings.Where(s => s.Key == key).ExecuteDeleteAsync(ct).ConfigureAwait(false);
            return;
        }

        await UpsertAsync(ctx, key, JsonSerializer.Serialize(entries, JsonOptions), ct).ConfigureAwait(false);
        await ctx.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    private static async Task UpsertAsync(LibraryContext ctx, string key, string value, CancellationToken ct)
    {
        var row = await ctx.Settings.FindAsync([key], ct).ConfigureAwait(false);
        if (row is null)
        {
            ctx.Settings.Add(new Setting { Key = key, Value = value });
        }
        else
        {
            row.Value = value;
        }
    }

    private static Task<List<LibraryRoot>> LoadRootsAsync(LibraryContext ctx, int libraryId, CancellationToken ct)
        => ctx.LibraryRoots.AsNoTracking().Where(r => r.LibraryId == libraryId).ToListAsync(ct);

    private static List<ScanExclusionFolder> Resolve(int libraryId, List<Entry> entries, List<LibraryRoot> roots)
    {
        var result = new List<ScanExclusionFolder>(entries.Count);
        foreach (var e in entries)
        {
            var root = roots.FirstOrDefault(r => r.Id == e.Root);
            if (root is null)
            {
                continue; // Wurzel ausgetragen: Eintrag gilt nicht mehr
            }

            var full = ScanExclusionSet.Normalize(Path.Combine(root.Path, e.Path));
            if (full.Length == 0 || result.Any(x => string.Equals(x.FullPath, full, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            result.Add(new ScanExclusionFolder(libraryId, root.Id, e.Path, full, string.IsNullOrWhiteSpace(root.Name) ? Path.GetFileName(full) : root.Name));
        }

        return result.OrderBy(r => r.FullPath, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    private static LibraryRoot? RootOf(List<LibraryRoot> roots, string full)
        => roots.Where(r => ScanExclusionSet.IsInside(full, ScanExclusionSet.Normalize(r.Path)))
            .OrderByDescending(r => r.Path.Length)
            .FirstOrDefault();

    private static string Relative(string rootPath, string full)
        => full[ScanExclusionSet.Normalize(rootPath).Length..].TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    private void Send(int? libraryId)
    {
        try
        {
            _messenger.Send(new ScanExclusionsChangedMessage(libraryId));
        }
        catch (Exception)
        {
            // Empfänger-Fehler dürfen das Speichern nicht rückgängig machen
        }
    }
}
