using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Lumora.Domain.Entities;
using Lumora.Domain.Enums;
using Lumora.Services.Database;
using Lumora.Services.Library;

namespace Lumora.Services.Operations;

/// <summary>
/// Standard-Implementierung von <see cref="IFolderAdoptionService"/> (ADR-0034): Kontext aus Katalog-Wurzeln, Quellbaum
/// über <see cref="FolderAdoptionSourceReader"/>, Plan über <see cref="FolderAdoptionPlanner"/>, Ausführung über
/// <see cref="IFileOperationService.AdoptFolderAsync"/> und Austragen des Eingangs über <see cref="ILibraryService.RemoveRootAsync"/>.
/// </summary>
public sealed class FolderAdoptionService : IFolderAdoptionService
{
    /// <summary>Gleicher Schlüssel wie „In die Bibliothek…“ (ArchiveMoveService): ein gemeinsames „zuletzt benutzt“.</summary>
    public const string LastTargetSettingPrefix = "archive.lastTarget.";

    private readonly ILibraryContextFactory _factory;
    private readonly IFileOperationService _fileOps;
    private readonly ILibraryService _libraries;
    private readonly ILogger<FolderAdoptionService> _logger;
    private readonly Scanning.Exclusions.IScanExclusionStore? _exclusions;

    /// <param name="exclusions">Ausschlüsse beim Einlesen (ADR-0066): ausgeschlossene Ordner der Quelle bleiben liegen; optional (Tests).</param>
    public FolderAdoptionService(
        ILibraryContextFactory factory,
        IFileOperationService fileOps,
        ILibraryService libraries,
        ILogger<FolderAdoptionService> logger,
        Scanning.Exclusions.IScanExclusionStore? exclusions = null)
    {
        _exclusions = exclusions;
        _factory = factory;
        _fileOps = fileOps;
        _libraries = libraries;
        _logger = logger;
    }

    public async Task<FolderAdoptionContext?> GetContextAsync(string sourceFolder, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceFolder);
        var source = Path.TrimEndingDirectorySeparator(Path.GetFullPath(sourceFolder));
        await using var ctx = _factory.Create();
        var roots = await ctx.LibraryRoots.AsNoTracking().ToListAsync(ct).ConfigureAwait(false);
        var inbox = roots
            .Where(r => IsSameOrInside(source, r.Path))
            .OrderByDescending(r => r.Path.Length)
            .FirstOrDefault();
        if (inbox is not { Role: LibraryRootRole.Inbox })
        {
            return null;
        }

        var archive = roots.FirstOrDefault(r => r.LibraryId == inbox.LibraryId && r.Role == LibraryRootRole.Archive);
        if (archive is null)
        {
            return null;
        }

