using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Clippboard.Core;
using Clippboard.App.Platform;

internal static partial class Program
{
    private static async Task CheckCapturePrivacyAsync(string root)
    {
        var normalized = new AppSettings { ExcludedApps = [" KeePass.EXE ", "keepass", "chrome.exe", "C:\\bad.exe", ".exe"], PauseUntil = DateTimeOffset.UtcNow.AddDays(10) }.Normalize();
        Check(normalized.ExcludedApps.SequenceEqual(["KeePass", "chrome"]) && normalized.PauseUntil <= DateTimeOffset.UtcNow.AddDays(1),
            "executable exclusions normalize, deduplicate and reject paths; timed pause is bounded");
        Check(CapturePolicy.ValidateExclusions(["C:\\bad.exe"]) is not null && CapturePolicy.ValidateExclusions(["*.exe"]) is not null
            && CapturePolicy.ValidateExclusions([".exe"]) is not null, "invalid privacy rules return an explicit validation error");
        Check(CapturePolicy.ValidateExclusions(Enumerable.Range(0, 64).Select(i => new string('名', 120) + i).ToArray()) is not null,
            "Unicode exclusion lists stay within settings transport bounds");
        Check(!CapturePolicy.AllowsSource(normalized.ExcludedApps, "KEEPASS.exe")
            && CapturePolicy.AllowsSource(normalized.ExcludedApps, "notepad"), "exclusions match exact executable names case-insensitively");
        Check(!CapturePolicy.AllowsSource(normalized.ExcludedApps, null) && CapturePolicy.AllowsSource([], null),
            "unknown clipboard sources fail closed only when exclusions are enabled");
        Check(!CapturePolicy.AllowsSource(["chrome"], "chrome") && CapturePolicy.AllowsSource(["chrome"], "chromedriver"),
            "exclusion does not accidentally hide a similarly named executable");
        Check(new AppSettings { PauseUntil = DateTimeOffset.UtcNow.AddMinutes(5) }.IsCapturePaused
            && !new AppSettings { PauseUntil = DateTimeOffset.UtcNow.AddMinutes(-5) }.IsCapturePaused
            && new AppSettings { CapturePaused = true, PauseUntil = DateTimeOffset.UtcNow.AddMinutes(-5) }.IsCapturePaused,
            "timed pause expires while manual pause remains explicit");
        var settings = new SettingsStore(Path.Combine(root, "privacy-settings.json"));
        await settings.SaveAsync(normalized with { ClearOnLock = true });
        var restored = await settings.LoadAsync();
        Check(restored.ClearOnLock && restored.IsCapturePaused && restored.ExcludedApps.SequenceEqual(normalized.ExcludedApps),
            "privacy settings survive restart with timed pause and exclusions");

        var directory = Path.Combine(root, "interrupted-purge");
        var history = new HistoryStore(directory, new WindowsProtector()); await history.InitializeAsync();
        var clip = new Clip { Kind = ClipKind.Text, Text = "synthetic private fixture", Pinned = true };
        await history.AddAsync(clip);
        var metadata = Path.Combine(directory, clip.Id + ".meta"); var failed = false;
        using (var locked = new FileStream(metadata, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            try { await history.ClearAsync(); } catch (IOException) { failed = true; }
            var blocked = false;
            try { await history.AddAsync(new Clip { Kind = ClipKind.Text, Text = "cannot enter incomplete purge" }); } catch (IOException) { blocked = true; }
            Check(blocked && Directory.GetFiles(directory, "*.clip").Length == 0,
                "new capture cannot enter a partially failed purge and be silently deleted later");
        }
        Check(failed && File.Exists(Path.Combine(directory, "clear.pending")), "interrupted purge leaves a durable non-content retry marker");
        var reopened = new HistoryStore(directory, new WindowsProtector()); await reopened.InitializeAsync();
        Check((await reopened.ListAsync()).Count == 0 && Directory.GetFiles(directory).Length == 0,
            "restart completes interrupted deletion including starred metadata");

        using var cancellation = new CancellationTokenSource(); cancellation.Cancel(); var rejected = false;
        try { await reopened.AddAsync(clip, cancellation.Token); } catch (OperationCanceledException) { rejected = true; }
        Check(rejected && (await reopened.ListAsync()).Count == 0, "canceled capture cannot repopulate cleared history");
        var busyDirectory = Path.Combine(root, "queued-capture");
        using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
        var busy = new HistoryStore(busyDirectory, new BlockingProtector(entered, release)); await busy.InitializeAsync();
        var first = Task.Run(() => busy.AddAsync(clip));
        Check(entered.Wait(TimeSpan.FromSeconds(3)), "fixture owns storage writer before cancellation test");
        using var queuedCancellation = new CancellationTokenSource();
        var queuedClip = new Clip { Kind = ClipKind.Text, Text = "must not survive locking" };
        var queued = busy.AddAsync(queuedClip, queuedCancellation.Token); queuedCancellation.Cancel(); release.Set();
        await first; rejected = false;
        try { await queued; } catch (OperationCanceledException) { rejected = true; }
        await busy.ClearAsync();
        Check(rejected && (await busy.ListAsync()).Count == 0, "capture queued behind writer is canceled before purge and cannot resurrect data");
    }
    private sealed class BlockingProtector(ManualResetEventSlim entered, ManualResetEventSlim release) : IContentProtector
    {
        private int _calls;
        private readonly WindowsProtector _inner = new();
        public byte[] Protect(byte[] plaintext)
        {
            if (Interlocked.Increment(ref _calls) == 1)
            { entered.Set(); if (!release.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException(); }
            return _inner.Protect(plaintext);
        }
        public byte[] Unprotect(byte[] ciphertext) => _inner.Unprotect(ciphertext);
    }
}
