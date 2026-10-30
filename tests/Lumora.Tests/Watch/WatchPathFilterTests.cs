using Lumora.Services.Media;
using Lumora.Services.Watch;

namespace Lumora.Tests.Watch;

public sealed class WatchPathFilterTests
{
    private readonly WatchPathFilter _filter = new(new MediaFormatRegistry(), Path.Combine(Path.GetTempPath(), "lumora-thumbs-root"));

    [Theory]
    [InlineData(@"C:\lib\2024\IMG_0001.jpg", true)]
    [InlineData(@"C:\lib\2024\clip.MP4", true)]
    [InlineData(@"C:\lib\2024\IMG_0001.jpg.tmp", false)]
    [InlineData(@"C:\lib\2024\download.part", false)]
    [InlineData(@"C:\lib\2024\~IMG_0001.jpg", false)]
    [InlineData(@"C:\lib\2024\.hidden.jpg", false)]
    [InlineData(@"C:\lib\2024\Thumbs.db", false)]
    [InlineData(@"C:\lib\2024\desktop.ini", false)]
    [InlineData(@"C:\lib\2024\notes.txt", false)]
    [InlineData(@"C:\lib\.thumbnails\IMG_0001.jpg", false)]
    [InlineData(@"C:\lib\@eaDir\IMG_0001.jpg", false)]
    [InlineData(@"C:\lib\$RECYCLE.BIN\S-1\IMG_0001.jpg", false)]
    public void Created_file_relevance_follows_format_register_and_ignore_rules(string path, bool expected)
    {
        _filter.IsRelevant(WatcherChangeTypes.Created, path).Should().Be(expected);
    }

    [Theory]
    [InlineData(@"X:\.lumora\cache\ab\17.png")]
    [InlineData(@"X:\.LUMORA\temp\17.jpg")]
    [InlineData(@"X:\.lumora-ki\work\ergebnis.png")]
    [InlineData(@"X:\Fotos\.lumora\cache\17.png")]
    public void Own_catalog_folders_in_a_hidden_library_do_not_trigger_a_scan(string path)
    {
        _filter.IsRelevant(WatcherChangeTypes.Created, path).Should().BeFalse();
        _filter.IsRelevant(WatcherChangeTypes.Changed, path).Should().BeFalse();
        _filter.IsRelevant(WatcherChangeTypes.Deleted, path).Should().BeFalse();
    }

    [Fact]
    public void Deleted_folder_inside_the_catalog_folder_does_not_trigger_a_scan()
    {
        _filter.IsRelevant(WatcherChangeTypes.Deleted, @"X:\.lumora\temp\sitzung").Should().BeFalse();
    }

    [Fact]
    public void Medium_renamed_out_of_the_catalog_folder_still_counts()
    {
        _filter.IsRelevant(WatcherChangeTypes.Renamed, @"X:\Fotos\a.jpg", oldPath: @"X:\.lumora\temp\a.jpg").Should().BeTrue();
    }

    [Fact]
    public void Own_thumbnail_cache_is_ignored()
    {
        var thumb = Path.Combine(Path.GetTempPath(), "lumora-thumbs-root", "1", "17_320_still.jpg");
        _filter.IsRelevant(WatcherChangeTypes.Created, thumb).Should().BeFalse();
    }

    [Fact]
    public void Rename_from_media_to_temp_is_relevant_because_the_medium_disappears()
    {
        _filter.IsRelevant(WatcherChangeTypes.Renamed, @"C:\lib\a.tmp", oldPath: @"C:\lib\a.jpg").Should().BeTrue();
        _filter.IsRelevant(WatcherChangeTypes.Renamed, @"C:\lib\a.jpg", oldPath: @"C:\lib\a.tmp").Should().BeTrue();
        _filter.IsRelevant(WatcherChangeTypes.Renamed, @"C:\lib\a.txt", oldPath: @"C:\lib\a.tmp").Should().BeFalse();
    }

    [Fact]
    public void Directory_events_count_for_delete_and_create_but_not_for_changed()
    {
        var dir = Path.Combine(Path.GetTempPath(), "Lumora.Tests", "watch-dir-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            _filter.IsRelevant(WatcherChangeTypes.Created, dir).Should().BeTrue("ein angelegter Ordner kann Medien enthalten");
            _filter.IsRelevant(WatcherChangeTypes.Changed, dir).Should().BeFalse("Changed eines Ordners feuert für jede Datei darin");
            _filter.IsRelevant(WatcherChangeTypes.Deleted, Path.Combine(dir, "gone")).Should().BeTrue("ein gelöschter Pfad ohne Endung ist vermutlich ein Ordner");
            _filter.IsRelevant(WatcherChangeTypes.Created, Path.Combine(dir, "missing")).Should().BeFalse("existiert nicht als Ordner");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Theory]
    [InlineData("2024.06 Urlaub")]
    [InlineData("v1.2")]
    [InlineData("Fotos.alt")]
    public void Directory_with_a_dot_in_its_name_counts_when_created_or_renamed(string name)
    {
        var parent = Path.Combine(Path.GetTempPath(), "Lumora.Tests", "watch-dot-" + Guid.NewGuid().ToString("N"));
        var dir = Path.Combine(parent, name);
        Directory.CreateDirectory(dir);
        try
        {
            _filter.IsRelevant(WatcherChangeTypes.Created, dir).Should().BeTrue("ein angelegter Ordner kann Medien enthalten — auch mit Punkt im Namen");
            _filter.IsRelevant(WatcherChangeTypes.Renamed, dir, oldPath: Path.Combine(parent, "alt")).Should().BeTrue();
            _filter.IsRelevant(WatcherChangeTypes.Changed, dir).Should().BeFalse("Changed eines Ordners feuert für jede Datei darin");
        }
        finally
        {
            Directory.Delete(parent, recursive: true);
        }
    }

    [Theory]
    [InlineData(@"C:\lib\2024.06 Urlaub", true)]
    [InlineData(@"C:\lib\2024.06", true)]
    [InlineData(@"C:\lib\Teil 1. Rom", true)]
    [InlineData(@"C:\lib\2024\IMG_0001.jpg", true)]
    [InlineData(@"C:\lib\2024\notizen.txt", false)]
    [InlineData(@"C:\lib\2024\IMG_0001.jpg.xmp", false)]
    [InlineData(@"C:\lib\2024\IMG_0001.jpg.tmp", false)]
    [InlineData(@"C:\lib\2024\video.crdownload", false)]
    public void Deleted_path_with_a_dot_counts_when_it_cannot_be_a_file_extension(string path, bool expected)
    {
        _filter.IsRelevant(WatcherChangeTypes.Deleted, path).Should().Be(expected);
    }
}
