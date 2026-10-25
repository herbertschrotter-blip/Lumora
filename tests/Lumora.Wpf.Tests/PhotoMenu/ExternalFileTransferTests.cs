using System.IO;
using System.Windows;
using Lumora.Albums;
using Lumora.Domain.Enums;
using Lumora.PhotoMenu;
using Lumora.Services.Export;
using Lumora.Wpf.Tests.Fixtures;

namespace Lumora.Wpf.Tests.PhotoMenu;

/// <summary>
/// Fotos in andere Programme kopieren (ADR-0068, Issue #25): Datenobjekt beim Ziehen mit internem Format UND echtem Datei-Drop,
/// nach außen nur „Kopieren“ (kein Verschieben, keine Verknüpfung), interne Ziele entscheiden über die Tasten; Strg+C legt Dateien
/// als Kopie in die Zwischenablage; fehlende Dateien und Archive werden ausgelassen; die versteckte Bibliothek ist gesperrt.
/// </summary>
[Collection(UiTestCollection.Name)]
public sealed class ExternalFileTransferTests : IDisposable
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);
    private readonly string _dir = Directory.CreateTempSubdirectory("lumora-drop-").FullName;

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private (List<ExternalCandidate> Candidates, string Photo, string Video) Files()
    {
        var photo = Path.Combine(_dir, "foto.jpg");
        var video = Path.Combine(_dir, "film.mp4");
        var zip = Path.Combine(_dir, "paket.zip");
        File.WriteAllBytes(photo, [1, 2, 3]);
        File.WriteAllBytes(video, [4, 5, 6]);
        File.WriteAllBytes(zip, [7, 8, 9]);
        return ([
            new ExternalCandidate(1, photo, false, MediaType.Image),
            new ExternalCandidate(2, video, false, MediaType.Video),
            new ExternalCandidate(3, zip, false, MediaType.Archive),
            new ExternalCandidate(4, Path.Combine(_dir, "weg.jpg"), false, MediaType.Image),
            new ExternalCandidate(5, photo, true, MediaType.Image),
        ], photo, video);
    }

    [Fact]
    public void Ziehen_traegt_intern_und_Datei_Drop_und_erlaubt_nach_aussen_nur_Kopieren()
    {
        TestUiThread.Run(() =>
        {
            var (candidates, photo, video) = Files();
            var plan = ExternalFileTransfer.Plan(candidates, blocked: false);
            plan.Paths.Should().Equal(photo, video);
            plan.SkippedMissing.Should().Be(2, "nicht auf der Platte bzw. als fehlend markiert");
            plan.SkippedArchives.Should().Be(1);

            var (data, allowed) = ExternalFileTransfer.CreateDragData([1, 2, 3, 4, 5], plan);

            allowed.Should().Be(DragDropEffects.Copy, "fremde Ziele bekommen nie Verschieben oder Verknüpfen");
            GalleryDragData.Read(data).Should().Equal(1, 2, 3, 4, 5);
            data.GetFileDropList().Cast<string>().Should().Equal(photo, video);
            var preferred = (MemoryStream)data.GetData(ExternalFileTransfer.PreferredDropEffectFormat)!;
            BitConverter.ToInt32(preferred.ToArray()).Should().Be((int)DragDropEffects.Copy);

            // interne Ziele: Tasten entscheiden, der Effekt ist nur der Mauszeiger
            GalleryDragData.EffectFor(DragDropKeyStates.None, allowed).Should().Be(DragDropEffects.Copy);
            GalleryDragData.EffectFor(DragDropKeyStates.ShiftKey, allowed).Should().Be(DragDropEffects.Copy);
            GalleryDragData.IsMove(DragDropKeyStates.ShiftKey).Should().BeTrue();
            GalleryDragData.LinkEffect(allowed).Should().Be(DragDropEffects.Copy);
            return true;
        }, Timeout);
    }

    [Fact]
    public void Versteckte_Bibliothek_zieht_nur_intern_und_kopiert_nichts_in_die_Zwischenablage()
    {
        TestUiThread.Run(() =>
        {
            var (candidates, _, _) = Files();
            var plan = ExternalFileTransfer.Plan(candidates, blocked: true);
            plan.Blocked.Should().BeTrue();

            var (data, allowed) = ExternalFileTransfer.CreateDragData([1, 2], plan);

            allowed.Should().Be(DragDropEffects.Link | DragDropEffects.Move, "nur interne Ziele wie bisher (ADR-0054)");
            data.GetDataPresent(DataFormats.FileDrop).Should().BeFalse("keine Pfade nach außen");
            GalleryDragData.EffectFor(DragDropKeyStates.None, allowed).Should().Be(DragDropEffects.Link);
            GalleryDragData.EffectFor(DragDropKeyStates.ShiftKey, allowed).Should().Be(DragDropEffects.Move);

            DataObject? clipboard = null;
            ExternalFileTransfer.CopyToClipboard(plan, d => clipboard = d).Should().Be(ExternalFileSet.BlockedText);
            clipboard.Should().BeNull();
            return true;
        }, Timeout);
    }

    [Fact]
    public void StrgC_legt_Dateien_als_Kopie_in_die_Zwischenablage_und_nennt_Ausgelassene()
    {
        TestUiThread.Run(() =>
        {
            var (candidates, photo, video) = Files();
            DataObject? clipboard = null;

            var text = ExternalFileTransfer.CopyToClipboard(ExternalFileTransfer.Plan(candidates, false), d => clipboard = d);

            text.Should().Be("2 Dateien in die Zwischenablage kopiert — 2 fehlende Dateien und 1 Archiv ausgelassen.");
            clipboard!.GetFileDropList().Cast<string>().Should().Equal(photo, video);
            BitConverter.ToInt32(((MemoryStream)clipboard.GetData(ExternalFileTransfer.PreferredDropEffectFormat)!).ToArray()).Should().Be((int)DragDropEffects.Copy);

            ExternalFileTransfer.CopyToClipboard(ExternalFileTransfer.Plan([candidates[3]], false), d => clipboard = null)
                .Should().Be("Nichts kopiert — 1 fehlende Datei ausgelassen.");
            return true;
        }, Timeout);
    }

    [Fact]
    public void Menue_Kopieren_nutzt_die_gleiche_Sperre()
    {
        TestUiThread.Run(() =>
        {
            var (candidates, _, _) = Files();
            var actions = PhotoMenuTestKit.Actions(blocked: true);
            DataObject? clipboard = null;
            actions.ClipboardSetter = d => clipboard = d;

            actions.IsExternalBlocked.Should().BeTrue();
            actions.CopyToClipboard(candidates).Should().Be(ExternalFileSet.BlockedText);
            clipboard.Should().BeNull();
            return true;
        }, Timeout);
    }
}
