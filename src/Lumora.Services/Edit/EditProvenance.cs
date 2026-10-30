using System.Globalization;
using System.Security;
using System.Text;

namespace Lumora.Services.Edit;

/// <summary>
/// Kennzeichnung „KI-bearbeitet“ (ADR-0076 Punkt 8.4): IPTC <c>DigitalSourceType</c> = <c>compositeWithTrainedAlgorithmicMedia</c>,
/// <c>xmp:CreatorTool</c> „Lumora KI-Bearbeitung (Vorlage, Modell, Datum)“ und für <c>PhotoVariants.Tools</c>
/// „ki:&lt;vorlage&gt;:&lt;modell&gt;“. Ohne Pfade oder Freitext — nur feste Angaben.
/// </summary>
public sealed record EditProvenance(EditTemplate Template, string Model, DateTimeOffset At)
{
    public const string DigitalSourceType = "http://cv.iptc.org/newscodes/digitalsourcetype/compositeWithTrainedAlgorithmicMedia";

    /// <summary>„ki:entfernen:flux1-fill-dev-Q4_K_S“ — Modell ohne Ordner und Endung.</summary>
    public string Tools => $"ki:{TemplateKey}:{ModelKey}";

    /// <summary>„Lumora KI-Bearbeitung (Entfernen, flux1-fill-dev-Q4_K_S, 2026-10-30)“.</summary>
    public string Software => $"Lumora KI-Bearbeitung ({TemplateLabel}, {ModelKey}, {At.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)})";

    private string ModelKey => string.IsNullOrWhiteSpace(Model) ? "unbekannt" : Path.GetFileNameWithoutExtension(Model.Replace('\\', '/').Split('/')[^1]);

    private string TemplateKey => Template switch
    {
        EditTemplate.Background => "hintergrund",
        EditTemplate.Outfit => "outfit",
        EditTemplate.Remove => "entfernen",
        EditTemplate.Expand => "erweitern",
        _ => "verbessern",
    };

    private string TemplateLabel => Template switch
    {
        EditTemplate.Background => "Hintergrund",
        EditTemplate.Outfit => "Outfit",
        EditTemplate.Remove => "Entfernen",
        EditTemplate.Expand => "Erweitern",
        _ => "Verbessern",
    };

    /// <summary>XMP-Paket mit den beiden Angaben (UTF-8, ohne Füllzeichen).</summary>
    public string ToXmp()
        => "<?xpacket begin=\"﻿\" id=\"W5M0MpCehiHzreSzNTczkc9d\"?>"
           + "<x:xmpmeta xmlns:x=\"adobe:ns:meta/\"><rdf:RDF xmlns:rdf=\"http://www.w3.org/1999/02/22-rdf-syntax-ns#\">"
           + "<rdf:Description rdf:about=\"\" xmlns:xmp=\"http://ns.adobe.com/xap/1.0/\" xmlns:Iptc4xmpExt=\"http://iptc.org/std/Iptc4xmpExt/2008-02-29/\""
           + $" xmp:CreatorTool=\"{SecurityElement.Escape(Software)}\" Iptc4xmpExt:DigitalSourceType=\"{DigitalSourceType}\"/>"
           + "</rdf:RDF></x:xmpmeta><?xpacket end=\"w\"?>";
}

/// <summary>XMP in eine JPEG-Datei schreiben (APP1 „http://ns.adobe.com/xap/1.0/“), ein vorhandenes XMP wird ersetzt.</summary>
public static class JpegXmp
{
    private static readonly byte[] Header = Encoding.ASCII.GetBytes("http://ns.adobe.com/xap/1.0/\0");

    /// <summary>Neue JPEG-Bytes mit dem XMP nach SOI und den APP0/EXIF-Blöcken; wirft bei keinem JPEG.</summary>
    public static byte[] Insert(byte[] jpeg, string xmp)
    {
        if (jpeg.Length < 4 || jpeg[0] != 0xFF || jpeg[1] != 0xD8)
        {
            throw new InvalidDataException("Kein JPEG.");
        }

        var payload = Encoding.UTF8.GetBytes(xmp);
        var length = Header.Length + payload.Length + 2;
        if (length > ushort.MaxValue)
        {
            throw new InvalidDataException("XMP zu groß für einen APP1-Block.");
        }

        using var output = new MemoryStream(jpeg.Length + length + 2);
        output.Write(jpeg, 0, 2);
        var pos = 2;
        while (pos + 4 <= jpeg.Length && jpeg[pos] == 0xFF)
        {
            var marker = jpeg[pos + 1];
            var segment = (jpeg[pos + 2] << 8) | jpeg[pos + 3];
            if (marker is < 0xE0 or > 0xEF || pos + 2 + segment > jpeg.Length)
            {
                break; // erstes Nicht-APP-Segment (DQT, SOF …): davor einfügen
            }

            var isXmp = marker == 0xE1 && segment >= Header.Length + 2 && jpeg.AsSpan(pos + 4, Header.Length).SequenceEqual(Header);
            if (!isXmp)
            {
                output.Write(jpeg, pos, segment + 2);
            }

            pos += segment + 2;
        }

        output.WriteByte(0xFF);
        output.WriteByte(0xE1);
        output.WriteByte((byte)(length >> 8));
        output.WriteByte((byte)(length & 0xFF));
        output.Write(Header);
        output.Write(payload);

        output.Write(jpeg, pos, jpeg.Length - pos);
        return output.ToArray();
    }

    /// <summary>Das XMP einer JPEG-Datei als Text, null ohne XMP (Tests, Prüfung).</summary>
    public static string? Read(byte[] jpeg)
    {
        var pos = 2;
        while (pos + 4 <= jpeg.Length && jpeg[pos] == 0xFF && jpeg[pos + 1] is >= 0xE0 and <= 0xEF)
        {
            var segment = (jpeg[pos + 2] << 8) | jpeg[pos + 3];
            if (jpeg[pos + 1] == 0xE1 && segment >= Header.Length + 2 && jpeg.AsSpan(pos + 4, Header.Length).SequenceEqual(Header))
            {
                return Encoding.UTF8.GetString(jpeg, pos + 4 + Header.Length, segment - 2 - Header.Length);
            }

            pos += segment + 2;
        }

        return null;
    }
}
