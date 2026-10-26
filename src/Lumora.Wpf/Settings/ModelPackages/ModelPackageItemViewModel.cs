using System;
using System.Globalization;
using System.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Lumora.Services.ModelPackages;
using Wpf.Ui.Controls;

namespace Lumora.Settings.ModelPackages;

/// <summary>Farbe der Zustands-Marke.</summary>
public enum ModelPackageTone
{
    Neutral = 0,
    Success = 1,
    Accent = 2,
    Caution = 3,
    Critical = 4,
}

/// <summary>Eine Zeile in Einstellungen › Modelle &amp; Daten (Mockup 19).</summary>
public sealed partial class ModelPackageItemViewModel : ObservableObject
{
    private readonly ModelPackagesViewModel _owner;

    internal ModelPackageItemViewModel(IModelPackageSource source, ModelPackagesViewModel owner)
    {
        Source = source;
        _owner = owner;
        var d = source.Definition;
        Id = d.Id;
        Title = d.Title;
        Name = d.Name;
        Purpose = d.Purpose;
        Group = d.Group;
        VersionText = d.Group == ModelPackageGroup.Model ? $"Version {d.Version}" : d.Version;
        LicenseText = d.License;
        LicenseRestricted = d.LicenseRestricted;
        SourceText = d.Origin.Name;
        Website = d.Origin.Website;
        WebsiteText = d.Origin.Website?.Host;
        Folder = source.Folder;
        IsOverridden = source.IsOverridden;
        Symbol = SymbolFor(d.Id, d.Group);
        UpdateHint = d.UpdateSource is null ? ModelUpdateChecker.NoSourceText(d) : "Update-Suche beim Hersteller nur auf Klick.";
        SizeText = d.InstalledSize > 0 ? ModelDownloadDialog.SizeText(d.InstalledSize) : null;
    }

    internal IModelPackageSource Source { get; }

    public string Id { get; }

    public string Title { get; }

    public string Name { get; }

    public string Purpose { get; }

    public ModelPackageGroup Group { get; }

    public string VersionText { get; }

    public string LicenseText { get; }

    /// <summary>Lizenz mit Einschränkung (z. B. nur nicht-kommerziell) — hervorgehoben.</summary>
    public bool LicenseRestricted { get; }

    public string SourceText { get; }

    public Uri? Website { get; }

    public string? WebsiteText { get; }

    /// <summary>Speicherort (nur Anzeige, nie im Protokoll).</summary>
    public string? Folder { get; }

    public bool IsOverridden { get; }

    public SymbolRegular Symbol { get; }

    /// <summary>„Version x, keine automatische Prüfung“ bzw. Hinweis auf die Update-Suche.</summary>
    public string UpdateHint { get; }

    [ObservableProperty] private string? _sizeText;
    [ObservableProperty] private ModelPackageState _state;
    [ObservableProperty] private string _stateText = "wird ermittelt…";
    [ObservableProperty] private ModelPackageTone _tone;
    [ObservableProperty] private string? _detail;
    [ObservableProperty] private string? _checkedText;
    [ObservableProperty] private bool _canLoad;
    [ObservableProperty] private string _loadLabel = "Laden…";
    [ObservableProperty] private bool _canVerify;
    [ObservableProperty] private bool _canRemove;
    [ObservableProperty] private bool _canCheckUpdate;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private bool _isDownloading;
    [ObservableProperty] private double _progressPercent;
    [ObservableProperty] private string? _progressText;
    [ObservableProperty] private string? _message;
    [ObservableProperty] private bool _messageIsError;

    public bool IsExternal => Group == ModelPackageGroup.External;

    public bool HasWebsite => Website is not null;

    internal CancellationTokenSource? DownloadCts { get; set; }

