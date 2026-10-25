using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Win32;
using Lumora.Services.HiddenLibraries;

namespace Lumora.Shell.HiddenLibraries;

/// <summary>Antwort von „Versteckte Bibliothek anlegen…“.</summary>
public sealed record HiddenLibraryCreateAnswer(string Folder, string Name, string Password);

/// <summary>Antwort von „Öffnen“: das Kennwort bzw. bei <see cref="IsReset"/> das neue Kennwort.</summary>
public sealed record HiddenLibraryUnlockAnswer(string Password, bool IsReset);

/// <summary>Antwort von „Container öffnen…“; bei einer BitLocker-VHDX ist <see cref="DriveLetter"/> '\0' (Windows wählt den Buchstaben).</summary>
public sealed record ContainerMountAnswer(string ContainerFile, char DriveLetter);

/// <summary>Dialoge der versteckten Bibliotheken (ADR-0049); Tests ersetzen sie. null = abgebrochen.</summary>
public interface IHiddenLibraryDialogs
{
    /// <param name="validateFolder">Prüft den Zielordner (null = geeignet); der Dialog bleibt bei einer Meldung offen.</param>
    HiddenLibraryCreateAnswer? AskCreate(IReadOnlyList<string> drives, Func<string, Task<string?>> validateFolder);

    /// <param name="verify">Prüft das Kennwort; der Dialog bleibt bei false offen („Kennwort falsch“).</param>
    HiddenLibraryUnlockAnswer? AskUnlock(HiddenLibraryInfo library, bool canReset, Func<string, Task<bool>> verify);

    ContainerMountAnswer? AskMountContainer(IReadOnlyList<char> freeLetters);
}

/// <summary>Produktive Dialoge (modal über dem Hauptfenster).</summary>
public sealed class HiddenLibraryDialogs : IHiddenLibraryDialogs
{
    public HiddenLibraryCreateAnswer? AskCreate(IReadOnlyList<string> drives, Func<string, Task<string?>> validateFolder)
    {
        var dialog = new HiddenLibraryDialog(HiddenLibraryDialog.Mode.Create) { ValidateFolder = validateFolder };
        dialog.SetDrives(drives);
        return Show(dialog) ? dialog.CreateAnswer : null;
    }

    public HiddenLibraryUnlockAnswer? AskUnlock(HiddenLibraryInfo library, bool canReset, Func<string, Task<bool>> verify)
    {
        var dialog = new HiddenLibraryDialog(HiddenLibraryDialog.Mode.Unlock) { Verify = verify };
        dialog.SetLibrary(library.Name, canReset);
        return Show(dialog) ? dialog.UnlockAnswer : null;
    }

    public ContainerMountAnswer? AskMountContainer(IReadOnlyList<char> freeLetters)
    {
        var dialog = new HiddenLibraryDialog(HiddenLibraryDialog.Mode.Mount);
        dialog.SetLetters(freeLetters);
        return Show(dialog) ? dialog.MountAnswer : null;
    }

    private static bool Show(Window dialog)
    {
        var owner = Application.Current?.MainWindow;
        if (owner is { IsVisible: true })
        {
            dialog.Owner = owner;
        }
        else
        {
            dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }

        return dialog.ShowDialog() == true;
    }
}

/// <summary>Ein Fenster für Anlegen, Öffnen und Container öffnen (ADR-0049).</summary>
public sealed partial class HiddenLibraryDialog : Dialogs.AppFluentWindow
{
    public enum Mode
    {
        Create,
        Unlock,
        Mount,
    }

    private readonly Mode _mode;

    public HiddenLibraryDialog(Mode mode)
    {
        _mode = mode;
        InitializeComponent();
        var (title, intro, primary) = mode switch
        {
            Mode.Create => ("Versteckte Bibliothek anlegen",
                "Wählen Sie einen Ordner im eingehängten verschlüsselten Container. Lumora legt dort den Katalog an und scannt die Fotos.",
                "Anlegen"),
            Mode.Unlock => ("Versteckte Bibliothek öffnen", string.Empty, "Öffnen"),
            _ => ("Container öffnen", "Eine BitLocker-VHDX hängt Windows ein und fragt nach dem Kennwort; eine VeraCrypt-Datei hängt VeraCrypt unter dem gewählten Buchstaben ein.", "Öffnen"),
        };
        Title = title;
        DialogTitleBar.Title = title;
        IntroText.Text = intro;
        PrimaryButton.Content = primary;
        CreatePanel.Visibility = mode == Mode.Create ? Visibility.Visible : Visibility.Collapsed;
        UnlockPanel.Visibility = mode == Mode.Unlock ? Visibility.Visible : Visibility.Collapsed;
        MountPanel.Visibility = mode == Mode.Mount ? Visibility.Visible : Visibility.Collapsed;
        UpdateMountKind();
        Loaded += OnLoaded;
    }

