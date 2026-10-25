#Requires -Version 7
<#
.SYNOPSIS
    Integrations-Rauchtest (SmokeTestRunner) gegen eine beliebige Lumora.exe — Debug-Build oder portables Paket.

.DESCRIPTION
    Kopiert die Testbibliothek in einen frischen Arbeitsordner (der Rauchtest verschiebt/löscht dort Dateien), startet
    die EXE mit eigenem AppData-Root (LUMORA_APPDATA, frische DB) und den Rauchtest-Variablen, wartet auf das
    Ende (Timeout → Kill) und gibt smoke-result.txt sowie einen Log-Auszug aus. Keine Maus-/Tastatureingaben.
    ExitCode des Skripts: 0 = alle Schritte OK, 1 = Fehler/Timeout.

.PARAMETER Exe
    Pfad zur Lumora.exe (bis 0.2.48 PhotoViewerV3.exe), z. B. artifacts\Lumora-0.2.49-win-x64\Lumora.exe oder
    src\Lumora.Wpf\bin\Debug\net10.0-windows\Lumora.exe.

.PARAMETER TestLib
    Ordner der Testbibliothek (wird nur gelesen; mindestens 6 Dateien).

.PARAMETER WorkDir
    Arbeitsordner für Bibliothekskopie, AppData und Screenshots; Standard: %TEMP%\Lumora-smoke-<Tag>.

.PARAMETER NoShots
    Keine PNG-Screenshots der Hauptansichten (LUMORA_SMOKE_SHOTS bleibt leer).

.EXAMPLE
    pwsh -File build\run-smoke.ps1 -Exe artifacts\Lumora-0.2.49-win-x64\Lumora.exe -TestLib D:\testlib
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Exe,
    [Parameter(Mandatory)][string]$TestLib,
    [string]$Tag = 'run',
    [string]$WorkDir = '',
    [int]$TimeoutSec = 600,
    [switch]$NoShots
)
$ErrorActionPreference = 'Stop'

$exePath = (Resolve-Path -LiteralPath $Exe).Path
$testlib = (Resolve-Path -LiteralPath $TestLib).Path
# Prozessname = Dateiname der EXE (Lumora seit 0.2.49, ältere Pakete PhotoViewerV3)
$processName = [IO.Path]::GetFileNameWithoutExtension($exePath)
if (-not $WorkDir) { $WorkDir = Join-Path ([IO.Path]::GetTempPath()) "Lumora-smoke-$Tag" }
$lib = Join-Path $WorkDir 'lib'
$appdata = Join-Path $WorkDir 'appdata'
$shots = Join-Path $WorkDir 'shots'

if (Get-Process $processName -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $exePath }) {
    throw "$processName aus $exePath läuft bereits"
}

# Frische Testbibliothek (Verschieben/Löschen verändert die Kopie). Nur die eigenen Unterordner leeren, nie den
# ganzen WorkDir — der Parameter könnte auf einen Ordner mit fremden Dateien zeigen.
New-Item -ItemType Directory -Force -Path $WorkDir | Out-Null
foreach ($dir in @($lib, $appdata, $shots)) {
    if (Test-Path -LiteralPath $dir) { Remove-Item -LiteralPath $dir -Recurse -Force }
}
Copy-Item -LiteralPath $testlib -Destination $lib -Recurse
$expected = @(Get-ChildItem -LiteralPath $testlib -Recurse -File).Count
$count = @(Get-ChildItem -LiteralPath $lib -Recurse -File -ErrorAction SilentlyContinue).Count
if ($count -lt $expected) {
    # Copy-Item/robocopy kopierten im App-Container gelegentlich 0 Dateien → Datei für Datei über .NET nachkopieren
    foreach ($src in Get-ChildItem -LiteralPath $testlib -Recurse -File) {
        $dst = Join-Path $lib ([IO.Path]::GetRelativePath($testlib, $src.FullName))
        [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($dst)) | Out-Null
        [IO.File]::Copy($src.FullName, $dst, $true)
    }
    $count = @(Get-ChildItem -LiteralPath $lib -Recurse -File).Count
}
if ($count -lt 6) { throw "Testbibliothek-Kopie unvollständig: $count von $expected Dateien in $lib" }
Write-Output "EXE:            $exePath"
Write-Output "Testbibliothek: $lib ($count Dateien)"