    /// <summary>Zustand übernehmen (UI-Thread).</summary>
    internal void Apply(ModelPackageStatus status)
    {
        State = status.State;
        StateText = status.Text;
        Detail = status.Detail;
        Tone = status.State switch
        {
            ModelPackageState.Installed or ModelPackageState.ExternalReady => ModelPackageTone.Success,
            ModelPackageState.UpdateAvailable or ModelPackageState.BuiltIn or ModelPackageState.OnDemand => ModelPackageTone.Accent,
            ModelPackageState.Broken => ModelPackageTone.Critical,
            ModelPackageState.ExternalMissing => ModelPackageTone.Caution,
            _ => ModelPackageTone.Neutral,
        };
        CheckedText = status.CheckedUtc is { } at
            ? $"Geprüft {at.ToLocalTime().ToString("g", CultureInfo.CurrentCulture)} · SHA-256 stimmt"
            : null;
        if (Group != ModelPackageGroup.Model && status.DiskBytes is { } bytes)
        {
            SizeText = ModelDownloadDialog.SizeText(bytes);
        }

        var caps = Source.Capabilities;
        CanLoad = caps.HasFlag(ModelPackageCapabilities.Download) && status.State is ModelPackageState.NotInstalled or ModelPackageState.Broken or ModelPackageState.UpdateAvailable;
        LoadLabel = status.State switch
        {
            ModelPackageState.Broken => "Neu laden…",
            ModelPackageState.UpdateAvailable => "Update laden…",
            _ => "Laden…",
        };
        CanVerify = caps.HasFlag(ModelPackageCapabilities.Verify) && status.State != ModelPackageState.NotInstalled;
        CanRemove = caps.HasFlag(ModelPackageCapabilities.Remove) && status.State is ModelPackageState.Installed or ModelPackageState.Broken or ModelPackageState.UpdateAvailable;
        CanCheckUpdate = Group == ModelPackageGroup.Model;
        NotifyCommands();
    }

    partial void OnIsBusyChanged(bool value) => NotifyCommands();

    internal void ShowMessage(string? text, bool error)
    {
        MessageIsError = error;
        Message = text;
    }

    private void NotifyCommands()
    {
        LoadCommand.NotifyCanExecuteChanged();
        VerifyCommand.NotifyCanExecuteChanged();
        RemoveCommand.NotifyCanExecuteChanged();
        CheckUpdateCommand.NotifyCanExecuteChanged();
        CancelCommand.NotifyCanExecuteChanged();
    }

    private bool CanRunLoad() => CanLoad && !IsBusy;

    private bool CanRunVerify() => CanVerify && !IsBusy;

    private bool CanRunRemove() => CanRemove && !IsBusy;

    private bool CanRunCheckUpdate() => CanCheckUpdate && !IsBusy;

    private bool CanRunCancel() => IsDownloading;

    [RelayCommand(CanExecute = nameof(CanRunLoad))]
    private System.Threading.Tasks.Task LoadAsync() => _owner.LoadAsync(this);

    [RelayCommand(CanExecute = nameof(CanRunVerify))]
    private System.Threading.Tasks.Task VerifyAsync() => _owner.VerifyAsync(this);

    [RelayCommand(CanExecute = nameof(CanRunRemove))]
    private System.Threading.Tasks.Task RemoveAsync() => _owner.RemoveAsync(this);

    [RelayCommand(CanExecute = nameof(CanRunCheckUpdate))]
    private System.Threading.Tasks.Task CheckUpdateAsync() => _owner.CheckUpdateAsync(this);

    [RelayCommand(CanExecute = nameof(CanRunCancel))]
    private void Cancel() => DownloadCts?.Cancel();

    [RelayCommand]
    private void OpenWebsite() => _owner.OpenWebsite(this);

    [RelayCommand]
    private void OpenAssistantSettings() => _owner.OpenAssistantSettings();

    partial void OnIsDownloadingChanged(bool value) => CancelCommand.NotifyCanExecuteChanged();

    private static SymbolRegular SymbolFor(string id, ModelPackageGroup group) => id switch
    {
        ModelPackageCatalog.FacesId => SymbolRegular.PersonCircle24,
        ModelPackageCatalog.MotifsId => SymbolRegular.ImageSearch24,
        ModelPackageCatalog.GeoNamesId => SymbolRegular.Location24,
        ModelPackageCatalog.MapTilesId => SymbolRegular.Map24,
        ModelPackageCatalog.AssistantId => SymbolRegular.Chat24,
        Lumora.Services.Enhance.EnhanceModelCatalog.UpscaleId => SymbolRegular.ArrowExpand24, // Foto verbessern (ADR-0071)
        Lumora.Services.Enhance.EnhanceModelCatalog.UpscaleNaturalId => SymbolRegular.ArrowExpand24,
        Lumora.Services.Enhance.EnhanceModelCatalog.FacesId => SymbolRegular.PersonSquare24,
        Lumora.Services.Enhance.EnhanceModelCatalog.ColorizeId => SymbolRegular.Color24,
        Lumora.Services.Enhance.EnhanceModelCatalog.InpaintId => SymbolRegular.Eraser24,
        _ => group == ModelPackageGroup.Model ? SymbolRegular.BrainCircuit24 : SymbolRegular.Database24,
    };
}
