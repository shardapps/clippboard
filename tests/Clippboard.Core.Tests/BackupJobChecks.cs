using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Automation;
using System.Windows.Threading;
using Clippboard.Core;
using Clippboard.App.Platform;
using Clippboard.App.Services;
using Clippboard.App.Views;

internal static partial class Program
{
    private static async Task CheckBackupJobsAsync(string root)
    {
        const string passphrase = "Synthetic backup passphrase 🧵";
        var history = new HistoryStore(Path.Combine(root, "backup-job-source"), new WindowsProtector()); await history.InitializeAsync();
        var clip = new Clip { Kind = ClipKind.Text, Text = "Synthetic job source — private words" }; await history.AddAsync(clip);
        using var jobs = new BackupJobs(history, () => { });
        var path = Path.Combine(root, "job-export.clippboard"); var id = Guid.NewGuid().ToString("N"); var password = Encoding.UTF8.GetBytes(passphrase);
        jobs.Start(id, false, path, password, FileActions.DestinationState(path), CancellationToken.None); CryptographicOperations.ZeroMemory(password);
        Check(jobs.HoldsCapture, "background export holds capture while its bounded worker is active");
        await jobs.Completion.WaitAsync(TimeSpan.FromSeconds(30));
        Check(jobs.Snapshot(id)?.Phase == BackupPhase.Completed && !jobs.HoldsCapture && File.Exists(path), "export verifies and publishes a portable encrypted file then releases capture");
        var count = 0; password = Encoding.UTF8.GetBytes(passphrase);
        await using (var input = File.OpenRead(path)) await PortableArchive.ReadAsync(input, password, item => { count++; Check(item.Text == clip.Text, "worker-owned password survives caller-buffer wiping and decrypts exact text"); return Task.CompletedTask; });
        Check(count == 1 && Directory.GetFiles(root, ".clippboard-backup-*.tmp").Length == 0, "verified export removes temporary ciphertext");
        var expected = FileActions.DestinationState(path); await File.WriteAllTextAsync(path, "Changed external destination"); id = Guid.NewGuid().ToString("N");
        jobs.Start(id, false, path, password, expected, CancellationToken.None); await jobs.Completion.WaitAsync(TimeSpan.FromSeconds(30));
        Check(jobs.Snapshot(id)?.Phase == BackupPhase.Failed && await File.ReadAllTextAsync(path) == "Changed external destination", "stale export selection cannot overwrite a changed file");
        id = Guid.NewGuid().ToString("N"); jobs.Start(id, false, path, password, FileActions.DestinationState(path), CancellationToken.None); await jobs.Completion.WaitAsync(TimeSpan.FromSeconds(30));
        Check(jobs.Snapshot(id)?.Phase == BackupPhase.Completed, "replacement export completes before import: " + jobs.Snapshot(id));
        using (var heldDestination = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var lockedId = Guid.NewGuid().ToString("N");
            jobs.Start(lockedId, false, path, password, FileActions.DestinationState(path), CancellationToken.None);
            await jobs.Completion.WaitAsync(TimeSpan.FromSeconds(30));
            Check(jobs.Snapshot(lockedId)?.Phase == BackupPhase.Failed, "a destination held by another application is preserved rather than replaced");
        }
        var targetPath = Path.Combine(root, "backup-job-target"); var target = new HistoryStore(targetPath, new WindowsProtector()); await target.InitializeAsync(); var changed = 0;
        using var imports = new BackupJobs(target, () => changed++); id = Guid.NewGuid().ToString("N");
        imports.Start(id, true, path, password, null, CancellationToken.None); await imports.Completion.WaitAsync(TimeSpan.FromSeconds(30));
        Check(imports.Snapshot(id) is { Phase: BackupPhase.Ready, Preview.NewNotes: 1 } && imports.HoldsCapture && (await target.ListAsync()).Count == 0,
            "inspection pauses capture and requires explicit commit before history changes");
        var rejected = false;
        try { imports.Start(Guid.NewGuid().ToString("N"), true, path, password, null, CancellationToken.None); } catch (InvalidOperationException) { rejected = true; }
        Check(rejected, "one transfer or reviewed import can exist at a time");
        imports.Cancel(id); await imports.Completion.WaitAsync(TimeSpan.FromSeconds(30));
        Check(imports.Snapshot(id)?.Phase == BackupPhase.Canceled && !imports.HoldsCapture && (await target.ListAsync()).Count == 0
            && Directory.GetDirectories(targetPath, "restore-*").Length == 0, "canceling ready preview cleans staging without writing history");
        id = Guid.NewGuid().ToString("N"); imports.Start(id, true, path, password, null, CancellationToken.None); await imports.Completion.WaitAsync(TimeSpan.FromSeconds(30));
        imports.Commit(id); await imports.Completion.WaitAsync(TimeSpan.FromSeconds(30));
        Check(imports.Snapshot(id)?.Phase == BackupPhase.Completed && changed == 1 && (await target.GetAsync(clip.Id))!.Text == clip.Text,
            "explicit background restore updates history and emits one refresh notification");
        id = Guid.NewGuid().ToString("N"); imports.Start(id, true, path, Encoding.UTF8.GetBytes("Wrong synthetic passphrase"), null, CancellationToken.None); await imports.Completion.WaitAsync(TimeSpan.FromSeconds(30));
        var failure = imports.Snapshot(id);
        Check(failure?.Phase == BackupPhase.Failed && !failure.Message!.Contains(root, StringComparison.OrdinalIgnoreCase)
            && !failure.Message.Contains(clip.Text!, StringComparison.Ordinal) && (await target.ListAsync()).Count == 1, "failed transfer notices exclude personal paths, content and password details");
        id = Guid.NewGuid().ToString("N"); using var locked = new CancellationTokenSource();
        imports.Start(id, true, path, password, null, locked.Token); await imports.Completion.WaitAsync(TimeSpan.FromSeconds(30)); locked.Cancel();
        _ = imports.Snapshot(id); await imports.Completion.WaitAsync(TimeSpan.FromSeconds(30));
        Check(imports.Snapshot(id)?.Phase == BackupPhase.Canceled && !imports.HoldsCapture, "session lifetime cancellation also cleans a ready restore preview");
        CryptographicOperations.ZeroMemory(password);
        await CheckDestinationProbeAsync(); await CheckTransferTransportAsync(); CheckBackupVisuals();
    }
    private static async Task CheckTransferTransportAsync()
    {
        byte[]? received = null;
        using var server = new OverlayServer(request =>
        {
            received = request.Secret;
            if (request.Command == "transfer-import") throw new IOException("Synthetic request failure");
            return Task.FromResult(new OverlayReply(Transfer: new(request.Id!, BackupPhase.Exporting)));
        });
        var running = server.RunAsync();
        using var client = new OverlayClient(server.Name);
        var id = Guid.NewGuid().ToString("N"); var secret = Encoding.UTF8.GetBytes("Synthetic pipe passphrase 🧵");
        var state = await client.BeginTransferAsync(id, false, @"C:\synthetic\backup.clippboard", secret, new(false, 0, 0), CancellationToken.None);
        Check(state.Id == id && state.Phase == BackupPhase.Exporting && received!.All(value => value == 0) && secret.Any(value => value != 0),
            "current-user IPC wipes the server's decoded password while preserving caller ownership and typed transfer state");
        var rejected = false;
        try { await client.BeginTransferAsync(id, true, @"C:\synthetic\backup.clippboard", secret, null, CancellationToken.None); } catch (IOException) { rejected = true; }
        Check(rejected && received!.All(value => value == 0), "handler failure also clears the decoded IPC password");
        CryptographicOperations.ZeroMemory(secret); server.Dispose(); await running.WaitAsync(TimeSpan.FromSeconds(3));
    }
    private static async Task CheckDestinationProbeAsync()
    {
        using var release = new ManualResetEventSlim(); var calls = 0;
        var probe = new DestinationProbe(_ => { Interlocked.Increment(ref calls); release.Wait(); return new(false, 0, 0); }, TimeSpan.FromMilliseconds(50));
        var timedOut = false;
        try { await probe.ReadAsync(@"C:\synthetic\slow.clippboard", CancellationToken.None); } catch (FileActionException) { timedOut = true; }
        var blocked = false;
        try { await probe.ReadAsync(@"C:\synthetic\other.clippboard", CancellationToken.None); } catch (FileActionException) { blocked = true; }
        Check(timedOut && blocked && calls == 1, "slow destination check has a caller deadline and retries cannot create more filesystem workers");
        release.Set(); var watch = System.Diagnostics.Stopwatch.StartNew(); var recovered = false;
        while (!recovered && watch.Elapsed < TimeSpan.FromSeconds(3))
        { try { await probe.ReadAsync(@"C:\synthetic\available.clippboard", CancellationToken.None); recovered = true; } catch (FileActionException) { await Task.Delay(10); } }
        Check(recovered && calls == 2, "destination worker slot recovers once the original operating-system call finishes");
    }
    private sealed class FixtureTransfer : IHistoryTransfer
    {
        internal int Starts, Commits, Polls; internal string? Id; internal byte[]? Secret;
        internal BackupState? Current; internal TaskCompletionSource<BackupState>? Pending; internal string? Canceled;
        public Task<BackupState> BeginTransferAsync(string id, bool importing, string path, byte[] password, FileActions.DestinationRevision? expected, CancellationToken cancellation)
        {
            Starts++; Id = id; Secret = password;
            Current = new(id, importing ? BackupPhase.Ready : BackupPhase.Exporting, Preview: importing ? new(2, 1, 0, 1234) : null);
            return Pending?.Task ?? Task.FromResult(Current);
        }
        public Task<BackupState?> TransferStateAsync(string id, CancellationToken cancellation) { Polls++; return Task.FromResult(Current); }
        public Task<BackupState> CommitTransferAsync(string id, CancellationToken cancellation) { Commits++; Current = new(id, BackupPhase.Completed, Message: "Restored synthetic notes"); return Task.FromResult(Current); }
        public Task CancelTransferAsync(string id) { Canceled = id; return Task.CompletedTask; }
    }
    private static void CheckBackupVisuals()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher)); Window? window = null;
            try
            {
                var transfer = new FixtureTransfer();
                static Task<BackupSelection?> Choose(CancellationToken _) => Task.FromResult<BackupSelection?>(new(@"C:\synthetic\backup.clippboard", new(false, 0, 0)));
                var view = new BackupView(false, transfer, Choose, false);
                window = new Window { Content = view, Width = 800, Height = 650, Left = -20000, Top = -20000, ShowActivated = false, ShowInTaskbar = false }; window.Show(); window.UpdateLayout();
                Pump(view.BeginAsync()); Check(transfer.Starts == 0, "backup screen requires a chosen destination before starting");
                Pump(view.ChooseAsync()); var boxes = Descendants<PasswordBox>(view).ToArray();
                boxes[0].Password = "Synthetic passphrase"; boxes[1].Password = "Different passphrase"; Pump(view.BeginAsync());
                Check(transfer.Starts == 0 && Descendants<Button>(view).Single(button => button.Content as string == "Save encrypted backup").IsEnabled, "mismatched passwords cannot start export and leave retry available");
                boxes[1].Password = "Synthetic passphrase"; Pump(view.BeginAsync());
                Check(transfer.Starts == 1 && transfer.Secret!.All(value => value == 0) && boxes.All(box => box.SecurePassword.Length == 0), "submitted password buffers are wiped and password fields clear after starting");
                transfer.Current = new(transfer.Id!, BackupPhase.Completed, Message: "Verified synthetic backup"); Pump(view.PollAsync());
                Check(view.State?.Phase == BackupPhase.Completed, "backup progress reflects actual terminal job state");
                var imports = new FixtureTransfer(); var importView = new BackupView(true, imports, Choose, false); window.Content = importView; window.UpdateLayout(); Pump(importView.ChooseAsync());
                Descendants<PasswordBox>(importView).Single().Password = "Synthetic passphrase"; Pump(importView.BeginAsync());
                Check(importView.State?.Phase == BackupPhase.Ready && imports.Commits == 0, "import review shows counts without automatically restoring");
                Pump(importView.CommitAsync()); Check(imports.Commits == 1 && importView.State?.Phase == BackupPhase.Completed, "only explicit restore commits the reviewed import");
                var delayed = new FixtureTransfer { Pending = new(TaskCreationOptions.RunContinuationsAsynchronously) }; var detached = new BackupView(true, delayed, Choose, false);
                window.Content = detached; window.UpdateLayout(); Pump(detached.ChooseAsync()); Descendants<PasswordBox>(detached).Single().Password = "Synthetic passphrase";
                var pending = detached.BeginAsync(); window.Content = null; Pump(Task.Delay(30));
                Check(delayed.Canceled == delayed.Id && delayed.Id is not null, "detaching during a start request cancels its preassigned job identity");
                delayed.Pending.SetResult(new(delayed.Id!, BackupPhase.Ready, Preview: new(2, 0, 0, 123))); Pump(pending);
                Check(delayed.Secret!.All(value => value == 0) && detached.State is null, "late start reply cannot revive a detached transfer panel or retain its secret");
            }
            catch (Exception ex) { failure = ex; }
            finally { window?.Close(); }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); thread.Join(); if (failure is not null) throw failure;
    }
}
