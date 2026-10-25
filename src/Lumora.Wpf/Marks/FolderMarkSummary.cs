using System.Collections.Generic;
using System.Linq;
using Lumora.Services.Marks;
using Lumora.Services.Tags;

namespace Lumora.Marks;

/// <summary>Namen für die Ordner-Tooltips: Tags nach Id, Kategorie-Werte als „Kategorie: Wert“ nach ValueId.</summary>
public sealed record FolderMarkNames(IReadOnlyDictionary<int, TagInfo> Tags, IReadOnlyDictionary<int, string> ValueLabels)
{
    public static FolderMarkNames Empty { get; } = new(new Dictionary<int, TagInfo>(), new Dictionary<int, string>());
}

/// <summary>
/// Tag-/Kategorie-Zahlen eines Ordnerknotens (Mockup 16): Dateien DIREKT im Ordner mit Tags bzw. Kategorie-Werten, dazu die
/// Dateizahl des Ordners für „n von m Fotos“ und die häufigsten Namen — Tooltip erst bei Bedarf.
/// </summary>
public sealed class FolderMarkSummary
{
    private readonly FolderMarkCount _count;
    private readonly FolderMarkNames _names;

    public FolderMarkSummary(FolderMarkCount count, int fileCount, FolderMarkNames names)
    {
        _count = count;
        _names = names;
        FileCount = fileCount;
    }

    public int TaggedFiles => _count.TaggedFiles;

    public int CategorizedFiles => _count.CategorizedFiles;

    /// <summary>Dateien direkt im Ordner (Bezug für „n von m Fotos“).</summary>
    public int FileCount { get; }

    public bool HasTags => TaggedFiles > 0;

    public bool HasCategories => CategorizedFiles > 0;

    public string TagCountText => MarkTexts.FolderCountText(TaggedFiles);

    public string CategoryCountText => MarkTexts.FolderCountText(CategorizedFiles);

    /// <summary>„Tags · 86 von 212 Fotos“ mit den häufigsten Tags, „Kategorien · 40 von 212 Fotos“ mit den häufigsten Werten.</summary>
    public MarkToolTipContent BuildToolTipContent()
        => new(
            $"Tags · {MarkTexts.OfFiles(TaggedFiles, FileCount)}",
            _count.TopTags
                .Where(u => _names.Tags.ContainsKey(u.Id))
                .Select(u => new MarkChip(_names.Tags[u.Id].Name, _names.Tags[u.Id].Color))
                .ToList(),
            $"Kategorien · {MarkTexts.OfFiles(CategorizedFiles, FileCount)}",
            _count.TopValues
                .Where(u => _names.ValueLabels.ContainsKey(u.Id))
                .Select(u => new MarkChip(_names.ValueLabels[u.Id]))
                .ToList(),
            HasTags,
            HasCategories);
}
