using System.Windows;

namespace VoiceJournal.App.Services;

/// <summary>
/// Command-line options, for verification only (the settings screen comes later):
/// <c>--workspace &lt;folder&gt;</c> records into another workspace (acceptance tests must not touch the user's library),
/// <c>--theme light|dark</c> forces a theme (to check both).
/// </summary>
/// <param name="TextScale">--text-scale 100..225: overrides the Windows text size (for checking AT-25 without changing the user's setting).</param>
public sealed record StartupOptions(string? WorkspaceRoot, ThemeMode? Theme, int? TextScale = null)
{
    public static StartupOptions Parse(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);
        string? workspace = null;
        ThemeMode? theme = null;
        int? textScale = null;
        for (var i = 0; i < args.Count; i++)
        {
            switch (args[i])
            {
                case "--workspace" when i + 1 < args.Count:
                    workspace = args[++i];
                    break;
                case "--theme" when i + 1 < args.Count:
                    theme = args[++i].ToUpperInvariant() switch
                    {
                        "LIGHT" => ThemeMode.Light,
                        "DARK" => ThemeMode.Dark,
                        _ => null,
                    };
                    break;
                case "--text-scale" when i + 1 < args.Count:
                    textScale = int.TryParse(args[++i], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var percent)
                        && percent is >= TextScaling.MinPercent and <= TextScaling.MaxPercent ? percent : null;
                    break;
            }
        }
        return new StartupOptions(workspace, theme, textScale);
    }
}
