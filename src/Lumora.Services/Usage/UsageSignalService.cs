using System.Globalization;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Lumora.Services.Database;
using Lumora.Services.Messaging;
using Lumora.Services.Quality;

namespace Lumora.Services.Usage;

/// <summary>
/// Nutzungs-Signale (ADR-0072) über Raw-SQL im Katalog der aktuellen Sitzung — eine versteckte Bibliothek hat ihren
/// eigenen Katalog im Container, also landet dort auch ihre Nutzung und nichts außerhalb. Meldungen werden je (Datei, Tag)
/// im Speicher summiert und nach <see cref="FlushDelay"/> in EINER Transaktion addiert (UPSERT); ob „Nutzung lernen“ an
/// ist, liest der Schreibvorgang selbst in dieser Transaktion (ausgeschaltet → verworfen). Gelöschte Dateien fallen still
/// heraus. Protokoll nur mit Zahlen, nie mit Pfaden oder Namen.
/// </summary>
public sealed class UsageSignalService : IUsageSignals, IUsageService, IDisposable
{
    /// <summary>So lange werden Meldungen gesammelt, bevor sie geschrieben werden.</summary>
    public static readonly TimeSpan FlushDelay = TimeSpan.FromSeconds(3);

    /// <summary>„Lange nicht angesehen“ bzw. „vor über einem Jahr aufgenommen“ (Vergessene Schätze).</summary>
    public const int ForgottenDays = 365;

    private const int ChunkSize = 500;

    private readonly ILibraryContextFactory _factory;
    private readonly ILogger _logger;
    private readonly IMessenger _messenger;
    private readonly object _sync = new();
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private Dictionary<(int FileId, int Day), Counters> _pending = new();
    private bool _flushScheduled;
    private volatile bool _enabled = true;
    private bool _disposed;

    public UsageSignalService(ILibraryContextFactory factory, ILogger<UsageSignalService>? logger = null, IMessenger? messenger = null, TimeProvider? time = null)
    {
        _factory = factory;
        _logger = logger ?? (ILogger)NullLogger.Instance;
        _messenger = messenger ?? WeakReferenceMessenger.Default;
        Time = time ?? TimeProvider.System;
        _messenger.Register<UsageSignalService, UsageSignalMessage>(this, static (r, m) => r.Record(m.FileIds, m.Kind));
    }

    public TimeProvider Time { get; }

    public bool IsEnabled => _enabled;

    /// <summary>Gesammelte, noch nicht geschriebene (Datei, Tag)-Einträge (Tests).</summary>
    internal int PendingCount
    {
        get
        {
            lock (_sync)
            {
                return _pending.Count;
            }
        }
    }

    // ---- Melden ----------------------------------------------------------------------------------------------------

    public void RecordView(int fileId, TimeSpan activeTime, bool zoomed, UsageViewContext context)
    {
        if (!_enabled || context == UsageViewContext.None || activeTime < UsageScoring.MinView || fileId <= 0)
        {
            return; // Durchblättern im Schnelllauf, „Doppelte“, abgeschaltet
        }

        var seconds = (int)Math.Round(Math.Min(activeTime.TotalSeconds, UsageScoring.MaxView.TotalSeconds));
        Add(fileId, c =>
        {
            switch (context)
            {
                case UsageViewContext.Normal:
                    c.Opens++;
                    c.Seconds += seconds;
                    c.Zooms += zoomed ? 1 : 0;
                    break;
                case UsageViewContext.Story:
                    c.SoftOpens++;
                    c.SoftSeconds += seconds;
                    c.Zooms += zoomed ? 1 : 0;
                    break;
                default: // Diashow: nur die Zeit, gedämpft
                    c.SoftSeconds += seconds;
                    break;
            }
        });
    }

    public void Record(IReadOnlyCollection<int> fileIds, UsageKind kind)
    {
        ArgumentNullException.ThrowIfNull(fileIds);
        if (!_enabled)
        {
            return;
        }

        foreach (var fileId in fileIds.Where(id => id > 0).Distinct())
        {
            Add(fileId, c =>
            {
                if (kind == UsageKind.Shared)
                {
                    c.Shares++;
                }
                else
                {
                    c.Uses++;
                }
            });
        }
    }

