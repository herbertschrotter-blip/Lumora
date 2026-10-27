using System.Globalization;
using Lumora.Services.Database;

namespace Lumora.Services.AiEdit;

/// <summary>
/// Einstellungen der KI-Bearbeitung (ADR-0076), Schlüssel <c>aiedit.*</c> in der Settings-Tabelle des <b>versteckten</b>
/// Katalogs (im Container). Der Speicher wird nur in versteckten Sitzungen registriert.
/// </summary>
/// <param name="IdleMinutes">Nach so vielen Minuten ohne Auftrag beenden (0 = nie; Standard 10, Herbert 2026-10-27).</param>
public sealed record AiEditSettings(int IdleMinutes = AiEditSettings.DefaultIdleMinutes)
{
    public const int DefaultIdleMinutes = 10;
    public const int MaxIdleMinutes = 240;
    public const string IdleMinutesKey = "aiedit.idleMinutes";

    public static AiEditSettings Default { get; } = new();

    public static int ClampIdleMinutes(int minutes) => Math.Clamp(minutes, 0, MaxIdleMinutes);
}

/// <summary>Lesen/Speichern der Einstellungen der KI-Bearbeitung.</summary>
public interface IAiEditSettingsStore
{
    Task<AiEditSettings> LoadAsync(CancellationToken ct = default);

    Task SaveAsync(AiEditSettings settings, CancellationToken ct = default);
}

/// <inheritdoc />
public sealed class AiEditSettingsStore(ILibraryContextFactory factory) : IAiEditSettingsStore
{
    public Task<AiEditSettings> LoadAsync(CancellationToken ct = default)
        => Task.Run(() =>
        {
            using var connection = factory.OpenSqliteConnection();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT Value FROM Settings WHERE Key = $key;";
            cmd.Parameters.AddWithValue("$key", AiEditSettings.IdleMinutesKey);
            var value = cmd.ExecuteScalar() as string;
            return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var minutes)
                ? new AiEditSettings(AiEditSettings.ClampIdleMinutes(minutes))
                : AiEditSettings.Default;
        }, ct);

    public async Task SaveAsync(AiEditSettings settings, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var minutes = AiEditSettings.ClampIdleMinutes(settings.IdleMinutes);
        await Task.Run(async () =>
        {
            using var connection = factory.OpenSqliteConnection();
            using var tx = await factory.BusyRetry.BeginTransactionAsync(connection, ct).ConfigureAwait(false);
            using var cmd = connection.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = "INSERT INTO Settings (Key, Value) VALUES ($k, $v) ON CONFLICT(Key) DO UPDATE SET Value = excluded.Value;";
            cmd.Parameters.AddWithValue("$k", AiEditSettings.IdleMinutesKey);
            cmd.Parameters.AddWithValue("$v", minutes.ToString(CultureInfo.InvariantCulture));
            cmd.ExecuteNonQuery();
            tx.Commit();
        }, ct).ConfigureAwait(false);
    }
}