    internal Func<string, Task<string?>>? ValidateFolder { get; init; }

    internal Func<string, Task<bool>>? Verify { get; init; }

    public HiddenLibraryCreateAnswer? CreateAnswer { get; private set; }

    public HiddenLibraryUnlockAnswer? UnlockAnswer { get; private set; }

    public ContainerMountAnswer? MountAnswer { get; private set; }

    internal void SetDrives(IReadOnlyList<string> drives)
    {
        DriveBox.ItemsSource = drives;
        DriveBox.SelectedIndex = drives.Count > 0 ? 0 : -1;
        if (drives.Count == 0)
        {
            ShowError("Kein passendes Laufwerk gefunden. Bitte zuerst den Container einhängen.");
        }
    }

    internal void SetLibrary(string name, bool canReset)
    {
        IntroText.Text = $"„{name}“ ist mit einem Lumora-Kennwort geschützt.";
        ResetCheck.Visibility = canReset ? Visibility.Visible : Visibility.Collapsed;
    }

    internal void SetLetters(IReadOnlyList<char> letters)
    {
        LetterBox.ItemsSource = letters.Select(l => $"{l}:").ToList();
        LetterBox.SelectedIndex = letters.Count > 0 ? letters.Count - 1 : -1;
    }

    /// <summary>Für Tests: Felder setzen und den Primärknopf auslösen.</summary>
    internal void FillCreate(string drive, string folder, string name, string password, string repeat)
    {
        DriveBox.SelectedItem = drive;
        FolderBox.Text = folder;
        NameBox.Text = name;
        NewPasswordBox.Password = password;
        RepeatPasswordBox.Password = repeat;
    }

    internal void FillUnlock(string password) => PasswordBox.Password = password;

    internal string? ErrorMessage => ErrorText.Visibility == Visibility.Visible ? ErrorText.Text : null;

