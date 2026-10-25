#Requires -Version 7
<#
.SYNOPSIS
    Baut die portable Version von Lumora (vormals PhotoViewer V3; Ordner + ZIP, win-x64) per dotnet publish.

.DESCRIPTION
    Standard: self-contained (kein installiertes .NET nötig), kein Single-File, kein Trimming (WPF verträgt es nicht).
    Version = oberster Eintrag „## [x.y.z]“ in CHANGELOG.md (dieselbe Quelle liest Lumora.Wpf.csproj); das
    Skript bricht ab, wenn die veröffentlichte EXE eine andere Produktversion trägt.

    Ergebnis (Standard-Ausgabe artifacts\ in der Repo-Wurzel, per .gitignore ausgenommen):
      artifacts\Lumora-<Version>-win-x64\        Ordner mit Lumora.exe
      artifacts\Lumora-<Version>-win-x64.zip     derselbe Ordner gepackt

    Nicht im Paket: FFmpeg (Video-Vorschaubilder) — wird wie im Debug-Build im PATH bzw. unter den winget-Links gesucht
    („winget install Gyan.FFmpeg“). Daten liegen unabhängig vom Paketordner unter %LocalAppData%\Lumora
    (bzw. im Ordner aus database-location.json) — außer mit -Portable (ADR-0069).

.PARAMETER Runtime
    Runtime Identifier (nur win-x64 ist geprüft).

.PARAMETER OutputRoot
    Zielordner für Paketordner und ZIP; Standard: <Repo>\artifacts.

.PARAMETER FrameworkDependent
    Ohne mitgelieferte .NET-Runtime (kleiner, braucht aber die installierte „.NET 10 Desktop Runtime“ x64).

.PARAMETER NoZip
    Nur den Ordner erzeugen.

.PARAMETER Portable
    Portable Release-Installation (ADR-0069): legt die Markierungsdatei lumora.portable neben Lumora.exe. Lumora nutzt dann
    <Paketordner>\Daten (Katalog, Vorschaubilder, Protokolle, Sicherungen) statt %LocalAppData%\Lumora — getrennt von jeder
    anderen Installation, frischer Katalog beim ersten Start. LUMORA_APPDATA hat weiter Vorrang (Rauchtest).
    Paketname mit Anhang „-portable“.

.EXAMPLE
    pwsh -File build\publish.ps1

.EXAMPLE
    pwsh -File build\publish.ps1 -Portable
