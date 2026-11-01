using System.Windows;

namespace VoiceJournal.App.Services;

/// <summary>
/// Command-line options, for verification only (the settings screen comes later):
/// <c>--workspace &lt;folder&gt;</c> records into another workspace (acceptance tests must not touch the user's library),
/// <c>--theme light|dark</c> forces a theme (to check both).
/// </summary>
public sealed record StartupOptions(string? WorkspaceRoot, ThemeMode? Theme)
{
    public static StartupOptions Parse(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);
        string? workspace = null;
        ThemeMode? theme = null;
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
            }
        }
        return new StartupOptions(workspace, theme);
    }
}