    private void Add(int fileId, Action<Counters> change)
    {
        var day = UsageScoring.DayNumber(DateOnly.FromDateTime(Time.GetLocalNow().DateTime));
        bool schedule;
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            if (!_pending.TryGetValue((fileId, day), out var counters))
            {
                _pending[(fileId, day)] = counters = new Counters();
            }

            change(counters);
            schedule = !_flushScheduled;
            _flushScheduled = true;
        }

        if (schedule)
        {
            _ = Task.Delay(FlushDelay, Time).ContinueWith(_ => FlushAsync(), CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default).Unwrap();
        }
    }

    public async Task FlushAsync(CancellationToken ct = default)
    {
        Dictionary<(int FileId, int Day), Counters> batch;
        lock (_sync)
        {
            batch = _pending;
            _pending = new();
            _flushScheduled = false;
        }

        if (batch.Count == 0)
        {
            return;
        }

        await _writeGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var written = await Task.Run(() => WriteAsync(batch, ct), ct).ConfigureAwait(false);
            _logger.LogDebug("Nutzungs-Signale: {Rows} Tageseinträge geschrieben ({Received} gemeldet)", written, batch.Count);
        }
        catch (DatabaseSuspendedException)
        {
            Requeue(batch); // Katalog zieht gerade um: später erneut
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Nutzungs-Signale: {Count} Tageseinträge nicht geschrieben", batch.Count);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    private void Requeue(Dictionary<(int FileId, int Day), Counters> batch)
    {
        bool schedule;
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            foreach (var (key, counters) in batch)
            {
                if (_pending.TryGetValue(key, out var existing))
                {
                    existing.Merge(counters);
                }
                else
                {
                    _pending[key] = counters;
                }
            }

            schedule = !_flushScheduled;
            _flushScheduled = true;
        }

        if (schedule)
        {
            _ = Task.Delay(FlushDelay, Time).ContinueWith(_ => FlushAsync(), CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default).Unwrap();
        }
    }

    private async Task<int> WriteAsync(Dictionary<(int FileId, int Day), Counters> batch, CancellationToken ct)
    {
        using var connection = _factory.OpenSqliteConnection();
        using var tx = await _factory.BusyRetry.BeginTransactionAsync(connection, ct).ConfigureAwait(false);
        var settings = ReadSettings(connection, tx);
        _enabled = settings.Enabled;
        if (!settings.Enabled)
        {
            return 0; // ausgeschaltet, während gesammelt wurde
        }

        using var cmd = connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO FileUsageDays (FileId, Day, Opens, Seconds, Zooms, SoftOpens, SoftSeconds, Uses, Shares)
            SELECT $f, $d, $o, $s, $z, $so, $ss, $u, $sh WHERE EXISTS (SELECT 1 FROM Files WHERE Id = $f)
            ON CONFLICT (FileId, Day) DO UPDATE SET
                Opens = Opens + excluded.Opens, Seconds = Seconds + excluded.Seconds, Zooms = Zooms + excluded.Zooms,
                SoftOpens = SoftOpens + excluded.SoftOpens, SoftSeconds = SoftSeconds + excluded.SoftSeconds,
                Uses = Uses + excluded.Uses, Shares = Shares + excluded.Shares;
            """;
        var f = cmd.Parameters.Add("$f", SqliteType.Integer);
        var d = cmd.Parameters.Add("$d", SqliteType.Integer);
        var o = cmd.Parameters.Add("$o", SqliteType.Integer);
        var s = cmd.Parameters.Add("$s", SqliteType.Integer);
        var z = cmd.Parameters.Add("$z", SqliteType.Integer);
        var so = cmd.Parameters.Add("$so", SqliteType.Integer);
        var ss = cmd.Parameters.Add("$ss", SqliteType.Integer);
        var u = cmd.Parameters.Add("$u", SqliteType.Integer);
        var sh = cmd.Parameters.Add("$sh", SqliteType.Integer);
        var written = 0;
        var firstDay = int.MaxValue;
        foreach (var ((fileId, day), c) in batch)
        {
            f.Value = fileId;
            d.Value = day;
            o.Value = c.Opens;
            s.Value = c.Seconds;
            z.Value = c.Zooms;
            so.Value = c.SoftOpens;
            ss.Value = c.SoftSeconds;
            u.Value = c.Uses;
            sh.Value = c.Shares;
            if (cmd.ExecuteNonQuery() > 0)
            {
                written++;
                firstDay = Math.Min(firstDay, day);
            }
        }

        if (written > 0)
        {
            using var since = connection.CreateCommand();
            since.Transaction = tx;
            since.CommandText = "INSERT OR IGNORE INTO Settings (Key, Value) VALUES ($k, $v);";
            since.Parameters.AddWithValue("$k", UsageSettings.SinceKey);
            since.Parameters.AddWithValue("$v", firstDay.ToString(CultureInfo.InvariantCulture));
            since.ExecuteNonQuery();
        }

        tx.Commit();
        return written;
    }

    // ---- Einstellungen ---------------------------------------------------------------------------------------------

    public Task<UsageSettings> GetSettingsAsync(CancellationToken ct = default)
        => ReadAsync(c =>
        {
            var settings = ReadSettings(c, null);
            _enabled = settings.Enabled;
            return settings;
        }, ct);

    public async Task SaveSettingsAsync(UsageSettings settings, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (!settings.Enabled)
        {
            lock (_sync)
            {
                _pending.Clear(); // was seit dem letzten Schreiben gesammelt wurde, gilt nicht mehr
            }
        }

        await WriteSettingsAsync(new Dictionary<string, string?>
        {
            [UsageSettings.EnabledKey] = settings.Enabled ? "true" : "false",
            [UsageSettings.ForgottenKey] = settings.ForgottenEnabled ? "true" : "false",
        }, ct).ConfigureAwait(false);
        _enabled = settings.Enabled;
        _messenger.Send(new UsageChangedMessage(settings));
    }

    private async Task WriteSettingsAsync(Dictionary<string, string?> values, CancellationToken ct)
    {
        await _writeGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await Task.Run(async () =>
            {
                using var connection = _factory.OpenSqliteConnection();
                using var tx = await _factory.BusyRetry.BeginTransactionAsync(connection, ct).ConfigureAwait(false);
                foreach (var (key, value) in values)
                {
                    using var cmd = connection.CreateCommand();
                    cmd.Transaction = tx;
                    cmd.CommandText = value is null
                        ? "DELETE FROM Settings WHERE Key = $k;"
                        : "INSERT INTO Settings (Key, Value) VALUES ($k, $v) ON CONFLICT(Key) DO UPDATE SET Value = excluded.Value;";
                    cmd.Parameters.AddWithValue("$k", key);
                    if (value is not null)
                    {
                        cmd.Parameters.AddWithValue("$v", value);
                    }

                    cmd.ExecuteNonQuery();
                }

                tx.Commit();
            }, ct).ConfigureAwait(false);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    internal static UsageSettings ReadSettings(SqliteConnection connection, SqliteTransaction? tx)
    {
        using var cmd = connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "SELECT Key, Value FROM Settings WHERE Key IN ($a, $b);";
        cmd.Parameters.AddWithValue("$a", UsageSettings.EnabledKey);
        cmd.Parameters.AddWithValue("$b", UsageSettings.ForgottenKey);
        var d = UsageSettings.Default;
        bool enabled = d.Enabled, forgotten = d.ForgottenEnabled;
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            if (reader.IsDBNull(1) || !bool.TryParse(reader.GetString(1), out var value))
            {
                continue;
            }

            if (reader.GetString(0) == UsageSettings.EnabledKey)
            {
                enabled = value;
            }
            else
            {
                forgotten = value;
            }
        }

        return new UsageSettings(enabled, forgotten);
    }

    // ---- Lesen -----------------------------------------------------------------------------------------------------

    public Task<IReadOnlyDictionary<int, UsageTotals>> GetTotalsAsync(IReadOnlyCollection<int> fileIds, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(fileIds);
        return ReadAsync<IReadOnlyDictionary<int, UsageTotals>>(c =>
        {
            var result = new Dictionary<int, UsageTotals>();
            if (fileIds.Count == 0 || !ReadSettings(c, null).Enabled)
            {
                return result;
            }

            foreach (var chunk in fileIds.Distinct().Chunk(ChunkSize))
            {
                ct.ThrowIfCancellationRequested();
                ReadTotals(c, "u.FileId IN (" + string.Join(",", chunk.Select(i => i.ToString(CultureInfo.InvariantCulture))) + ")", result);
            }

            return result;
        }, ct);
    }

    public async Task<UsageTotals?> GetTotalsAsync(int fileId, CancellationToken ct = default)
        => (await GetTotalsAsync([fileId], ct).ConfigureAwait(false)).GetValueOrDefault(fileId);

    private static void ReadTotals(SqliteConnection connection, string where, Dictionary<int, UsageTotals> into)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = $"""
            SELECT u.FileId, SUM(u.Opens), SUM(u.Seconds), SUM(u.Zooms), SUM(u.SoftOpens), SUM(u.SoftSeconds), SUM(u.Uses), SUM(u.Shares),
                   SUM(CASE WHEN u.Opens > 0 OR u.Uses > 0 OR u.Shares > 0 THEN 1 ELSE 0 END), MAX(u.Day)
            FROM FileUsageDays u WHERE {where} GROUP BY u.FileId;
            """;
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            into[reader.GetInt32(0)] = new UsageTotals(
                reader.GetInt32(1), reader.GetInt32(2), reader.GetInt32(3), reader.GetInt32(4), reader.GetInt32(5),
                reader.GetInt32(6), reader.GetInt32(7), reader.GetInt32(8), reader.GetInt32(9));
        }
    }

    public Task<UsageSummary> GetSummaryAsync(CancellationToken ct = default)
        => ReadAsync(c =>
        {
            using var cmd = c.CreateCommand();
            cmd.CommandText = $"""
                SELECT (SELECT COUNT(DISTINCT FileId) FROM FileUsageDays),
                       (SELECT COUNT(DISTINCT Day) FROM FileUsageDays),
                       (SELECT COUNT(*) FROM ({UsageScoring.LovedFileIdsSql()})),
                       (SELECT Value FROM Settings WHERE Key = $since),
                       (SELECT MIN(Day) FROM FileUsageDays);
                """;
            cmd.Parameters.AddWithValue("$since", UsageSettings.SinceKey);
            using var reader = cmd.ExecuteReader();
            reader.Read();
            var files = reader.GetInt32(0);
            if (files == 0)
            {
                return UsageSummary.Empty;
            }

            int? stored = !reader.IsDBNull(3) && int.TryParse(reader.GetString(3), NumberStyles.Integer, CultureInfo.InvariantCulture, out var s) ? s : null;
            int? firstRow = reader.IsDBNull(4) ? null : reader.GetInt32(4);
            int? since = stored is { } a && firstRow is { } b ? Math.Min(a, b) : stored ?? firstRow;
            return new UsageSummary(files, reader.GetInt32(1), reader.GetInt32(2), since is { } day ? UsageScoring.DateOf(day) : null);
        }, ct);

    public async Task<int> EraseAsync(CancellationToken ct = default)
    {
        lock (_sync)
        {
            _pending.Clear();
        }

        int files;
        await _writeGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            files = await Task.Run(async () =>
            {
                using var connection = _factory.OpenSqliteConnection();
                using var tx = await _factory.BusyRetry.BeginTransactionAsync(connection, ct).ConfigureAwait(false);
                using var cmd = connection.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = "SELECT COUNT(DISTINCT FileId) FROM FileUsageDays;";
                var count = Convert.ToInt32(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
                cmd.CommandText = "DELETE FROM FileUsageDays; DELETE FROM Settings WHERE Key = $since;";
                cmd.Parameters.AddWithValue("$since", UsageSettings.SinceKey);
                cmd.ExecuteNonQuery();
                tx.Commit();
                return count;
            }, ct).ConfigureAwait(false);
        }
        finally
        {
            _writeGate.Release();
        }

        _logger.LogInformation("Gelernte Nutzung gelöscht: {Files} Dateien", files);
        var settings = await GetSettingsAsync(ct).ConfigureAwait(false);
        _messenger.Send(new UsageChangedMessage(settings, Erased: true));
        return files;
    }

    public Task<IReadOnlyList<int>> FindForgottenAsync(IReadOnlyCollection<int> libraryIds, DateOnly today, int max, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(libraryIds);
        return ReadAsync<IReadOnlyList<int>>(c =>
        {
            var settings = ReadSettings(c, null);
            if (!settings.Enabled || !settings.ForgottenEnabled || libraryIds.Count == 0 || max <= 0)
            {
                return [];
            }

            var todayNumber = UsageScoring.DayNumber(today);
            var cutoffDay = todayNumber - ForgottenDays;
            var takenBefore = (long)cutoffDay * 86400; // Aufnahme vor über einem Jahr (UTC-Mitternacht genügt)
            var seed = (today.Year * 12) + today.Month; // je Monat eine andere Auswahl
            var libraries = string.Join(",", libraryIds.Select(i => i.ToString(CultureInfo.InvariantCulture)));
            using var cmd = c.CreateCommand();
            cmd.CommandText = $"""
                SELECT f.Id FROM Files f
                LEFT JOIN FileQuality q ON q.FileId = f.Id
                WHERE f.LibraryId IN ({libraries}) AND f.Missing = 0 AND f.MediaType = 0 AND f.CapturedAtSource IS NOT NULL
                  AND COALESCE(f.CapturedAt, f.ModifiedAtUtc) < $before
                  AND f.Id NOT IN (SELECT v.FileId FROM FileUsageDays v WHERE v.Day >= $cutoff AND (v.Opens > 0 OR v.SoftOpens > 0))
                  AND (f.IsFavorite = 1 OR f.Rating >= 4
                       OR ({FileQualityIndexService.ValidQuality("q", "f")} AND q.ErrorCode IS NULL AND q.Sharpness >= 0.55 AND q.Exposure >= 0.6
                           AND (q.Aesthetic IS NULL OR q.Aesthetic >= 0.55))
                       OR f.Id IN ({UsageScoring.LovedFileIdsSql()}))
                ORDER BY (f.Id * 2654435761 + $seed * 40503) % 2147483647
                LIMIT $max;
                """;
            cmd.Parameters.AddWithValue("$before", takenBefore);
            cmd.Parameters.AddWithValue("$cutoff", cutoffDay);
            cmd.Parameters.AddWithValue("$seed", seed);
            cmd.Parameters.AddWithValue("$max", max);
            var ids = new List<int>();
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                ids.Add(reader.GetInt32(0));
            }

            return ids;
        }, ct);
    }

    private Task<T> ReadAsync<T>(Func<SqliteConnection, T> read, CancellationToken ct)
        => Task.Run(() =>
        {
            using var connection = _factory.OpenSqliteConnection();
            return read(connection);
        }, ct);

    public void Dispose()
    {
        _messenger.UnregisterAll(this);
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true; // ab jetzt keine neuen Meldungen; was gesammelt ist, wird unten noch geschrieben
        }

        try
        {
            // Sitzung endet (Beenden, Wechsel normal ↔ versteckt): nichts verlieren. Höchstens eine kurze Transaktion; ein
            // noch ausstehender Verzögerungs-Lauf findet danach nichts mehr vor.
            Task.Run(() => FlushAsync()).Wait(TimeSpan.FromSeconds(10));
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Nutzungs-Signale beim Beenden nicht geschrieben");
        }
    }

    /// <summary>Summen einer (Datei, Tag)-Zeile, bevor sie geschrieben wird.</summary>
    private sealed class Counters
    {
        public int Opens;
        public int Seconds;
        public int Zooms;
        public int SoftOpens;
        public int SoftSeconds;
        public int Uses;
        public int Shares;

        public void Merge(Counters other)
        {
            Opens += other.Opens;
            Seconds += other.Seconds;
            Zooms += other.Zooms;
            SoftOpens += other.SoftOpens;
            SoftSeconds += other.SoftSeconds;
            Uses += other.Uses;
            Shares += other.Shares;
        }
    }
}
