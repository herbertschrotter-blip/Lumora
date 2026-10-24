using System.Runtime.Versioning;
using System.Text;
using Lumora.Services.Operations;

namespace Lumora.Tests.Operations;

/// <summary>
/// Ordner im Papierkorb (ADR-0065): die <c>$R</c>-Seite eines gelöschten Ordners ist ein Ordner — das Verzeichnis des
/// Papierkorbs muss ihn finden. Nur gegen ein nachgebautes <c>$Recycle.Bin</c>-Verzeichnis in Temp, nie die echte Shell.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsRecycleBinFolderTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "Lumora.Tests", "binfolder-" + Guid.NewGuid().ToString("N"));

    public WindowsRecycleBinFolderTests() => Directory.CreateDirectory(_dir);

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

    private void WriteFolderEntry(string suffix, string originalPath, DateTime deletedUtc)
    {
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, Encoding.Unicode, leaveOpen: true))
        {
            writer.Write(2L);
            writer.Write(0L);
            writer.Write(deletedUtc.ToFileTimeUtc());
            writer.Write(originalPath.Length + 1);
            writer.Write(Encoding.Unicode.GetBytes(originalPath + "\0"));
        }

        File.WriteAllBytes(Path.Combine(_dir, "$I" + suffix), stream.ToArray());
        var data = Directory.CreateDirectory(Path.Combine(_dir, "$R" + suffix)).FullName;
        File.WriteAllBytes(Path.Combine(data, "bild.jpg"), [1, 2, 3]);
    }

    [Fact]
    public void Ordner_Eintrag_wird_gefunden()
    {
        var when = new DateTime(2026, 10, 24, 12, 0, 0, DateTimeKind.Utc);
        WriteFolderEntry("ABC123", @"D:\Fotos\Reise", when);

        var entry = WindowsRecycleBin.FindEntry(_dir, @"D:\Fotos\Reise", when.AddMinutes(-1));

        entry.Should().NotBeNull();
        Directory.Exists(entry!.DataPath).Should().BeTrue();
        WindowsRecycleBin.FindEntry(_dir, @"D:\Fotos\Reise", when.AddMinutes(1)).Should().BeNull("älter als der Batch");
    }

    [Fact]
    public void Fake_ohne_Ordner_Unterstuetzung_meldet_das_deutlich()
    {
        IRecycleBin bin = new Lumora.Tests.Fixtures.FakeRecycleBin();

        bin.GetUnavailableReasonFor(@"C:\x").Should().BeNull("Standard: Papierkorb vorhanden");
        var act = () => bin.RestoreDirectoryFromRecycleBin(@"C:\x", DateTime.UtcNow);
        act.Should().Throw<NotSupportedException>();
    }
}
