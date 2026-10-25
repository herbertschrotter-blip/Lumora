using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Controls;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.Logging;
using Lumora.Services.Messaging;
using Lumora.Services.Stories;
using Lumora.Shell;
using SymbolIcon = Wpf.Ui.Controls.SymbolIcon;
using SymbolRegular = Wpf.Ui.Controls.SymbolRegular;

namespace Lumora.Stories;

/// <summary>
/// Brücke zwischen Galerie und Story-Seite (ADR-0060): Kontextmenü der Galerie-Kachel („Als Story…“, „In Storys zeigen“,
/// „Zur Story … hinzufügen“), Story öffnen von außen (auch bevor die Seite erzeugt ist) und „In der Galerie zeigen“ für die
/// Dateien einer Story (Galerie-Filter mit genau diesen Dateien wie „In der Galerie zeigen“ der Karte, ADR-0050).
/// </summary>
public sealed class StoryGalleryBridge
{
    private readonly IStoryService _stories;
    private readonly IDialogService _dialogs;
    private readonly INavigationService _navigation;
    private readonly ILogger<StoryGalleryBridge> _logger;
    private readonly IMessenger _messenger;
    private int? _pendingOpen;

    public StoryGalleryBridge(IStoryService stories, IDialogService dialogs, INavigationService navigation, ILogger<StoryGalleryBridge> logger, IMessenger? messenger = null)
    {
        _stories = stories;
        _dialogs = dialogs;
        _navigation = navigation;
        _logger = logger;
        _messenger = messenger ?? WeakReferenceMessenger.Default;
        // anderer Bereich: das gemerkte Ziel von „Fotos hinzufügen…“ gilt nicht mehr
        _messenger.Register<StoryGalleryBridge, ActiveLibraryChangedMessage>(this, static (r, _) => r.CancelAdd());
    }

    /// <summary>Die Story-Seite soll diese Story öffnen (UI-Thread).</summary>
    public event Action<int>? OpenRequested;

    /// <summary>Ziel von „Fotos hinzufügen…“: in der Galerie auswählen → Rechtsklick „Zur Story … hinzufügen“.</summary>
    public (int StoryId, string Title)? AddTarget { get; private set; }

    /// <summary>Zur Story-Seite wechseln und die Story öffnen.</summary>
    public void RequestOpen(int storyId)
    {
        _pendingOpen = storyId;
        _navigation.NavigateTo(NavigationTags.Stories);
        OpenRequested?.Invoke(storyId);
    }

    /// <summary>Vorgemerkte Story (Seite war noch nicht erzeugt) abholen.</summary>
    public int? TakePendingOpen()
    {
        var id = _pendingOpen;
        _pendingOpen = null;
        return id;
    }

    /// <summary>„Fotos hinzufügen…“: Ziel merken und in die Galerie wechseln.</summary>
    public void BeginAdd(int storyId, string title)
    {
        AddTarget = (storyId, title);
        _navigation.NavigateTo(NavigationTags.Gallery);
    }

    public void CancelAdd() => AddTarget = null;

    /// <summary>„In der Galerie zeigen“: Galerie mit genau diesen Dateien.</summary>
    public void ShowInGallery(IReadOnlyList<int> fileIds)
    {
        if (fileIds.Count == 0)
        {
            return;
        }

        // ohne Ausschnitt (null): die Galerie filtert dann immer über die Id-Liste, nie über Koordinaten
        _messenger.Send(new ShowFilesOnMapSelectionMessage(null!, fileIds));
        _navigation.NavigateTo(NavigationTags.Gallery);
    }

    /// <summary>„Als Story…“: eigene Story aus der Auswahl, danach öffnen. Liefert die Id oder null (abgebrochen).</summary>
    public async Task<int?> CreateFromSelectionAsync(IReadOnlyList<int> fileIds, string? title = null, bool ask = true)
    {
        if (fileIds.Count == 0)
        {
            return null;
        }

        if (ask)
        {
            title = await _dialogs.PromptTextAsync(
                "Als Story",
                $"Eigene Story aus {fileIds.Count:N0} {(fileIds.Count == 1 ? "Foto" : "Fotos")}. Eigener Titel — leer lassen für die sachliche Überschrift aus Datum und Ort:",
                string.Empty,
                "Story anlegen");
            if (title is null)
            {
                return null;
            }
        }

        try
        {
            var id = await _stories.CreateFromSelectionAsync(fileIds, string.IsNullOrWhiteSpace(title) ? null : title.Trim());
            Lumora.Services.Usage.UsageReport.Used(fileIds); // eigene Story aus der Auswahl (Nutzungs-Signale, ADR-0072)
            _logger.LogInformation("Storys: eigene Story {Id} aus {Count} Dateien", id, fileIds.Count);
            RequestOpen(id);
            return id;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Storys: Story aus Auswahl fehlgeschlagen");
            await _dialogs.ShowErrorAsync("Als Story", ex.Message);
            return null;
        }
    }

