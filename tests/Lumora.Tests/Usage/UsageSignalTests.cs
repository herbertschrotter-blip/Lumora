using System.Globalization;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.Logging.Abstractions;
using Lumora.Services.Messaging;
using Lumora.Services.Quality;
using Lumora.Services.Scanning;
using Lumora.Services.Stories;
using Lumora.Services.Usage;
using Lumora.Tests.Stories;

namespace Lumora.Tests.Usage;

/// <summary>Uhr der Tests: feste Zeit in UTC, Ortszeit = UTC (Tagesgrenzen unabhängig vom Rechner), von Hand weiter.</summary>
internal sealed class UsageClock(DateTimeOffset start) : TimeProvider
{
    private DateTimeOffset _now = start;

    public UsageClock()
        : this(new DateTimeOffset(2026, 10, 21, 10, 0, 0, TimeSpan.Zero))
    {
    }

    public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan by) => _now += by;
}

/// <summary>
/// Nutzungs-Signale (ADR-0072): Tagessummen, Grenzen der Ansehdauer, Schnelllauf, Herkunft (voll/gedämpft/gar nicht),
/// Abschalten, Löschen, getrennte Kataloge (versteckte Bibliothek), Meldung über den Messenger, Rohwert C# = SQL,
/// gedämpfter Zuschlag ohne Abzug, „Vergessene Schätze“, „Neu aufbauen“.
/// </summary>
public sealed class UsageSignalTests : IDisposable
{
    private readonly List<IDisposable> _disposables = [];

    public void Dispose()
    {
        foreach (var d in Enumerable.Reverse(_disposables))
        {
            d.Dispose();
        }
    }

    private async Task<(StoryWorld World, UsageSignalService Usage, UsageClock Clock, List<int> Ids)> CreateAsync(int files = 4)
    {
        var w = await StoryWorld.CreateAsync();
        _disposables.Add(w);
        var clock = new UsageClock();
        var usage = new UsageSignalService(w.Fx.ContextFactory, NullLogger<UsageSignalService>.Instance, w.Fx.Messenger, clock);
        _disposables.Add(usage);
        var ids = w.Add(StoryWorld.Burst(StoryWorld.Utc(2023, 5, 1), files));
        return (w, usage, clock, ids);
    }

    private static List<(int FileId, int Day, int Opens, int Seconds, int Zooms, int SoftOpens, int SoftSeconds, int Uses, int Shares)> Rows(StoryWorld w)
    {
        using var connection = w.Fx.ContextFactory.OpenSqliteConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT FileId, Day, Opens, Seconds, Zooms, SoftOpens, SoftSeconds, Uses, Shares FROM FileUsageDays ORDER BY FileId, Day;";
        var list = new List<(int, int, int, int, int, int, int, int, int)>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            list.Add((reader.GetInt32(0), reader.GetInt32(1), reader.GetInt32(2), reader.GetInt32(3), reader.GetInt32(4),
                reader.GetInt32(5), reader.GetInt32(6), reader.GetInt32(7), reader.GetInt32(8)));
        }