New-Item -ItemType Directory -Force -Path $appdata | Out-Null
if (-not $NoShots) { New-Item -ItemType Directory -Force -Path $shots | Out-Null }

$env:LUMORA_APPDATA = $appdata
$env:LUMORA_SMOKE_ROOT = $lib
$env:LUMORA_SMOKE_SHOTS = if ($NoShots) { '' } else { $shots }
$env:LUMORA_SMOKE_EXIT = '1'
$env:LUMORA_SMOKE_CLEANUP = '0'
# Ältere Pakete (bis 0.4.2) lesen nur die alten Namen — mitsetzen, sonst nähmen sie den echten Katalog (ADR-0056)
foreach ($name in 'APPDATA', 'SMOKE_ROOT', 'SMOKE_SHOTS', 'SMOKE_EXIT', 'SMOKE_CLEANUP') {
    [Environment]::SetEnvironmentVariable("PHOTOVIEWERV3_$name", [Environment]::GetEnvironmentVariable("LUMORA_$name"))
}

$portableDataBefore = Test-Path -LiteralPath (Join-Path (Split-Path -Parent $exePath) 'Daten') # ADR-0069
$stdout = Join-Path $appdata 'stdout.txt'
$stderr = Join-Path $appdata 'stderr.txt'
$sw = [Diagnostics.Stopwatch]::StartNew()
$proc = Start-Process -FilePath $exePath -PassThru -WindowStyle Normal -RedirectStandardOutput $stdout -RedirectStandardError $stderr
Write-Output "Gestartet: PID $($proc.Id) — warte bis zu $TimeoutSec s …"
if (-not $proc.WaitForExit($TimeoutSec * 1000)) {
    Write-Output "TIMEOUT nach $TimeoutSec s — Prozess wird beendet"
    Stop-Process -Id $proc.Id -Force
    $proc.WaitForExit(10000) | Out-Null
    $exit = 'TIMEOUT'
} else {
    $exit = $proc.ExitCode
}
Write-Output "Beendet: ExitCode=$exit nach $([int]$sw.Elapsed.TotalSeconds) s"

$result = Join-Path $appdata 'smoke-result.txt'
Write-Output "`n== smoke-result.txt =="
if (Test-Path -LiteralPath $result) { Get-Content -LiteralPath $result } else { Write-Output '(keine Ergebnisdatei)' }

$log = Get-ChildItem (Join-Path $appdata 'logs') -Filter 'lumora-*.log' -ErrorAction SilentlyContinue |
    Sort-Object LastWriteTime | Select-Object -Last 1
if ($log) {
    $lines = Get-Content -LiteralPath $log.FullName
    Write-Output "`n== Log: $($log.FullName) ($($lines.Count) Zeilen) =="
    Write-Output '-- ERR/FTL/WRN --'
    $lines | Where-Object { $_ -match '\b(ERR|FTL|WRN)\]' } | Select-Object -First 40
    Write-Output '-- Rauchtest ✘ / FAIL --'
    $lines | Where-Object { $_ -match 'Rauchtest ✘|FAIL ' } | Select-Object -First 40
}
if (-not $NoShots) {
    Write-Output "`n== Screenshots =="
    Get-ChildItem -LiteralPath $shots -Filter *.png -ErrorAction SilentlyContinue | ForEach-Object { '{0,8} {1}' -f $_.Length, $_.Name }
}
# Portables Paket (ADR-0069): LUMORA_APPDATA hat Vorrang — neben der EXE darf kein Ordner Daten entstanden sein
$exeDir = Split-Path -Parent $exePath
if (Test-Path -LiteralPath (Join-Path $exeDir 'lumora.portable')) {
    Write-Output "`n== Portabler Modus =="
    if ($portableDataBefore -or -not (Test-Path -LiteralPath (Join-Path $exeDir 'Daten'))) {
        Write-Output 'lumora.portable im Paket — Rauchtest lief über LUMORA_APPDATA, kein neuer Ordner Daten'
    } else {
        Write-Output 'FEHLER: Ordner Daten neben der EXE angelegt, obwohl LUMORA_APPDATA gesetzt war'
        $exit = 'PORTABLE'
    }
}

Write-Output "`n== Prozesse =="
$left = Get-Process $processName -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $exePath }
if ($left) { Write-Output "HÄNGEND: $($left.Id)" } else { Write-Output 'kein hängender Prozess' }

exit $(if ($exit -eq 0) { 0 } else { 1 })