        var libraryName = await ctx.Libraries.AsNoTracking()
            .Where(l => l.Id == inbox.LibraryId)
            .Select(l => l.Name)
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false) ?? string.Empty;
        var lastTarget = await ctx.Settings.AsNoTracking()
            .Where(s => s.Key == LastTargetSettingPrefix + inbox.LibraryId)
            .Select(s => s.Value)
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);
        if (lastTarget is not null && !IsSameOrInside(Path.TrimEndingDirectorySeparator(lastTarget), archive.Path))
        {
            lastTarget = null; // Bibliotheksordner inzwischen gewechselt
        }

        return new FolderAdoptionContext(
            inbox.LibraryId,
            libraryName,
            source,
            inbox.Id,
            Path.TrimEndingDirectorySeparator(inbox.Path),
            inbox.Name,
            archive.Id,
            Path.TrimEndingDirectorySeparator(archive.Path),
            archive.Name,
            lastTarget);
    }

    public async Task<AdoptionSourceSnapshot> ReadSourceAsync(string sourceFolder, CancellationToken ct = default)
    {
        Scanning.Exclusions.ScanExclusionSet? exclusions = null;
        if (_exclusions is not null)
        {
            // Bibliothek der Wurzel, unter der die Quelle liegt (ADR-0066)
            var source = Path.TrimEndingDirectorySeparator(Path.GetFullPath(sourceFolder));
            await using var ctx = _factory.Create();
            var roots = await ctx.LibraryRoots.AsNoTracking().ToListAsync(ct).ConfigureAwait(false);
            var root = roots.Where(r => IsSameOrInside(source, r.Path)).OrderByDescending(r => r.Path.Length).FirstOrDefault();
            if (root is not null)
            {
                exclusions = await _exclusions.LoadSetAsync(root.LibraryId, ct).ConfigureAwait(false);
            }
        }

        return await Task.Run(() => FolderAdoptionSourceReader.Read(sourceFolder, ct, exclusions), ct).ConfigureAwait(false);
    }

    public FolderAdoptionPlan BuildPlan(AdoptionSourceSnapshot snapshot, string targetFolder, bool collapseEmptyChains)
        => FolderAdoptionPlanner.Plan(snapshot, targetFolder, collapseEmptyChains, FileSystemAdoptionTargetProbe.Instance);

    public async Task<FolderAdoptionOutcome> ExecuteAsync(
        FolderAdoptionContext context,
        FolderAdoptionPlan plan,
        FolderAdoptionOptions options,
        IProgress<int>? progress = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(options);
        if (!IsSameOrInside(Path.TrimEndingDirectorySeparator(plan.TargetFolder), context.ArchiveRootPath))
        {
            throw new InvalidOperationException("Der Zielordner muss im Bibliotheksordner liegen.");
        }

        var unregister = options.UnregisterInbox && context.IsWholeInbox;
        var keepSourceFolder = context.IsWholeInbox && !unregister; // Eingang bleibt eingetragen → Ordner bleibt, nur der Inhalt geht
        var execution = await Task.Run(
            () => _fileOps.AdoptFolderAsync(context.LibraryId, plan, options.RecycleSource, keepSourceFolder, progress, ct),
            ct).ConfigureAwait(false);
        await RememberTargetAsync(context.LibraryId, plan.TargetFolder).ConfigureAwait(false);

        var unregistered = false;
        if (unregister && execution is { Failed: 0, Moved: > 0, RemainingFiles.Count: 0 })
        {
            unregistered = await TryUnregisterInboxAsync(context).ConfigureAwait(false);
        }

        var summary = Summarize(context, plan, options, execution, unregister, unregistered);
        _logger.LogInformation("Übernahme {Source}: {Summary}", context.SourceFolder, summary);
        return new FolderAdoptionOutcome(execution, unregistered, summary);
    }

    /// <summary>Eingang nur austragen, wenn keine Katalogdatei mehr unter ihm liegt (RemoveRootAsync entfernt sie sonst samt Bewertungen).</summary>
    private async Task<bool> TryUnregisterInboxAsync(FolderAdoptionContext context)
    {
        try
        {
            int left;
            await using (var ctx = _factory.Create())
            {
                var rootId = context.InboxRootId;
                left = await ctx.Files.AsNoTracking()
                    .CountAsync(f => ctx.Folders.Any(d => d.Id == f.FolderId && d.RootId == rootId))
                    .ConfigureAwait(false);
            }

            if (left > 0)
            {
                _logger.LogWarning("Übernahme: Eingang {Root} bleibt eingetragen — noch {Count} Katalogdatei(en) darunter", context.InboxRootPath, left);
                return false;
            }

            await _libraries.RemoveRootAsync(context.InboxRootId).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Übernahme: Eingang {Root} konnte nicht ausgetragen werden", context.InboxRootPath);
            return false;
        }
    }

    private async Task RememberTargetAsync(int libraryId, string target)
    {
        try
        {
            await using var ctx = _factory.Create();
            var key = LastTargetSettingPrefix + libraryId;
            var setting = await ctx.Settings.FirstOrDefaultAsync(s => s.Key == key).ConfigureAwait(false);
            if (setting is null)
            {
                ctx.Settings.Add(new Setting { Key = key, Value = target });
            }
            else
            {
                setting.Value = target;
            }

            await ctx.SaveChangesAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Übernahme: Zielordner konnte nicht gemerkt werden");
        }
    }

    internal static string Summarize(
        FolderAdoptionContext context,
        FolderAdoptionPlan plan,
        FolderAdoptionOptions options,
        FolderAdoptionExecution execution,
        bool unregisterRequested,
        bool unregistered)
    {
        var parts = new List<string>
        {
            $"„{Path.GetFileName(context.SourceFolder)}“: {Count(execution.Moved)} in die Bibliothek übernommen → {TargetDisplay(context, plan.TargetFolder)}",
        };
        if (execution.Renamed > 0)
        {
            parts.Add($"{execution.Renamed:N0} wegen gleicher Namen umbenannt");
        }

        if (execution.Failed > 0)
        {
            parts.Add($"{Count(execution.Failed)} nicht übernommen");
        }

        if (options.RecycleSource)
        {
            parts.Add(execution.SourceRecycled
                ? context.IsWholeInbox && !unregisterRequested ? "Inhalt des Eingangs im Papierkorb" : "Quellordner im Papierkorb"
                : execution.RecycleNote ?? "Quellordner bleibt");
        }

        if (unregisterRequested)
        {
            parts.Add(unregistered ? $"Eingang „{context.InboxRootName}“ ausgetragen" : "Eingang bleibt eingetragen");
        }

        return string.Join(" · ", parts) + ".";
    }

    private static string Count(int n) => n == 1 ? "1 Datei" : $"{n:N0} Dateien";

    /// <summary>Ziel kurz: Name des Bibliotheksordners plus relativer Pfad (z. B. „Fotos\2026\Reise“).</summary>
    private static string TargetDisplay(FolderAdoptionContext context, string target)
    {
        var relative = Path.GetRelativePath(context.ArchiveRootPath, target);
        return relative == "." ? context.ArchiveRootName : Path.Combine(context.ArchiveRootName, relative);
    }

    private static bool IsSameOrInside(string path, string folder)
    {
        var f = Path.TrimEndingDirectorySeparator(folder);
        var prefix = f.EndsWith(Path.DirectorySeparatorChar) ? f : f + Path.DirectorySeparatorChar; // eine Laufwerkswurzel behält ihr Trennzeichen
        return string.Equals(path, f, StringComparison.OrdinalIgnoreCase)
               || path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }
}