        return list;
    }

    private static int Today(UsageClock clock) => UsageScoring.DayNumber(DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime));

    [Fact]
    public async Task Gleicher_Tag_wird_eine_Zeile_mit_Summen_naechster_Tag_eine_neue()
    {
        var (w, usage, clock, ids) = await CreateAsync();
        usage.RecordView(ids[0], TimeSpan.FromSeconds(10), zoomed: false, UsageViewContext.Normal);
        usage.RecordView(ids[0], TimeSpan.FromSeconds(20), zoomed: true, UsageViewContext.Normal);
        await usage.FlushAsync();
        clock.Advance(TimeSpan.FromDays(1));
        usage.RecordView(ids[0], TimeSpan.FromSeconds(5), zoomed: false, UsageViewContext.Normal);
        await usage.FlushAsync();

        var rows = Rows(w);
        rows.Should().HaveCount(2, "nur Tagessummen je Foto, keine Einzel-Ansichten");
        rows[0].Should().Be((ids[0], Today(clock) - 1, 2, 30, 1, 0, 0, 0, 0));
        rows[1].Should().Be((ids[0], Today(clock), 1, 5, 0, 0, 0, 0, 0));
        w.Scalar("SELECT COUNT(*) FROM sqlite_master WHERE sql LIKE '%FileUsageDays%' AND sql LIKE '%Utc%'").Should().Be(0, "keine Zeitstempel einzelner Ansichten");
        (await usage.GetTotalsAsync(ids[0]))!.Days.Should().Be(2, "Wiederkehr an einem anderen Tag");
    }

    [Fact]
    public async Task Ansehdauer_unter_einer_Sekunde_zaehlt_nicht_mehr_als_zwei_Minuten_werden_gekappt()
    {
        var (w, usage, _, ids) = await CreateAsync();
        usage.RecordView(ids[0], TimeSpan.FromMilliseconds(900), zoomed: true, UsageViewContext.Normal);
        usage.RecordView(ids[1], TimeSpan.FromSeconds(1), zoomed: false, UsageViewContext.Normal);
        usage.RecordView(ids[2], TimeSpan.FromMinutes(30), zoomed: false, UsageViewContext.Normal);
        await usage.FlushAsync();

        var rows = Rows(w);
        rows.Select(r => r.FileId).Should().BeEquivalentTo([ids[1], ids[2]]);
        rows.Single(r => r.FileId == ids[1]).Seconds.Should().Be(1);
        rows.Single(r => r.FileId == ids[2]).Seconds.Should().Be(120, "je Ansicht höchstens zwei Minuten");
    }

    [Fact]
    public async Task Schnelllauf_und_inaktives_Fenster_zaehlen_nicht()
    {
        var (w, usage, clock, ids) = await CreateAsync(10);
        var tracker = ((IUsageSignals)usage).CreateTracker(UsageViewContext.Normal);
        tracker.SetActive(true);
        foreach (var id in ids)
        {
            tracker.Show(id);
            clock.Advance(TimeSpan.FromMilliseconds(400)); // Pfeiltaste gehalten
        }

        tracker.Show(ids[0]);
        clock.Advance(TimeSpan.FromSeconds(3));
        tracker.SetActive(false); // anderes Fenster vorne
        clock.Advance(TimeSpan.FromMinutes(10));
        tracker.SetActive(true);
        clock.Advance(TimeSpan.FromSeconds(2));
        tracker.Stop();
        await usage.FlushAsync();

        var rows = Rows(w);
        rows.Should().ContainSingle("nur die eine ruhige Ansicht zählt").Which.Should().Be((ids[0], Today(clock), 1, 5, 0, 0, 0, 0, 0));
    }

    [Fact]
    public async Task Herkunft_Story_und_Diashow_gedaempft_Doppelte_gar_nicht()
    {
        var (w, usage, _, ids) = await CreateAsync();
        usage.RecordView(ids[0], TimeSpan.FromSeconds(8), zoomed: true, UsageViewContext.Story);
        usage.RecordView(ids[1], TimeSpan.FromSeconds(6), zoomed: false, UsageViewContext.Slideshow);
        usage.RecordView(ids[2], TimeSpan.FromSeconds(60), zoomed: true, UsageViewContext.None);
        await usage.FlushAsync();

        var rows = Rows(w);
        rows.Should().HaveCount(2);
        rows.Single(r => r.FileId == ids[0]).Should().Match<(int FileId, int Day, int Opens, int Seconds, int Zooms, int SoftOpens, int SoftSeconds, int Uses, int Shares)>(
            r => r.Opens == 0 && r.SoftOpens == 1 && r.SoftSeconds == 8 && r.Zooms == 1);
        rows.Single(r => r.FileId == ids[1]).Should().Match<(int FileId, int Day, int Opens, int Seconds, int Zooms, int SoftOpens, int SoftSeconds, int Uses, int Shares)>(
            r => r.Opens == 0 && r.SoftOpens == 0 && r.SoftSeconds == 6);
        var story = UsageScoring.Raw(new UsageTotals(0, 0, 0, 1, 8, 0, 0, 0, 0));
        var normal = UsageScoring.Raw(new UsageTotals(1, 8, 0, 0, 0, 0, 0, 1, 0));
        story.Should().BeLessThan(normal, "Ansehen aus einer Story zählt weniger");
    }

    [Fact]
    public async Task Verwendung_und_Weitergabe_ueber_den_Messenger_je_Datei_einmal()
    {
        var (w, usage, _, ids) = await CreateAsync();
        UsageReport.Used([ids[0], ids[0], ids[1]], w.Fx.Messenger);
        UsageReport.Shared([ids[1]], w.Fx.Messenger);
        UsageReport.Shared([], w.Fx.Messenger);
        await usage.FlushAsync();

        var rows = Rows(w);
        rows.Single(r => r.FileId == ids[0]).Uses.Should().Be(1, "doppelte Id in einer Meldung zählt einmal");
        rows.Single(r => r.FileId == ids[1]).Should().Match<(int FileId, int Day, int Opens, int Seconds, int Zooms, int SoftOpens, int SoftSeconds, int Uses, int Shares)>(r => r.Uses == 1 && r.Shares == 1);
    }

    [Fact]
    public async Task Abgeschaltet_wird_nichts_gespeichert_auch_nicht_was_vorher_gesammelt_wurde()
    {
        var (w, usage, _, ids) = await CreateAsync();
        usage.RecordView(ids[0], TimeSpan.FromSeconds(10), zoomed: false, UsageViewContext.Normal);
        await usage.SaveSettingsAsync(new UsageSettings(Enabled: false));
        usage.RecordView(ids[1], TimeSpan.FromSeconds(10), zoomed: false, UsageViewContext.Normal);
        UsageReport.Shared([ids[2]], w.Fx.Messenger);
        await usage.FlushAsync();

        Rows(w).Should().BeEmpty();
        usage.IsEnabled.Should().BeFalse();
        (await usage.GetSettingsAsync()).Should().Be(new UsageSettings(Enabled: false));

        // Abschalten in einer anderen Sitzung (Settings-Tabelle) wirkt beim Schreiben, auch wenn diese Instanz es noch nicht weiß
        await usage.SaveSettingsAsync(new UsageSettings());
        w.Execute("UPDATE Settings SET Value = 'false' WHERE Key = 'usage.enabled'");
        usage.RecordView(ids[3], TimeSpan.FromSeconds(10), zoomed: false, UsageViewContext.Normal);
        await usage.FlushAsync();
        Rows(w).Should().BeEmpty();
    }

    [Fact]
    public async Task Abgeschaltet_wird_Gelerntes_nicht_verwendet_bleibt_aber_bis_zum_Loeschen()
    {
        var (w, usage, _, ids) = await CreateAsync();
        Learn(usage, ids[0], days: 1, opens: 8);
        await usage.FlushAsync();
        (await usage.GetTotalsAsync([ids[0]])).Should().ContainKey(ids[0]);

        await usage.SaveSettingsAsync(new UsageSettings(Enabled: false));
        (await usage.GetTotalsAsync([ids[0]])).Should().BeEmpty("aus = nicht verwenden");
        (await usage.GetSummaryAsync()).Files.Should().Be(1, "Daten bleiben bis „Gelernte Nutzung löschen“");
        w.Scalar($"SELECT COUNT(*) FROM ({UsageScoring.LovedFileIdsSql(respectSetting: true)})").Should().Be(0, "Galerie-Filter „Lieblinge“ leer");
    }

    [Fact]
    public async Task Loeschen_entfernt_alles_und_meldet_es()
    {
        var (w, usage, clock, ids) = await CreateAsync();
        var messages = new List<UsageChangedMessage>();
        w.Fx.Messenger.Register<UsageChangedMessage>(messages, (r, m) => ((List<UsageChangedMessage>)r).Add(m));
        usage.RecordView(ids[0], TimeSpan.FromSeconds(10), zoomed: false, UsageViewContext.Normal);
        usage.RecordView(ids[1], TimeSpan.FromSeconds(10), zoomed: false, UsageViewContext.Normal);
        await usage.FlushAsync();
        var summary = await usage.GetSummaryAsync();
        summary.Should().Be(new UsageSummary(2, 1, 0, DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime)));
        usage.RecordView(ids[2], TimeSpan.FromSeconds(10), zoomed: false, UsageViewContext.Normal); // noch nicht geschrieben

        (await usage.EraseAsync()).Should().Be(2);
        await usage.FlushAsync();

        Rows(w).Should().BeEmpty("auch Gesammeltes wird verworfen");
        (await usage.GetSummaryAsync()).Should().Be(UsageSummary.Empty);
        w.Scalar("SELECT COUNT(*) FROM Settings WHERE Key = 'usage.since'").Should().Be(0);
        messages.Should().ContainSingle(m => m.Erased);
        usage.IsEnabled.Should().BeTrue("Löschen schaltet das Lernen nicht ab");
    }

    [Fact]
    public async Task Versteckte_Bibliothek_eigener_Katalog_nichts_im_normalen_und_keine_Dateien_ausserhalb()
    {
        var (normal, normalUsage, _, normalIds) = await CreateAsync();
        var (hidden, hiddenUsage, _, hiddenIds) = await CreateAsync();
        var hiddenFiles = Directory.EnumerateFiles(hidden.Fx.RootDir, "*", SearchOption.AllDirectories).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var normalFiles = Directory.EnumerateFiles(normal.Fx.RootDir, "*", SearchOption.AllDirectories).ToHashSet(StringComparer.OrdinalIgnoreCase);

        hiddenUsage.RecordView(hiddenIds[0], TimeSpan.FromSeconds(30), zoomed: true, UsageViewContext.Normal);
        UsageReport.Shared([hiddenIds[1]], hidden.Fx.Messenger);
        await hiddenUsage.FlushAsync();
        await normalUsage.FlushAsync();

        Rows(hidden).Should().HaveCount(2);
        Rows(normal).Should().BeEmpty("die Sitzung schreibt nur in ihren eigenen Katalog");
        Directory.EnumerateFiles(hidden.Fx.RootDir, "*", SearchOption.AllDirectories)
            .Where(f => !hiddenFiles.Contains(f) && !Path.GetFileName(f).StartsWith("library.db", StringComparison.OrdinalIgnoreCase))
            .Should().BeEmpty("nichts neben dem Katalog");
        Directory.EnumerateFiles(normal.Fx.RootDir, "*", SearchOption.AllDirectories).Where(f => !normalFiles.Contains(f)
            && !Path.GetFileName(f).StartsWith("library.db", StringComparison.OrdinalIgnoreCase)).Should().BeEmpty();
        _ = normalIds;
    }

    [Fact]
    public async Task Ende_der_Sitzung_schreibt_Gesammeltes_danach_wird_nichts_mehr_angenommen()
    {
        var (w, usage, _, ids) = await CreateAsync();
        usage.RecordView(ids[0], TimeSpan.FromSeconds(10), zoomed: false, UsageViewContext.Normal);
        usage.Dispose();
        usage.RecordView(ids[1], TimeSpan.FromSeconds(10), zoomed: false, UsageViewContext.Normal);
        UsageReport.Used([ids[2]], w.Fx.Messenger);
        await usage.FlushAsync();

        Rows(w).Select(r => r.FileId).Should().Equal(ids[0]);
    }

    [Fact]
    public async Task Geloeschte_Datei_faellt_still_heraus()
    {
        var (w, usage, _, ids) = await CreateAsync();
        usage.RecordView(ids[0], TimeSpan.FromSeconds(10), zoomed: false, UsageViewContext.Normal);
        usage.RecordView(ids[1], TimeSpan.FromSeconds(10), zoomed: false, UsageViewContext.Normal);
        w.Execute("DELETE FROM Files WHERE Id = $id", ("$id", ids[1]));
        await usage.FlushAsync();
        Rows(w).Select(r => r.FileId).Should().Equal(ids[0]);

        w.Execute("DELETE FROM Files WHERE Id = $id", ("$id", ids[0]));
        Rows(w).Should().BeEmpty("ON DELETE CASCADE");
    }

    [Fact]
    public async Task Rohwert_in_SQL_gleich_wie_in_CSharp_und_Lieblinge_ab_der_Schwelle()
    {
        var (w, usage, clock, ids) = await CreateAsync();
        Learn(usage, ids[0], days: 3, opens: 1, clock: clock); // an drei Tagen geöffnet → Liebling
        Learn(usage, ids[1], days: 1, opens: 2, clock: clock); // einmal kurz → kein Liebling
        UsageReport.Shared([ids[2], ids[2]], w.Fx.Messenger);
        usage.RecordView(ids[2], TimeSpan.FromSeconds(100), zoomed: true, UsageViewContext.Story);
        await usage.FlushAsync();

        var totals = await usage.GetTotalsAsync(ids);
        using var connection = w.Fx.ContextFactory.OpenSqliteConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = $"SELECT u.FileId, {UsageScoring.RawSql()} FROM FileUsageDays u GROUP BY u.FileId;";
        using (var reader = cmd.ExecuteReader())
        {
            while (reader.Read())
            {
                reader.GetDouble(1).Should().BeApproximately(totals[reader.GetInt32(0)].Raw, 1e-9);
            }
        }

        UsageScoring.IsLoved(totals[ids[0]]).Should().BeTrue();
        UsageScoring.IsLoved(totals[ids[1]]).Should().BeFalse();
        w.Scalar($"SELECT COUNT(*) FROM ({UsageScoring.LovedFileIdsSql(respectSetting: true)})").Should().Be(totals.Values.Count(UsageScoring.IsLoved));
        (await usage.GetSummaryAsync()).Loved.Should().Be(totals.Values.Count(UsageScoring.IsLoved));
    }

    [Fact]
    public void Zuschlag_gedaempft_hoechstens_unter_Favorit_ohne_Nutzung_null_Grund_lesbar()
    {
        UsageScoring.Bonus(null).Should().Be(0);
        UsageScoring.Bonus(UsageTotals.None).Should().Be(0, "fehlende Nutzung ist kein Abzug");
        var huge = new UsageTotals(500, 100_000, 100, 500, 100_000, 100, 100, 300, 0);
        UsageScoring.Bonus(huge).Should().BeLessThanOrEqualTo(UsageScoring.MaxBonus).And.BeLessThan(0.15, "kleiner als ein Favorit");
        UsageScoring.Bonus(new UsageTotals(1, 10, 0, 0, 0, 0, 0, 1, 0)).Should().BeLessThan(0.03, "eine kurze Ansicht hebt kaum");

        UsageScoring.Reason(new UsageTotals(1, 10, 0, 0, 0, 0, 0, 1, 0)).Should().BeNull("zu wenig, um es zu nennen");
        UsageScoring.Reason(new UsageTotals(4, 120, 0, 0, 0, 0, 0, 3, 0)).Should().Be("oft angesehen");
        UsageScoring.Reason(new UsageTotals(0, 0, 0, 0, 0, 0, 3, 1, 0)).Should().Be("3× geteilt");
        UsageScoring.Describe(new UsageTotals(9, 240, 2, 0, 0, 1, 1, 6, 0)).Should().Be("9× angesehen an 6 Tagen · 4 min · 2× gezoomt · 1× verwendet · 1× weitergegeben");
        UsageScoring.Describe(new UsageTotals(1, 20, 0, 5, 60, 0, 0, 1, 0)).Should().Be("1× angesehen · 5× in Storys · 1 min", "gedämpfte Ansichten getrennt — die Tage zählen nur gezielte");
        UsageScoring.Describe(new UsageTotals(0, 0, 0, 0, 12, 0, 0, 0, 0)).Should().Be("in der Diashow");
        UsageScoring.Describe(UsageTotals.None).Should().BeNull();
    }

    [Fact]
    public void Highlights_Nutzung_hebt_gedaempft_und_nennt_den_Grund_ohne_Abzug_fuer_andere()
    {
        HighlightCandidate Photo(int i, UsageTotals? usage = null, bool favorite = false)
            => new(100 + i, i, 1_699_999_200 + (i * 3600L), false, 0.6, 0.7, null, 0, 0, favorite, 0, null, false, null, usage);
        var loved = new UsageTotals(6, 600, 2, 0, 0, 0, 0, 4, 0);
        var plain = Enumerable.Range(0, 6).Select(i => Photo(i)).ToList();
        var withUsage = plain.Select(c => c.Position == 3 ? c with { Usage = loved } : c).ToList();

        var before = HighlightPlanner.Select(plain, new HighlightOptions(Share: 1, Min: 6, Max: 6), 10);
        var after = HighlightPlanner.Select(withUsage, new HighlightOptions(Share: 1, Min: 6, Max: 6), 10);
        after.Single(p => p.FileId == 103).Score.Should().BeGreaterThan(before.Single(p => p.FileId == 103).Score);
        after.Single(p => p.FileId == 103).Reason.Should().Contain("oft angesehen");
        after.Where(p => p.FileId != 103).Select(p => p.Score).Should().Equal(before.Where(p => p.FileId != 103).Select(p => p.Score), "ohne Nutzung kein Abzug");

        // Nur eines von zweien: das oft angesehene gewinnt bei gleicher Güte, ein Favorit wiegt aber schwerer
        var one = HighlightPlanner.Select([Photo(0), Photo(1, loved)], new HighlightOptions(Share: 0.5, Min: 1, Max: 1), 10);
        one.Single().FileId.Should().Be(101);
        var fav = HighlightPlanner.Select([Photo(0, favorite: true), Photo(1, loved)], new HighlightOptions(Share: 0.5, Min: 1, Max: 1), 10);
        fav.Single().FileId.Should().Be(100, "gedämpft: weniger als ein Favorit");
    }

    [Fact]
    public async Task Auswahl_aus_dem_Katalog_liest_die_Nutzung_nur_wenn_sie_an_ist()
    {
        var (w, usage, clock, ids) = await CreateAsync(2);
        Learn(usage, ids[1], days: 3, opens: 2, clock: clock);
        await usage.FlushAsync();
        var selector = new QualityHighlightSelector(w.Fx.ContextFactory, usage: usage);

        var pick = (await selector.SelectAsync(ids, new HighlightOptions(Share: 0.5, Min: 1, Max: 1))).Single();
        pick.FileId.Should().Be(ids[1]);
        pick.Reason.Should().Contain("oft angesehen");

        await usage.SaveSettingsAsync(new UsageSettings(Enabled: false));
        (await selector.SelectAsync(ids, new HighlightOptions(Share: 1, Min: 2, Max: 2))).Should().OnlyContain(p => !p.Reason.Contains("angesehen", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Vergessene_Schaetze_gute_alte_nicht_angesehene_Fotos_je_Monat_und_abschaltbar()
    {
        using var w = await StoryWorld.CreateAsync();
        var clock = new UsageClock(new DateTimeOffset(2026, 10, 21, 10, 0, 0, TimeSpan.Zero));
        using var usage = new UsageSignalService(w.Fx.ContextFactory, NullLogger<UsageSignalService>.Instance, w.Fx.Messenger, clock);
        var stories = new StoryService(w.Fx.ContextFactory, NullLogger<StoryService>.Instance, w.Selector, w.Fx.Messenger, w.Scope, w.Places, w.Albums, w.Index, w.Clock, w.Homes, usage);
        _disposables.Add(stories);
        var good = w.Add(Enumerable.Range(0, 10).Select(i => new StoryFile(StoryWorld.Utc(2019 + (i % 4), 3, 1 + i), Favorite: true)));
        var plain = w.Add(Enumerable.Range(0, 10).Select(i => new StoryFile(StoryWorld.Utc(2020, 6, 1 + i))));
        var recent = w.Add(Enumerable.Range(0, 3).Select(i => new StoryFile(StoryWorld.Utc(2026, 9, 1 + i), Favorite: true)));
        usage.RecordView(good[0], TimeSpan.FromSeconds(30), zoomed: false, UsageViewContext.Normal); // heute angesehen → nicht vergessen
        await usage.FlushAsync();

        var candidates = await usage.FindForgottenAsync([w.LibraryId], StoryWorld.Today, 100);
        candidates.Should().BeEquivalentTo(good.Skip(1), "gut (Favorit), älter als ein Jahr, seit einem Jahr nicht geöffnet");
        (await usage.FindForgottenAsync([w.LibraryId], StoryWorld.Today, 100)).Should().Equal(candidates, "je Monat dieselbe Auswahl");

        var memory = (await stories.GetMemoriesAsync()).Single(m => m.Kind == StoryMemoryKind.ForgottenTreasures);
        memory.Key.Should().Be("R:F:2026-10");
        memory.Kicker.Should().Be("Vergessene Schätze");
        memory.FileCount.Should().Be(9);
        memory.Subtitle.Should().StartWith("2019–2022");

        var id = await stories.OpenMemoryAsync(memory.Key);
        w.Items(id).Should().BeEquivalentTo(good.Skip(1));
        (await stories.GetAsync(id))!.Title.Should().Be("Vergessene Schätze");
        (await stories.OpenMemoryAsync(memory.Key)).Should().Be(id, "geöffnet bleibt die Auswahl");

        (await stories.GetMemoriesAsync()).Single(m => m.Kind == StoryMemoryKind.ForgottenTreasures).StoryId.Should().Be(id);

        await usage.SaveSettingsAsync(new UsageSettings(Enabled: true, ForgottenEnabled: false));
        (await usage.FindForgottenAsync([w.LibraryId], StoryWorld.Today, 100)).Should().BeEmpty();
        (await stories.GetMemoriesAsync()).Should().NotContain(m => m.Kind == StoryMemoryKind.ForgottenTreasures, "abgeschaltet — auch die geöffnete nicht mehr unter den Rückblicken");
        (await stories.GetAsync(id)).Should().NotBeNull("die geöffnete Story bleibt");
        await usage.SaveSettingsAsync(new UsageSettings(Enabled: false, ForgottenEnabled: true));
        (await stories.GetMemoriesAsync()).Should().NotContain(m => m.Kind == StoryMemoryKind.ForgottenTreasures);
        _ = plain;
        _ = recent;
    }

    [Fact]
    public async Task Vergessene_Schaetze_brauchen_mindestens_sechs_Fotos()
    {
        using var w = await StoryWorld.CreateAsync();
        using var usage = new UsageSignalService(w.Fx.ContextFactory, NullLogger<UsageSignalService>.Instance, w.Fx.Messenger, new UsageClock());
        var stories = new StoryService(w.Fx.ContextFactory, NullLogger<StoryService>.Instance, w.Selector, w.Fx.Messenger, w.Scope, w.Places, w.Albums, w.Index, w.Clock, w.Homes, usage);
        _disposables.Add(stories);
        w.Add(Enumerable.Range(0, 5).Select(i => new StoryFile(StoryWorld.Utc(2019, 3, 1 + i), Favorite: true)));

        (await stories.GetMemoriesAsync()).Should().NotContain(m => m.Kind == StoryMemoryKind.ForgottenTreasures);
    }

    [Fact]
    public async Task Neu_aufbauen_traegt_die_Tagessummen_auf_die_neuen_FileIds()
    {
        using var w = await StoryWorld.CreateAsync();
        var clock = new UsageClock();
        using var usage = new UsageSignalService(w.Fx.ContextFactory, NullLogger<UsageSignalService>.Instance, w.Fx.Messenger, clock);
        w.Fx.WriteLibraryFile(Path.Combine("Fotos", "a.jpg"), 2001);
        w.Fx.WriteLibraryFile(Path.Combine("Fotos", "b.jpg"), 2002);
        await w.Fx.ScanAsync(w.LibraryId);
        var a = (await w.Fx.FindFileAsync("a.jpg"))!.Id;
        usage.RecordView(a, TimeSpan.FromSeconds(40), zoomed: true, UsageViewContext.Normal);
        clock.Advance(TimeSpan.FromDays(1));
        usage.RecordView(a, TimeSpan.FromSeconds(20), zoomed: false, UsageViewContext.Normal);
        UsageReport.Shared([a], w.Fx.Messenger);
        await usage.FlushAsync();
        var before = Rows(w).Select(r => r with { FileId = 0 }).ToList();

        await w.Fx.CreateScanService().ScanAsync(w.LibraryId, ScanMode.ClearAndRescan, null, CancellationToken.None);

        var newA = (await w.Fx.FindFileAsync("a.jpg"))!.Id;
        newA.Should().NotBe(a);
        var after = Rows(w);
        after.Should().OnlyContain(r => r.FileId == newA);
        after.Select(r => r with { FileId = 0 }).Should().Equal(before);
        w.Scalar("SELECT COUNT(*) FROM FileUsageDays WHERE FileId < 0").Should().Be(0);
    }

    /// <summary>Öffnet das Foto an <paramref name="days"/> Tagen je <paramref name="opens"/>-mal (Uhr läuft dabei weiter).</summary>
    private static void Learn(UsageSignalService usage, int fileId, int days, int opens, UsageClock? clock = null)
    {
        for (var d = 0; d < days; d++)
        {
            for (var o = 0; o < opens; o++)
            {
                usage.RecordView(fileId, TimeSpan.FromSeconds(30), zoomed: false, UsageViewContext.Normal);
            }

            clock?.Advance(TimeSpan.FromDays(1));
        }
    }
}
