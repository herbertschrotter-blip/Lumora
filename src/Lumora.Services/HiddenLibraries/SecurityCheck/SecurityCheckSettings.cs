using Microsoft.Data.Sqlite;
using Lumora.Services.Database;

namespace Lumora.Services.HiddenLibraries.SecurityCheck;

/// <summary>
/// Einstellungen des Sicherheits-Checks (ADR-0064), Schlüssel <c>securitycheck.*</c> in der Settings-Tabelle des
/// <b>versteckten</b> Katalogs (im Container). Der Speicher wird nur in versteckten Sitzungen registriert — der normale
/// Katalog bekommt nie eine Zeile davon.
/// </summary>
/// <param name="Level">Gefahren-Stufe (Standard „Mitbenutzer“).</param>
/// <param name="ExitAfterLock">Lumora nach dem Zusperren ganz beenden (Standard aus).</param>
public sealed record SecurityCheckSettings(ThreatLevel Level = SecurityCheckRating.DefaultLevel, bool ExitAfterLock = false)
{
    public const string LevelKey = "securitycheck.level";
    public const string ExitAfterLockKey = "securitycheck.exitAfterLock";

    public static SecurityCheckSettings Default { get; } = new();
}

/// <summary>Lesen/Speichern der Einstellungen des Sicherheits-Checks.</summary>
public interface ISecurityCheckSettingsStore
{
    Task<SecurityCheckSettings> LoadAsync(CancellationToken ct = default);

    Task SaveAsync(SecurityCheckSettings settings, CancellationToken ct = default);
}

/// <inheritdoc />
public sealed class SecurityCheckSettingsStore(ILibraryContextFactory factory) : ISecurityCheckSettingsStore
{
    public Task<SecurityCheckSettings> LoadAsync(CancellationToken ct = default)
        => Task.Run(() =>
        {
            using var connection = factory.OpenSqliteConnection();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT Key, Value FROM Settings WHERE Key IN ($level, $exit);";
            cmd.Parameters.AddWithValue("$level", SecurityCheckSettings.LevelKey);
            cmd.Parameters.AddWithValue("$exit", SecurityCheckSettings.ExitAfterLockKey);
            var values = new Dictionary<string, string>(StringComparer.Ordinal);
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                values[reader.GetString(0)] = reader.IsDBNull(1) ? string.Empty : reader.GetString(1);
            }

            return new SecurityCheckSettings(
                SecurityCheckRating.ParseLevel(values.GetValueOrDefault(SecurityCheckSettings.LevelKey)),
                values.TryGetValue(SecurityCheckSettings.ExitAfterLockKey, out var exit) && bool.TryParse(exit, out var b) && b);
        }, ct);

    public async Task SaveAsync(SecurityCheckSettings settings, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var level = Enum.IsDefined(settings.Level) ? settings.Level : SecurityCheckRating.DefaultLevel;
        await Task.Run(async () =>
        {
            using var connection = factory.OpenSqliteConnection();
            using var tx = await factory.BusyRetry.BeginTransactionAsync(connection, ct).ConfigureAwait(false);
            Upsert(connection, tx, SecurityCheckSettings.LevelKey, ((int)level).ToString(System.Globalization.CultureInfo.InvariantCulture));
            Upsert(connection, tx, SecurityCheckSettings.ExitAfterLockKey, settings.ExitAfterLock ? "true" : "false");
            tx.Commit();
        }, ct).ConfigureAwait(false);
    }

    private static void Upsert(SqliteConnection connection, SqliteTransaction tx, string key, string value)
    {
        using var cmd = connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "INSERT INTO Settings (Key, Value) VALUES ($k, $v) ON CONFLICT(Key) DO UPDATE SET Value = excluded.Value;";
        cmd.Parameters.AddWithValue("$k", key);
        cmd.Parameters.AddWithValue("$v", value);
        cmd.ExecuteNonQuery();
    }
}
