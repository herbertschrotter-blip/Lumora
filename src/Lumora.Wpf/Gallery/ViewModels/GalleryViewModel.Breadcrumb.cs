using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.Logging;
using Lumora.Services.Messaging;

namespace Lumora.Gallery.ViewModels;

/// <summary>Ein Glied des Ordner-Pfads über der Galerie (Wurzel … gewählter Ordner).</summary>
/// <param name="IsLast">Der gewählte Ordner selbst (fett, ohne Pfeil dahinter).</param>
public sealed record FolderCrumb(int Id, string Name, bool IsLast);

/// <summary>
/// Pfad (Breadcrumb) des Ordner-Filters über der Galerie (ADR-0068, Mockup 18): anklickbar — ein Klick zeigt den Elternordner
/// und zieht die Ordnerleiste per <see cref="FolderSelectedMessage"/> nach. Das Kreuz am Chip hebt den Ordner-Filter auf und
/// meldet das ebenfalls (die Leiste hebt ihre Auswahl auf).
/// </summary>
public sealed partial class GalleryViewModel
{
    private const int MaxCrumbDepth = 64;
    private int _crumbVersion;

    /// <summary>Pfad des gewählten Ordners von der Wurzel an; leer ohne Ordner-Filter.</summary>
    public ObservableCollection<FolderCrumb> FolderBreadcrumb { get; } = new();

    partial void OnFolderIdChanged(int? value) => _ = LoadBreadcrumbAsync(value);

    private async Task LoadBreadcrumbAsync(int? folderId)
    {
        var version = ++_crumbVersion;
        if (folderId is not { } id)
        {
            FolderBreadcrumb.Clear();
            return;
        }

        var chain = new List<(int Id, string Name)>();
        try
        {
            int? current = id;
            for (var depth = 0; current is { } next && depth < MaxCrumbDepth; depth++)
            {
                var info = await _source.GetFolderInfoAsync(next, CancellationToken.None);
                if (info is null)
                {
                    break;
                }

                chain.Insert(0, (info.Id, info.Name));
                current = info.ParentId;
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Ordner-Pfad konnte nicht geladen werden");
        }

        if (version != _crumbVersion)
        {
            return; // inzwischen ein anderer Ordner
        }

        FolderBreadcrumb.Clear();
        for (var i = 0; i < chain.Count; i++)
        {
            FolderBreadcrumb.Add(new FolderCrumb(chain[i].Id, chain[i].Name, i == chain.Count - 1));
        }
    }

    /// <summary>Klick auf ein Glied des Pfads: diesen Ordner zeigen (Unterordner-Einstellung bleibt) und die Leiste nachziehen.</summary>
    [RelayCommand]
    private async Task OpenBreadcrumbAsync(FolderCrumb? crumb)
    {
        if (crumb is null || !HasLibrary || crumb.Id == FolderId)
        {
            return;
        }

        await ApplyFolderFilterAsync(crumb.Id, IncludeSubfolders, crumb.Name);
        _messenger.Send(new FolderSelectedMessage(_libraryId, crumb.Id, IncludeSubfolders));
    }
}
