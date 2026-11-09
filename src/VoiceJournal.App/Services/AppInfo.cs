using System.Reflection;

namespace VoiceJournal.App.Services;

/// <summary>
/// The app's version (Directory.Build.props) and, when the build knew it, the commit it was built from
/// (the .NET SDK appends it to the informational version as "+&lt;sha&gt;").
/// </summary>
internal static class AppInfo
{
    /// <summary>For example "1.0.0-beta.1+7b7c370…": goes into the diagnostics report as is.</summary>
    public static string InformationalVersion { get; } =
        typeof(AppInfo).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0";

    /// <summary>For example "1.0.0-beta.1".</summary>
    public static string Version => Split(InformationalVersion).Version;

    /// <summary>For example "1.0.0-beta.1（7b7c370）", or just the version when the commit is unknown.</summary>
    public static string DisplayText => Format(InformationalVersion);

    internal static string Format(string informationalVersion)
    {
        var (version, commit) = Split(informationalVersion);
        return commit is null ? version : $"{version}（{commit[..Math.Min(7, commit.Length)]}）";
    }

    private static (string Version, string? Commit) Split(string informationalVersion)
    {
        var plus = informationalVersion.IndexOf('+', StringComparison.Ordinal);
        return plus < 0 ? (informationalVersion, null) : (informationalVersion[..plus], plus + 1 < informationalVersion.Length ? informationalVersion[(plus + 1)..] : null);
    }
}
