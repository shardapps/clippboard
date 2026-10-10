using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Reflection;

namespace Clippboard.App.Services;

internal static class Feedback
{
    internal static string Version => Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "development";
    internal static string SupportInfo() => $"Clippboard {Version}\nWindows {Environment.OSVersion.Version}\nArchitecture: {System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture}\n.NET {Environment.Version}\nNo clipboard contents, file paths or device identifiers included.";
    internal static Uri Link(string action) => new(action switch
    {
        "bug" => "https://github.com/shardapps/clippboard/issues/new?template=bug_report.yml&version=" + Uri.EscapeDataString(Version),
        "feature" => "https://github.com/shardapps/clippboard/issues/new?template=feature_request.yml",
        _ => "https://github.com/shardapps/clippboard/discussions"
    });
    internal static bool Open(string action)
    {
        try { Process.Start(new ProcessStartInfo(Link(action).AbsoluteUri) { UseShellExecute = true }); return true; }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException) { return false; }
    }
}
