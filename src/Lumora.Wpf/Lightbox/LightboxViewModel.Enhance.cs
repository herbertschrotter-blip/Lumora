using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Input;
using Lumora.Enhance;

namespace Lumora.Lightbox;

/// <summary>
/// Leiste „Verbessern“ in der Lightbox (ADR-0071, Mockup 13). Die Logik liegt im <see cref="EnhancePanelViewModel"/>; hier nur
/// Anbindung an Blättern, Status und Lebenszyklus. Ohne Dienst (Tests) bleibt <see cref="Enhance"/> null und der Knopf aus.
/// </summary>
public sealed partial class LightboxViewModel
{
    /// <summary>Leiste „Verbessern“ (null ohne Dienst).</summary>
    public EnhancePanelViewModel? Enhance { get; private set; }

    public bool HasEnhance => Enhance is not null;

    private void InitEnhance(EnhancePanelFactory? factory)
    {
        Enhance = factory?.Create();
        if (Enhance is not null)
        {
            Enhance.Saved += (_, result) => ShowStatus($"Verbesserte Fassung gespeichert: {System.IO.Path.GetFileName(result.Path)} — das Original bleibt unverändert.");
        }
    }

    /// <summary>Beim Blättern: neues Foto in der Leiste (Vorschau und Pinsel werden verworfen).</summary>
    private void UpdateEnhanceForCurrent()
    {
        var item = CurrentItem;
        _ = Enhance?.SetFileAsync(item?.FileId, item is { IsImage: true });
    }

    /// <summary>Knopf „Verbessern“ (Taste E): Leiste öffnen/schließen.</summary>
    [RelayCommand]
    private Task ToggleEnhanceAsync() => Enhance is null ? Task.CompletedTask : Enhance.SetOpenAsync(!Enhance.IsOpen);

    /// <summary>Leiste öffnen (Rechtsklick „Bearbeiten ▸ Foto verbessern…“).</summary>
    public Task ShowEnhanceAsync() => Enhance is null || Enhance.IsOpen ? Task.CompletedTask : Enhance.SetOpenAsync(true);
}
