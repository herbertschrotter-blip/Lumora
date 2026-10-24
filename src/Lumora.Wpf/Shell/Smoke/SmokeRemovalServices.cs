using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Lumora.Removal;
using Lumora.Services.Operations;

namespace Lumora.Shell.Smoke;

/// <summary>
/// Rauchtest-Papierkorb (ADR-0065): reicht alles an den echten Papierkorb weiter — außer im <see cref="TestMode"/> des Schritts
/// „Ordner löschen / Fehlende entfernen“. Dann landen Ordner (und Dateien) in einem Ordner im Rauchtest-AppData und kommen von
/// dort zurück; so bleibt im echten Windows-Papierkorb nie etwas aus diesem Schritt liegen, auch nicht nach einem Abbruch.
/// </summary>
internal sealed class SmokeRecycleBin : IRecycleBin
{
    private readonly IRecycleBin _inner;
    private readonly string _binDir;
    private readonly Dictionary<string, string> _parked = new(StringComparer.OrdinalIgnoreCase);

    public SmokeRecycleBin(IRecycleBin inner, string binDir)
    {
        _inner = inner;
        _binDir = binDir;
    }

    /// <summary>Nur während des eigenen Schritts an.</summary>
    public bool TestMode { get; set; }

    /// <summary>Ablage des Test-Papierkorbs (im Rauchtest-AppData).</summary>
    public string BinDir => _binDir;

    public int ParkedCount => _parked.Count;

    public void MoveToRecycleBin(string filePath)
    {
        if (!TestMode)
        {
            _inner.MoveToRecycleBin(filePath);
            return;
        }

        File.Move(filePath, Park(filePath));
    }

    public void MoveDirectoryToRecycleBin(string directoryPath)
    {
        if (!TestMode)
        {
            _inner.MoveDirectoryToRecycleBin(directoryPath);
            return;
        }

        Directory.Move(directoryPath, Park(directoryPath));
    }

    public void RestoreFromRecycleBin(string originalPath, DateTime deletedAfterUtc)
    {
        if (!TestMode)
        {
            _inner.RestoreFromRecycleBin(originalPath, deletedAfterUtc);
            return;
        }

        File.Move(Unpark(originalPath), originalPath);
    }

    public string? GetUnavailableReasonFor(string path) => TestMode ? null : _inner.GetUnavailableReasonFor(path);

    public bool ContainsDirectory(string originalPath, DateTime deletedAfterUtc)
        => TestMode ? _parked.ContainsKey(Path.TrimEndingDirectorySeparator(Path.GetFullPath(originalPath))) : _inner.ContainsDirectory(originalPath, deletedAfterUtc);

    public void RestoreDirectoryFromRecycleBin(string originalPath, DateTime deletedAfterUtc)
    {
        if (!TestMode)
        {
            _inner.RestoreDirectoryFromRecycleBin(originalPath, deletedAfterUtc);
            return;
        }

        Directory.Move(Unpark(originalPath), originalPath);
    }

    /// <summary>Test-Papierkorb leeren (nur der eigene Ordner im Rauchtest-AppData).</summary>
    public void Clear()
    {
        _parked.Clear();
        if (Directory.Exists(_binDir))
        {
            Directory.Delete(_binDir, recursive: true);
        }
    }

    private string Park(string path)
    {
        Directory.CreateDirectory(_binDir);
        var target = Path.Combine(_binDir, Guid.NewGuid().ToString("N"));
        _parked[Path.TrimEndingDirectorySeparator(Path.GetFullPath(path))] = target;
        return target;
    }

    private string Unpark(string path)
        => _parked.Remove(Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)), out var parked)
            ? parked
            : throw new FileNotFoundException("Nicht im Test-Papierkorb.", path);
}

/// <summary>Rauchtest-Bestätigungen für „Ordner in den Papierkorb“ / „Fehlende entfernen“: automatisch „Ja“, Text wird gemerkt und geloggt.</summary>
internal sealed class SmokeRemovalDialogs : IRemovalDialogs
{
    private readonly ILogger<SmokeRemovalDialogs> _logger;

    public SmokeRemovalDialogs(ILogger<SmokeRemovalDialogs> logger) => _logger = logger;

    public List<(string Title, string Message)> Requests { get; } = [];

    public Task<bool> ConfirmAsync(string title, string message, string confirmText)
    {
        Requests.Add((title, message));
        _logger.LogInformation("Rauchtest-Dialog „{Title}“ → automatisch „{Button}“", title, confirmText);
        return Task.FromResult(true);
    }
}
