using Lumora.Imaging.Protocol;
using Lumora.Imaging.Worker;
using Lumora.Imaging.Worker.Decoding;

namespace Lumora.Imaging.Tests.Worker;

/// <summary>Einstieg des Hilfsprozesses ohne Prozess: Fehler aus parallelen Abschnitten, Abbrechen wartender Anfragen.</summary>
public sealed class WorkerEntryTests
{
    [Fact]
    public void Sammelfehler_aus_Parallel_For_liefert_den_massgeblichen_Fehler()
    {
        var decode = new SourceDecodeException(3, "nicht lesbar");
        Program.Unwrap(new AggregateException(new InvalidOperationException("x"), decode)).Should().BeSameAs(decode, "unlesbare Quelle → verständliche Meldung statt „Unknown“");

        var oom = new OutOfMemoryException();
        Program.Unwrap(new AggregateException(decode, new AggregateException(oom))).Should().BeSameAs(oom, "Speicher zuerst — der Prozess beendet sich danach");

        var canceled = new OperationCanceledException();
        Program.Unwrap(new AggregateException(decode, canceled)).Should().BeSameAs(canceled);

        var worker = new WorkerException(WorkerErrorCode.NotEnoughOverlap, "zu wenig Überlappung");
        Program.Unwrap(new AggregateException(new InvalidOperationException("x"), worker)).Should().BeSameAs(worker);
    }

    [Fact]
    public void Abbrechen_einer_wartenden_Anfrage_wirkt_bei_ihrem_Start()
    {
        var current = new Program.CurrentJob();
        current.Cancel(7); // Anfrage 7 steht noch in der Warteschlange

        using (var first = current.Begin(6))
        {
            first.IsCancellationRequested.Should().BeFalse("nur die gemeinte Anfrage");
            current.End();
        }

        using (var seventh = current.Begin(7))
        {
            seventh.IsCancellationRequested.Should().BeTrue("der Abbruch kam vor dem Start und wurde gemerkt");
            current.End();
        }

        using var eighth = current.Begin(8);
        eighth.IsCancellationRequested.Should().BeFalse();
        current.Cancel(8);
        eighth.IsCancellationRequested.Should().BeTrue("laufende Anfrage wie bisher");
        current.End();
    }
}
