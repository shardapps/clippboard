using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.Tasks;
using Clippboard.App.Platform;
using Clippboard.Core;

internal static class UpgradeChecks
{
    private sealed record Snapshot(string[] EntryIds, string[] Files, string[] Hashes, string SettingsHash);
    private static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
    internal static async Task<int> RunAsync(string executable, string directory, bool create)
    {
        if (Environment.GetEnvironmentVariable("GITHUB_ACTIONS") != "true") throw new InvalidOperationException("Upgrade checks require an isolated runner.");
        directory = Path.GetFullPath(directory);
        var history = Path.Combine(directory, "history"); var settings = Path.Combine(directory, "settings.json");
        var snapshotFile = Path.Combine(directory, "expected.json");
        if (create)
        {
            if (Directory.Exists(directory)) throw new IOException("Upgrade fixture must be fresh.");
            Directory.CreateDirectory(directory);
            await new SettingsStore(settings).SaveAsync(new AppSettings { Theme = AppTheme.Ocean, NoteWidth = 340, RetentionDays = 17, CapturePaused = true });
            var store = new HistoryStore(history, new WindowsProtector()); await store.InitializeAsync();
            var original = Path.Combine(directory, "原始 file.txt"); await File.WriteAllTextAsync(original, "Synthetic original");
            Clip[] clips = [new() { Kind = ClipKind.Text, Text = "Upgrade fixture — اردو — 😀", Pinned = true },
                new() { Kind = ClipKind.Files, FilePaths = [original] },
                new() { Kind = ClipKind.Image, ImagePng = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=") }];
            // The executable launched first is the pinned old baseline. Seed its
            // schema, not the current writer's enhanced image/snippet format.
            await SeedLegacyFixtureAsync(history, clips);
            await store.InitializeAsync();
            var files = Directory.GetFiles(history).Select(Path.GetFileName).Order().ToArray();
            await File.WriteAllTextAsync(snapshotFile, JsonSerializer.Serialize(new Snapshot(clips.Select(x => x.Id).ToArray(), files!, files.Select(x => Hash(Path.Combine(history, x!))).ToArray(), Hash(settings))));
        }
        var expected = JsonSerializer.Deserialize<Snapshot>(await File.ReadAllTextAsync(snapshotFile)) ?? throw new IOException("Missing snapshot.");
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var start = new ProcessStartInfo(Path.GetFullPath(executable)) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
        start.ArgumentList.Add("--dev"); start.ArgumentList.Add("--background");
        start.Environment["CLIPPBOARD_DEV_DATA"] = directory; start.Environment["CLIPPBOARD_SESSION_DIAGNOSTICS"] = "1";
        using var process = Process.Start(start) ?? throw new IOException("Installed listener did not start.");
        process.ErrorDataReceived += (_, line) => { if (line.Data == "Clipboard listener ready") ready.TrySetResult(); };
        process.BeginErrorReadLine();
        try
        {
            await ready.Task.WaitAsync(TimeSpan.FromSeconds(15));
            await Task.Delay(500);
            if (process.HasExited) throw new IOException("Installed listener exited during upgrade check.");
        }
        finally { if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); } }
        if (Hash(settings) != expected.SettingsHash) throw new IOException("User preferences changed during upgrade.");
        var actualFiles = Directory.GetFiles(history).Select(Path.GetFileName).Order().ToArray();
        if (!actualFiles.SequenceEqual(expected.Files)) throw new IOException("Encrypted history entries changed.");
        for (var i = 0; i < expected.Files.Length; i++) if (Hash(Path.Combine(history, expected.Files[i])) != expected.Hashes[i]) throw new IOException("Encrypted entry bytes changed.");
        var reopened = new HistoryStore(history, new WindowsProtector()); await reopened.InitializeAsync();
        foreach (var id in expected.EntryIds) if (await reopened.GetAsync(id) is null) throw new IOException("Upgrade lost a persisted payload.");
        var report = new { Version = FileVersionInfo.GetVersionInfo(Path.GetFullPath(executable)).ProductVersion, Entries = expected.EntryIds.Length, PreferencesPreserved = true, EncryptedHistoryPreserved = true, ListenerStarted = true, CapturePaused = true };
        await File.WriteAllTextAsync(Path.Combine(Path.GetDirectoryName(directory)!, create ? "upgrade-before.json" : "upgrade-after.json"), JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine("PASS: installed listener preserves encrypted text/image/file fixtures, star and preferences across version upgrade.");
        return 0;
    }
    internal static async Task SeedLegacyFixtureAsync(string history, System.Collections.Generic.IReadOnlyList<Clip> clips)
    {
        Directory.CreateDirectory(history);
        foreach (var clip in clips)
        {
            if (clip.Id.Length != 32 || !clip.Id.All(Uri.IsHexDigit)) throw new ArgumentException("Invalid fixture identity.");
            var legacy = new { clip.Id, clip.Kind, clip.CopiedAt, clip.Pinned, clip.Text, clip.ImagePng, clip.FilePaths };
            var plaintext = JsonSerializer.SerializeToUtf8Bytes(legacy);
            try { await File.WriteAllBytesAsync(Path.Combine(history, clip.Id + ".clip"), new WindowsProtector().Protect(plaintext)); }
            finally { CryptographicOperations.ZeroMemory(plaintext); }
        }
    }
}
