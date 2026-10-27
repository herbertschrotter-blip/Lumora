using System.Globalization;

namespace Lumora.AppConnect;

/// <summary>
/// Start ohne Fenster (Abschnitt 9): <c>Lumora.exe --background --started-by &lt;appId&gt;:&lt;pid&gt;</c>. Erlaubt nur, wenn die
/// Verbindungen an sind, der Benutzer den Hintergrundstart erlaubt und Lumora der startenden App <c>background.start</c>
/// gegeben hat; sonst beendet sich Lumora sofort wieder, ohne etwas zu öffnen.
/// </summary>
public sealed record BackgroundLaunch(string StartedByAppId, int StartedByPid)
{
    public const string StartedByArg = "--started-by";

    /// <summary>true = die Befehlszeile verlangt einen Start ohne Fenster (mit oder ohne gültiges <c>--started-by</c>).</summary>
    public static bool IsRequested(IReadOnlyList<string> args)
        => args.Any(a => string.Equals(a, AppConnectContract.BackgroundArg, StringComparison.OrdinalIgnoreCase));

    /// <summary>Der Hintergrundstart mit seinem Abfrager; null ohne <c>--background</c> oder ohne gültiges <c>--started-by</c>.</summary>
    public static BackgroundLaunch? Parse(IReadOnlyList<string> args)
    {
        if (!IsRequested(args))
        {
            return null;
        }

        for (var i = 0; i < args.Count - 1; i++)
        {
            if (!string.Equals(args[i], StartedByArg, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var value = args[i + 1];
            var colon = value.LastIndexOf(':');
            if (colon > 0
                && int.TryParse(value.AsSpan(colon + 1), NumberStyles.None, CultureInfo.InvariantCulture, out var pid) && pid > 0
                && value[..colon] is var appId && appId.All(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c is '-' or '_'))
            {
                return new BackgroundLaunch(appId, pid);
            }
        }

        return null;
    }

    /// <summary>Befehlszeile, mit der Lumora eine andere App im Hintergrund startet.</summary>
    public static string ArgumentsFor(string backgroundArg, string ownAppId, int ownPid)
        => $"{backgroundArg} {StartedByArg} {ownAppId}:{ownPid.ToString(CultureInfo.InvariantCulture)}";

    /// <summary>Warum der Hintergrundstart nicht erlaubt ist; null = erlaubt.</summary>
    public string? Refusal(AppConnectSettings settings, IAppConnectPeers peers)
        => !settings.Enabled ? "Verbindungen sind aus"
            : !settings.AllowBackgroundStart ? "Hintergrundstart ist aus"
            : !peers.GrantedTo(StartedByAppId).Contains(Rights.BackgroundStart, StringComparer.Ordinal) ? "die App hat kein Recht background.start"
            : null;
}