    /// <summary>„Zur Story … hinzufügen“: Dateien zur gemerkten Story, danach zurück zur Story.</summary>
    public async Task<int> AddToTargetAsync(IReadOnlyList<int> fileIds)
    {
        if (AddTarget is not { } target || fileIds.Count == 0)
        {
            return 0;
        }

        try
        {
            var added = await _stories.AddFilesAsync(target.StoryId, fileIds);
            if (added > 0)
            {
                Lumora.Services.Usage.UsageReport.Used(fileIds); // Nutzungs-Signale (ADR-0072)
            }
            AddTarget = null;
            RequestOpen(target.StoryId);
            return added;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Storys: Fotos zur Story hinzufügen fehlgeschlagen");
            await _dialogs.ShowErrorAsync("Zur Story hinzufügen", ex.Message);
            return 0;
        }
    }

    /// <summary>Einträge im Kontextmenü der Galerie-Kachel (eine Zeile in <c>GalleryView</c>).</summary>
    public void AddMenuItems(ContextMenu menu, IReadOnlyList<int> selectedIds, int targetFileId)
    {
        AddMenuItems(menu.Items, selectedIds, targetFileId);
        menu.Items.Add(new Separator());
    }

    /// <summary>Wie oben, in ein beliebiges Menü (ADR-0068: Untermenü „Story ▸“ des Rechtsklick-Menüs), ohne Trenner am Ende.</summary>
    public void AddMenuItems(ItemCollection items, IReadOnlyList<int> selectedIds, int targetFileId)
    {
        var menu = new { Items = items };
        var ids = selectedIds.Count > 0 ? selectedIds : [targetFileId];
        if (AddTarget is { } target)
        {
            var add = new MenuItem
            {
                Header = $"Zur Story „{Shorten(target.Title)}“ hinzufügen",
                Icon = new SymbolIcon { Symbol = SymbolRegular.Add24 },
            };
            add.Click += async (_, _) => await AddToTargetAsync(ids);
            menu.Items.Add(add);
            var stop = new MenuItem { Header = "Hinzufügen zur Story beenden" };
            stop.Click += (_, _) => CancelAdd();
            menu.Items.Add(stop);
        }

        var create = new MenuItem
        {
            Header = ids.Count > 1 ? $"Als Story… ({ids.Count:N0} ausgewählt)" : "Als Story…",
            Icon = new SymbolIcon { Symbol = SymbolRegular.BookOpen24 },
            ToolTip = "Eigene Story aus der Auswahl — mit Tagesabschnitten, Highlights und Diashow",
        };
        create.Click += async (_, _) => await CreateFromSelectionAsync(ids);
        menu.Items.Add(create);

        var show = new MenuItem { Header = "In Storys zeigen", Icon = new SymbolIcon { Symbol = SymbolRegular.History24 } };
        show.Items.Add(new MenuItem { Header = "Wird geladen …", IsEnabled = false });
        var loaded = false;
        show.SubmenuOpened += async (_, _) =>
        {
            if (loaded)
            {
                return;
            }

            loaded = true;
            await FillStoriesForFileAsync(show, targetFileId);
        };
        menu.Items.Add(show);
    }

    /// <summary>Untermenü „In Storys zeigen“: Storys mit dieser Datei (öffnen per Klick).</summary>
    internal async Task FillStoriesForFileAsync(MenuItem parent, int fileId)
    {
        IReadOnlyList<StoryCard> cards;
        try
        {
            cards = await _stories.GetStoriesForFileAsync(fileId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Storys einer Datei nicht lesbar");
            cards = [];
        }

        parent.Items.Clear();
        if (cards.Count == 0)
        {
            parent.Items.Add(new MenuItem { Header = "In keiner Story", IsEnabled = false });
            return;
        }

        foreach (var card in cards.Take(15))
        {
            var item = new MenuItem
            {
                Header = $"{card.KindLabel}: {card.Title}",
                Icon = new SymbolIcon { Symbol = StoryKindVisuals.Symbol(card.Kind, card.IsAway) },
            };
            var id = card.Id;
            item.Click += (_, _) => RequestOpen(id);
            parent.Items.Add(item);
        }
    }

    private static string Shorten(string text) => text.Length <= 40 ? text : text[..39] + "…";
}