#>
[CmdletBinding()]
param(
    [string]$Runtime = 'win-x64',
    [string]$OutputRoot = '',
    [switch]$FrameworkDependent,
    [switch]$NoZip,
    [switch]$Portable
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repo 'src\Lumora.Wpf\Lumora.Wpf.csproj'
$changelog = Join-Path $repo 'CHANGELOG.md'
if (-not $OutputRoot) { $OutputRoot = Join-Path $repo 'artifacts' }

# --- Version aus dem obersten CHANGELOG-Eintrag -------------------------------------------------------------------
$match = Select-String -LiteralPath $changelog -Pattern '^## \[(\d+\.\d+\.\d+)\]' | Select-Object -First 1
# In Zeichenketten keine typografischen Anführungszeichen: PowerShell liest „ und “ als Stringgrenze.
if (-not $match) { throw "Kein Eintrag '## [x.y.z]' in $changelog gefunden." }
$version = $match.Matches[0].Groups[1].Value

$flavor = if ($FrameworkDependent) { '-fdd' } else { '' }
if ($Portable) { $flavor += '-portable' } # ADR-0069
$packageName = "Lumora-$version-$Runtime$flavor"
$outDir = Join-Path $OutputRoot $packageName
$zipPath = "$outDir.zip"

Write-Host "Lumora $version → $outDir ($(if ($FrameworkDependent) { 'framework-dependent' } else { 'self-contained' }))"
if (Test-Path -LiteralPath $outDir) { Remove-Item -LiteralPath $outDir -Recurse -Force }
if (Test-Path -LiteralPath $zipPath) { Remove-Item -LiteralPath $zipPath -Force }
New-Item -ItemType Directory -Force -Path $OutputRoot | Out-Null

# --- dotnet publish -----------------------------------------------------------------------------------------------
# VlcWindowsX86Enabled=false: das LibVLC-Paket kopiert bei AnyCPU sonst auch libvlc\win-x86 (~90 MB) mit.
# SatelliteResourceLanguages=de: nur deutsche Ressourcen der Runtime/WPF/WPF-UI statt aller Sprachen.
$publishArgs = @(
    'publish', $project,
    '-c', 'Release',
    '-r', $Runtime,
    '--self-contained', $(if ($FrameworkDependent) { 'false' } else { 'true' }),
    '-o', $outDir,
    "-p:Version=$version",
    '-p:PublishSingleFile=false',
    '-p:PublishTrimmed=false',
    '-p:PublishReadyToRun=false',
    '-p:VlcWindowsX86Enabled=false',
    '-p:SatelliteResourceLanguages=de',
    '--nologo'
)
& dotnet @publishArgs
if ($LASTEXITCODE -ne 0) { throw "dotnet publish fehlgeschlagen (ExitCode $LASTEXITCODE)." }

# --- Prüfen -------------------------------------------------------------------------------------------------------
$exe = Join-Path $outDir 'Lumora.exe'
if (-not (Test-Path -LiteralPath $exe)) { throw "EXE fehlt im Paket: $exe" }
$productName = (Get-Item -LiteralPath $exe).VersionInfo.ProductName
if ($productName -ne 'Lumora') { throw "Produktname der EXE ($productName) ist nicht 'Lumora'." }

$productVersion = (Get-Item -LiteralPath $exe).VersionInfo.ProductVersion
if ($productVersion -ne $version) {
    throw "Produktversion der EXE ($productVersion) weicht vom CHANGELOG ($version) ab."
}

$required = @('e_sqlite3.dll', 'libvlc\win-x64\libvlc.dll', 'libvlc\win-x64\plugins')
if (-not $FrameworkDependent) { $required += @('coreclr.dll', 'PresentationFramework.dll') }
foreach ($item in $required) {
    if (-not (Test-Path -LiteralPath (Join-Path $outDir $item))) { throw "Pflichtbestandteil fehlt im Paket: $item" }
}
if (Test-Path -LiteralPath (Join-Path $outDir 'libvlc\win-x86')) { throw 'libvlc\win-x86 sollte nicht im x64-Paket liegen.' }
# Nie Daten im Paket (kein Katalog, keine Protokolle); ein Ordner Daten neben der EXE schaltete den portablen Modus ein
if (Test-Path -LiteralPath (Join-Path $outDir 'Daten')) { throw 'Ordner Daten darf nicht im Paket liegen.' }

# --- Portabler Modus (ADR-0069) ----------------------------------------------------------------------------------
$marker = Join-Path $outDir 'lumora.portable'
if ($Portable) {
    $markerText = @(
        'Lumora - portabler Modus (ADR-0069).',
        'Solange diese Datei neben Lumora.exe liegt, speichert Lumora Katalog, Vorschaubilder, Protokolle und',
        'Katalog-Sicherungen im Ordner Daten neben Lumora.exe statt unter %LocalAppData%\Lumora.',
        'Datei loeschen = Standardmodus (der Ordner Daten schaltet den portablen Modus aber ebenfalls ein).'
    )
    Set-Content -LiteralPath $marker -Value $markerText -Encoding utf8
    Write-Host 'Portabler Modus: lumora.portable liegt im Paket'
} elseif (Test-Path -LiteralPath $marker) {
    throw 'lumora.portable im Standardpaket gefunden.'
}
# Werkzeuge für 'dotnet ef' gehören nicht ins Paket (Data-Projekt: EF-Design-Paket ohne contentfiles, 0.2.31).
$designOnly = @(Get-ChildItem -LiteralPath $outDir -Directory -Filter 'BuildHost-*') +
              @(Get-ChildItem -LiteralPath $outDir -File -Filter 'Microsoft.EntityFrameworkCore.Design.dll')
if ($designOnly.Count -gt 0) { throw "Design-Zeit-Bestandteile im Paket: $($designOnly.Name -join ', ')" }

$files = Get-ChildItem -LiteralPath $outDir -Recurse -File
$folderMb = [math]::Round(($files | Measure-Object Length -Sum).Sum / 1MB, 1)
Write-Host "Ordner: $($files.Count) Dateien, $folderMb MB — Produktversion $productVersion OK"

# --- ZIP ----------------------------------------------------------------------------------------------------------
if (-not $NoZip) {
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [System.IO.Compression.ZipFile]::CreateFromDirectory(
        $outDir, $zipPath, [System.IO.Compression.CompressionLevel]::Optimal, $true)
    $zipMb = [math]::Round((Get-Item -LiteralPath $zipPath).Length / 1MB, 1)
    Write-Host "ZIP:    $zipPath ($zipMb MB)"
}

Write-Host "Fertig: $exe"
