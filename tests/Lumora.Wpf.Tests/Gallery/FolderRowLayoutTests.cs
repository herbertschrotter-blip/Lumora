using System;
using System.Linq;
using FluentAssertions;
using Lumora.Gallery.FolderRows;
using Lumora.Gallery.ViewModels;
using Xunit;

namespace Lumora.Wpf.Tests.Gallery;

/// <summary>Ansicht „Ordner“ (Mockup 21): Auswahl und Verteilung einer Zeile.</summary>
public sealed class FolderRowLayoutTests
{
    private static readonly int[] Ids = Enumerable.Range(100, 50).ToArray();

    [Fact]
    public void Pick_hat_immer_erstes_und_letztes_und_dazwischen_verschiedene_Zufallsfotos()
    {
        var pick = FolderRowLayout.Pick(Ids, 20, seed: 7);

        pick.First.Should().Be(100);
        pick.Last.Should().Be(149);
        pick.Middle.Should().HaveCount(20).And.OnlyHaveUniqueItems().And.NotContain(new[] { 100, 149 });
        pick.Middle.Should().OnlyContain(id => id > 100 && id < 149);
    }

    [Fact]
    public void Pick_gleicher_Startwert_gleiche_Auswahl_anderer_Startwert_andere()
    {
        FolderRowLayout.Pick(Ids, 10, 7).Middle.Should().Equal(FolderRowLayout.Pick(Ids, 10, 7).Middle);
        FolderRowLayout.Pick(Ids, 10, 8).Middle.Should().NotEqual(FolderRowLayout.Pick(Ids, 10, 7).Middle);
    }

    [Fact]
    public void Pick_kleine_Ordner()
    {
        FolderRowLayout.Pick(Array.Empty<int>(), 10, 1).Should().Be(FolderRowPick.Empty);
        FolderRowLayout.Pick(new[] { 5 }, 10, 1).Should().BeEquivalentTo(new FolderRowPick(5, null, Array.Empty<int>()));
        var three = FolderRowLayout.Pick(new[] { 1, 2, 3 }, 10, 1);
        (three.First, three.Last).Should().Be((1, 3));
        three.Middle.Should().Equal(2);
    }

    [Fact]
    public void Fit_fuellt_die_Breite_genau_und_ordnet_nach_Aufnahme_mit_erstem_und_letztem_aussen()
    {
        var first = new FolderRowCandidate(1, 0, 2.0 / 3);
        var last = new FolderRowCandidate(99, 98, 1.5);
        var middle = Enumerable.Range(0, 30).Select(i => new FolderRowCandidate(10 + i, 60 - i, i % 2 == 0 ? 2.0 / 3 : 1.5)).ToList();

        var slots = FolderRowLayout.Fit(first, last, middle, totalCount: 99, width: 1000, height: 150);

        slots[0].Should().Be(slots[0] with { Id = 1, Edge = FolderRowEdge.First });
        slots[^1].Id.Should().Be(99);
        slots[^1].Edge.Should().Be(FolderRowEdge.Last);
        var positions = slots.Skip(1).SkipLast(1).Select(s => middle.Single(m => m.Id == s.Id).Position).ToList();
        positions.Should().BeInAscendingOrder("dazwischen in Aufnahme-Reihenfolge");
        (slots.Sum(s => s.Width) + FolderRowLayout.Spacing * (slots.Count - 1)).Should().BeApproximately(1000, 0.01, "randvoll");
        slots.Count.Should().BeGreaterThan(4);
    }

    [Fact]
    public void Fit_passen_alle_Fotos_bleiben_sie_natuerlich_breit()
    {
        var slots = FolderRowLayout.Fit(new(1, 0, 1), new(3, 2, 1), [new(2, 1, 1)], totalCount: 3, width: 1000, height: 100);

        slots.Select(s => s.Id).Should().Equal(1, 2, 3);
        slots.Should().OnlyContain(s => s.Width == 100);
    }

    [Fact]
    public void Fit_schmale_Zeile_zeigt_wenigstens_erstes_und_letztes()
    {
        var slots = FolderRowLayout.Fit(new(1, 0, 1.5), new(9, 8, 1.5), [new(5, 4, 1)], totalCount: 9, width: 200, height: 150);

        slots.Select(s => s.Id).Should().Equal(1, 9);
        (slots.Sum(s => s.Width) + FolderRowLayout.Spacing).Should().BeApproximately(200, 0.01);
    }

    [Theory]
    [InlineData("2024-05-12", "2024-05-12", "12. Mai 2024")]
    [InlineData("2024-05-12", "2024-05-14", "12.–14. Mai 2024")]
    [InlineData("2024-05-28", "2024-06-03", "28. Mai – 3. Juni 2024")]
    [InlineData("2023-12-30", "2024-01-02", "30. Dez. 2023 – 2. Jan. 2024")]
    public void Zeitraum_der_Zeile(string from, string to, string expected)
        => GalleryViewModel.FormatDateRange(DateTime.Parse(from), DateTime.Parse(to)).Should().Be(expected);
}
