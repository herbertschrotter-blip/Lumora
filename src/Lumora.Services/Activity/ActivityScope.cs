namespace Lumora.Services.Activity;

/// <summary>
/// Der Vorgang des aktuellen asynchronen Ablaufs (AsyncLocal, ADR-0067). Wer einen Vorgang startet und darin weitere
/// Dienste aufruft, setzt ihn hier: verschachtelte Datei-Batches legen dann keinen eigenen Eintrag an (ein Sortier-Lauf =
/// ein Vorgang), der <c>FileOperationService</c> meldet seinen Fortschritt je Datei an den Besitzer
/// (<see cref="ReportItems"/>), und eine wartende Scan-Sperre zeigt sich als „wartet“ am richtigen Eintrag.
/// <see cref="Suppress"/> unterdrückt Einträge ganz (Hintergrund-Papierkorb beim automatischen Entpacken).
/// </summary>
public static class ActivityScope
{
    private static readonly AsyncLocal<Frame?> CurrentFrame = new();

    /// <summary>Vorgang des aktuellen Ablaufs oder null.</summary>
    public static IActivityHandle? Current => CurrentFrame.Value?.Handle;

    /// <summary>true, wenn verschachtelte Vorgänge keinen eigenen Eintrag anlegen sollen (Besitzer vorhanden oder unterdrückt).</summary>
    public static bool IsOwned => CurrentFrame.Value is not null;

    /// <summary>Setzt <paramref name="handle"/> für den Ablauf; <paramref name="acceptsItemProgress"/>: Datei-Fortschritt melden.</summary>
    public static IDisposable Enter(IActivityHandle handle, bool acceptsItemProgress = false)
        => Push(new Frame(handle, acceptsItemProgress));

    /// <summary>Keine Einträge für verschachtelte Datei-Vorgänge dieses Ablaufs.</summary>
    public static IDisposable Suppress() => Push(new Frame(null, false));

    /// <summary>Fortschritt „<paramref name="done"/> von <paramref name="total"/>“ an den Besitzer, wenn er Datei-Fortschritt annimmt.</summary>
    public static void ReportItems(int done, int total)
    {
        if (CurrentFrame.Value is { AcceptsItemProgress: true, Handle: { } handle })
        {
            handle.Report(done, total);
        }
    }

    private static IDisposable Push(Frame frame)
    {
        var previous = CurrentFrame.Value;
        CurrentFrame.Value = frame;
        return new Restore(previous);
    }

    private sealed record Frame(IActivityHandle? Handle, bool AcceptsItemProgress);

    private sealed class Restore(Frame? previous) : IDisposable
    {
        public void Dispose() => CurrentFrame.Value = previous;
    }
}
