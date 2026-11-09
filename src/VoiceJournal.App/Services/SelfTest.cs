using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using VoiceJournal.Infrastructure.Llm;

namespace VoiceJournal.App.Services;

/// <summary>
/// <c>--self-test &lt;file&gt;</c>: checks an installed copy without a window or a workspace (scripts/New-Installer.ps1).
/// The analysis schema is compiled at run time from the reference assemblies the build ships in <c>refs\</c>
/// (PreserveCompilationContext); a self-contained publish that lost them would fail only at the first record.
/// Writes the version, the folder and the result as JSON; exit code 0 when everything passed.
/// </summary>
internal static class SelfTest
{
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public static int Run(string reportPath)
    {
        var report = new JsonObject
        {
            ["app_version"] = AppInfo.InformationalVersion,
            ["app_folder"] = AppContext.BaseDirectory,
            ["dotnet"] = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
        };
        var watch = Stopwatch.StartNew();
        bool ok;
        try
        {
            // An empty object misses every required property: errors prove the schema compiled and validates.
            var errors = new AnalysisSchemaValidator().Validate("{}");
            ok = errors.Count > 0;
            report["schema"] = ok ? "ok" : "an empty object passed the schema";
        }
#pragma warning disable CA1031 // any failure here is the result being reported
        catch (Exception ex)
#pragma warning restore CA1031
        {
            ok = false;
            report["schema"] = $"{ex.GetType().FullName}: {ex.Message}";
        }
        report["schema_ms"] = watch.ElapsedMilliseconds;
        report["ok"] = ok;
        File.WriteAllText(reportPath, report.ToJsonString(Indented));
        return ok ? 0 : 1;
    }
}