    internal Task SubmitAsync() => SubmitCoreAsync();

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoaded;
        _ = _mode switch
        {
            Mode.Create => NameBox.Focus(),
            Mode.Unlock => PasswordBox.Focus(),
            _ => ContainerFileBox.Focus(),
        };
    }

    private void OnResetToggled(object sender, RoutedEventArgs e)
    {
        var reset = ResetCheck.IsChecked == true;
        ResetPanel.Visibility = reset ? Visibility.Visible : Visibility.Collapsed;
        PasswordBox.IsEnabled = !reset;
        PrimaryButton.Content = reset ? "Kennwort setzen und öffnen" : "Öffnen";
    }

    private async void OnPrimaryClick(object sender, RoutedEventArgs e) => await SubmitCoreAsync();

    private void OnCancelClick(object sender, RoutedEventArgs e) => Finish(false);

    private async void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            Finish(false);
        }
        else if (e.Key == Key.Enter && PrimaryButton.IsEnabled)
        {
            e.Handled = true;
            await SubmitCoreAsync();
        }
    }

    private async Task SubmitCoreAsync()
    {
        ShowError(null);
        SetBusy(true);
        try
        {
            var done = _mode switch
            {
                Mode.Create => await SubmitCreateAsync(),
                Mode.Unlock => await SubmitUnlockAsync(),
                _ => SubmitMount(),
            };
            if (done && IsLoaded)
            {
                Finish(true);
            }
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async Task<bool> SubmitCreateAsync()
    {
        if (DriveBox.SelectedItem is not string drive)
        {
            ShowError("Bitte ein Laufwerk wählen.");
            return false;
        }

        var sub = (FolderBox.Text ?? string.Empty).Trim().Trim('\\', '/');
        if (sub.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            ShowError("Der Ordnername enthält unzulässige Zeichen (nur ein Ordner direkt auf dem Laufwerk).");
            return false;
        }

        var folder = string.IsNullOrEmpty(sub) ? drive : Path.Combine(drive, sub);
        var name = (NameBox.Text ?? string.Empty).Trim();
        if (name.Length == 0)
        {
            ShowError("Bitte einen Namen eingeben.");
            return false;
        }

        if (HiddenLibraryPassword.Validate(NewPasswordBox.Password, RepeatPasswordBox.Password) is { } passwordProblem)
        {
            ShowError(passwordProblem);
            return false;
        }

        if (ValidateFolder is not null && await ValidateFolder(folder) is { } folderProblem)
        {
            ShowError(folderProblem);
            return false;
        }

        CreateAnswer = new HiddenLibraryCreateAnswer(folder, name, NewPasswordBox.Password);
        return true;
    }

    private async Task<bool> SubmitUnlockAsync()
    {
        if (ResetCheck.IsChecked == true)
        {
            if (HiddenLibraryPassword.Validate(ResetPasswordBox.Password, ResetRepeatBox.Password) is { } problem)
            {
                ShowError(problem);
                return false;
            }

            UnlockAnswer = new HiddenLibraryUnlockAnswer(ResetPasswordBox.Password, IsReset: true);
            return true;
        }

        if (string.IsNullOrEmpty(PasswordBox.Password))
        {
            ShowError("Bitte das Kennwort eingeben.");
            return false;
        }

        if (Verify is not null && !await Verify(PasswordBox.Password))
        {
            ShowError("Das Kennwort ist falsch.");
            PasswordBox.Clear();
            _ = PasswordBox.Focus();
            return false;
        }

        UnlockAnswer = new HiddenLibraryUnlockAnswer(PasswordBox.Password, IsReset: false);
        return true;
    }

    /// <summary>Für Tests: Containerdatei eintragen.</summary>
    internal void FillMount(string file) => ContainerFileBox.Text = file;

    /// <summary>Für Tests: Feld „Laufwerksbuchstabe“ sichtbar (nur VeraCrypt).</summary>
    internal bool IsLetterVisible => LetterPanel.Visibility == Visibility.Visible;

    private string MountFile => (ContainerFileBox.Text ?? string.Empty).Trim().Trim('"');

    private void OnContainerFileChanged(object sender, TextChangedEventArgs e) => UpdateMountKind();

    /// <summary>BitLocker-VHDX: Windows wählt den Buchstaben und fragt das Kennwort — kein Feld „Laufwerksbuchstabe“.</summary>
    private void UpdateMountKind()
    {
        var vhdx = IContainerTools.IsVirtualDiskFile(MountFile);
        LetterPanel.Visibility = vhdx ? Visibility.Collapsed : Visibility.Visible;
        MountHint.Text = vhdx
            ? "Windows hängt die VHDX ein (wie ein Doppelklick im Explorer, ggf. mit Rückfrage nach Administratorrechten) und fragt das BitLocker-Kennwort in seinem eigenen Fenster — Lumora sieht es nie. Lumora merkt sich weder Datei noch Laufwerk."
            : "VeraCrypt fragt das Container-Kennwort in seinem eigenen Fenster — Lumora sieht es nie. Lumora merkt sich weder Datei noch Laufwerk.";
    }

    private bool SubmitMount()
    {
        var file = MountFile;
        if (file.Length == 0 || !Path.IsPathFullyQualified(file) || !File.Exists(file))
        {
            ShowError("Bitte eine vorhandene Containerdatei angeben.");
            return false;
        }

        if (IContainerTools.IsVirtualDiskFile(file))
        {
            MountAnswer = new ContainerMountAnswer(file, '\0');
            return true;
        }

        if (LetterBox.SelectedItem is not string letter || letter.Length == 0)
        {
            ShowError("Bitte einen freien Laufwerksbuchstaben wählen.");
            return false;
        }

        MountAnswer = new ContainerMountAnswer(file, letter[0]);
        return true;
    }

    private void OnBrowseContainer(object sender, RoutedEventArgs e)
    {
        // AddToRecent = false: nichts unter „Zuletzt verwendet“; Lumora merkt sich die Datei nicht (ADR-0049)
        var dialog = new OpenFileDialog
        {
            Title = "Containerdatei wählen",
            Filter = "BitLocker-Container (*.vhdx;*.vhd)|*.vhdx;*.vhd|Alle Dateien (*.*)|*.*",
            CheckFileExists = true,
            AddToRecent = false,
        };
        if (dialog.ShowDialog(this) == true)
        {
            ContainerFileBox.Text = dialog.FileName;
        }
    }

    /// <summary>Modal: DialogResult setzen; nicht modal (Tests): nur schließen.</summary>
    private void Finish(bool result)
    {
        Confirmed = result;
        try
        {
            DialogResult = result;
        }
        catch (InvalidOperationException)
        {
            Close();
        }
    }

    internal bool Confirmed { get; private set; }

    private void SetBusy(bool busy)
    {
        Busy.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        PrimaryButton.IsEnabled = !busy;
    }

    private void ShowError(string? message)
    {
        ErrorText.Text = message ?? string.Empty;
        ErrorText.Visibility = string.IsNullOrEmpty(message) ? Visibility.Collapsed : Visibility.Visible;
    }
}
