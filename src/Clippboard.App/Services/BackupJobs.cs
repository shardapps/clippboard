using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Clippboard.Core;

namespace Clippboard.App.Services;

internal enum BackupPhase { Exporting, Verifying, Inspecting, Ready, Restoring, Canceling, Completed, Canceled, Failed }
internal sealed record BackupState(string Id, BackupPhase Phase, int Done = 0, int Total = 0, string? Message = null, RestorePreview? Preview = null, string? FailureCode = null);
internal interface IHistoryTransfer
{
    Task<BackupState> BeginTransferAsync(string id, bool importing, string path, byte[] password, FileActions.DestinationRevision? expected, CancellationToken cancellation);
    Task<BackupState?> TransferStateAsync(string id, CancellationToken cancellation);
    Task<BackupState> CommitTransferAsync(string id, CancellationToken cancellation);
    Task CancelTransferAsync(string id);
}
internal sealed class BackupJobs(HistoryStore history, Action changed) : IDisposable
{
    private sealed class Job
    {
        internal BackupState State;
        internal readonly CancellationTokenSource Cancellation = new();
        internal readonly CancellationTokenRegistration Registration;
        internal Task Work = Task.CompletedTask;
        internal HistoryRestore? Restore;
        internal long ReadyAt;
        internal bool WorkerFinished;
        internal Job(string id, BackupPhase phase, CancellationToken lifetime)
        {
            State = new(id, phase);
            Registration = lifetime.UnsafeRegister(static state => _ = RequestCancellationAsync((CancellationTokenSource)state!), Cancellation);
        }
    }
    private readonly object _gate = new();
    private Job? _job;
    private bool _disposed;
    internal bool HoldsCapture { get { lock (_gate) return _job is not null && (Active(_job.State.Phase) || !_job.Work.IsCompleted); } }
    internal Task Completion { get { lock (_gate) return _job?.Work ?? Task.CompletedTask; } }
    private static bool Active(BackupPhase phase) => phase is not (BackupPhase.Completed or BackupPhase.Canceled or BackupPhase.Failed);
    internal BackupState Start(string id, bool importing, string path, byte[] password, FileActions.DestinationRevision? expected, CancellationToken lifetime)
    {
        if (id.Length != 32 || !id.All(Uri.IsHexDigit) || password.Length is < 12 or > 1024) throw new ArgumentException("Invalid transfer identity or password length.");
        FileActions.ValidateLocation(path);
        if (!string.Equals(Path.GetExtension(path), ".clippboard", StringComparison.OrdinalIgnoreCase)) throw new FileActionException("Choose a .clippboard backup file.");
        if (!importing && expected is null) throw new ArgumentException("Export destination revision is required.");
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_job is not null && (Active(_job.State.Phase) || !_job.Work.IsCompleted)) throw new InvalidOperationException("Finish or cancel the current transfer first.");
            lifetime.ThrowIfCancellationRequested();
            var job = _job = new Job(id, importing ? BackupPhase.Inspecting : BackupPhase.Exporting, lifetime);
            var ownedPassword = password.ToArray();
            job.Work = Task.Run(() => RunAsync(job, async cancellation =>
            {
                if (importing)
                {
                    await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous | FileOptions.SequentialScan);
                    if (input.Length > 514L * 1024 * 1024) throw new InvalidDataException("Backup file exceeds its format limit.");
                    job.Restore = await history.StageRestoreAsync(input, ownedPassword, cancellation,
                        (done, total) => Update(job, new(id, BackupPhase.Inspecting, done, total)));
                    cancellation.ThrowIfCancellationRequested(); job.ReadyAt = Stopwatch.GetTimestamp();
                }
                else
                {
                    await ExportAsync(job, path, ownedPassword, expected!, cancellation);
                    Update(job, new(id, BackupPhase.Completed, Message: "Encrypted backup saved and verified."));
                }
            }, ownedPassword));
            return job.State;
        }
    }
    internal BackupState? Snapshot(string id)
    {
        Expire(); lock (_gate) return _job?.State.Id == id ? _job.State : null;
    }
    internal BackupState Commit(string id)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var job = _job;
            if (job?.State.Id != id || job.State.Phase != BackupPhase.Ready || job.Restore is null || !job.WorkerFinished)
                throw new InvalidOperationException("The restore preview is not ready. Inspect the backup again.");
            job.State = new(id, BackupPhase.Restoring, Preview: job.Restore.Preview);
            job.WorkerFinished = false;
            job.Work = Task.Run(() => RunAsync(job, async cancellation =>
            {
                var result = await history.CommitRestoreAsync(job.Restore, cancellation);
                try { changed(); } catch (InvalidOperationException) { /* A closing UI cannot undo an already committed restore. */ }
                Update(job, new(id, BackupPhase.Completed, Preview: result, Message: $"Restored {result.NewNotes} notes. Existing notes were preserved."));
            }));
            return job.State;
        }
    }
    internal void Cancel(string? id = null)
    {
        lock (_gate)
        {
            var job = _job;
            if (job is null || (id is not null && job.State.Id != id) || !Active(job.State.Phase)) return;
            _ = RequestCancellationAsync(job.Cancellation); var wasReady = job.State.Phase == BackupPhase.Ready;
            job.State = job.State with { Phase = BackupPhase.Canceling, Message = "Canceling and cleaning encrypted staging…" };
            if (wasReady && job.WorkerFinished)
            {
                job.WorkerFinished = false;
                job.Work = Task.Run(() => RunAsync(job, cancellation => { cancellation.ThrowIfCancellationRequested(); return Task.CompletedTask; }));
            }
        }
    }
    internal void Expire()
    {
        lock (_gate)
            if (_job is { } job && job.State.Phase == BackupPhase.Ready
                && (job.Cancellation.IsCancellationRequested || Stopwatch.GetElapsedTime(job.ReadyAt) > TimeSpan.FromMinutes(10))) Cancel(job.State.Id);
    }
    private void Update(Job job, BackupState state)
    {
        lock (_gate)
        {
            if (!ReferenceEquals(_job, job)) return;
            if (job.State.Phase == BackupPhase.Canceling && Active(state.Phase)) return;
            job.State = state;
        }
    }
    private async Task RunAsync(Job job, Func<CancellationToken, Task> action, byte[]? password = null)
    {
        try { await action(job.Cancellation.Token); }
        catch (OperationCanceledException) { Update(job, job.State with { Phase = BackupPhase.Canceled, Message = "Transfer canceled." }); }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or InvalidOperationException
            or ArgumentException or CryptographicException or System.Text.Json.JsonException or NotSupportedException)
        {
            var message = ex is CryptographicException ? "The password is incorrect or the backup is damaged. Existing notes were preserved."
                : "Could not finish this transfer. Check the backup, available history capacity, free disk space and access. Existing notes were preserved.";
            Update(job, job.State with { Phase = BackupPhase.Failed, Message = message, FailureCode = ex.GetType().Name + ":" + ex.HResult.ToString("X8", System.Globalization.CultureInfo.InvariantCulture) });
        }
        finally
        {
            if (password is not null) CryptographicOperations.ZeroMemory(password);
            HistoryRestore? cleanup = null; var dispose = false;
            lock (_gate)
            {
                if (job.State.Phase == BackupPhase.Inspecting && job.Restore is not null && !job.Cancellation.IsCancellationRequested)
                    job.State = new(job.State.Id, BackupPhase.Ready, Preview: job.Restore.Preview, Message: "Review the counts, then restore. Existing notes are preserved.");
                if (job.State.Phase == BackupPhase.Ready && job.Cancellation.IsCancellationRequested)
                    job.State = job.State with { Phase = BackupPhase.Canceled, Message = "Transfer canceled." };
                if (job.State.Phase == BackupPhase.Canceling) job.State = job.State with { Phase = BackupPhase.Canceled, Message = "Transfer canceled." };
                if (job.State.Phase == BackupPhase.Ready) job.WorkerFinished = true;
                else { cleanup = job.Restore; job.Restore = null; dispose = true; }
            }
            cleanup?.Dispose(); if (dispose) { job.Registration.Dispose(); job.Cancellation.Dispose(); }
        }
    }
    private async Task ExportAsync(Job job, string target, byte[] password, FileActions.DestinationRevision expected, CancellationToken cancellation)
    {
        var temporary = Path.Combine(Path.GetDirectoryName(target)!, ".clippboard-backup-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            if (expected != FileActions.DestinationState(target)) throw new FileActionException("The destination changed. Select it again.");
            using var guard = expected.Exists ? new FileStream(target, FileMode.Open, FileAccess.ReadWrite, FileShare.Delete, 1) : null;
            if (expected != FileActions.DestinationState(target)) throw new FileActionException("The destination changed. Select it again.");
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, FileOptions.Asynchronous))
            {
                await PortableArchive.ExportAsync(history, output, password, cancellation,
                    (done, total) => Update(job, new(job.State.Id, BackupPhase.Exporting, done, total)));
                await output.FlushAsync(cancellation); output.Flush(true);
            }
            await using (var input = new FileStream(temporary, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous))
                await PortableArchive.ReadAsync(input, password, _ => Task.CompletedTask, cancellation,
                    (done, total) => Update(job, new(job.State.Id, BackupPhase.Verifying, done, total)));
            cancellation.ThrowIfCancellationRequested();
            if (expected != FileActions.DestinationState(target)) throw new FileActionException("The destination changed. Select it again.");
            File.Move(temporary, target, expected.Exists);
        }
        finally { File.Delete(temporary); }
    }
    public void Dispose() { lock (_gate) { if (_disposed) return; _disposed = true; Cancel(); } }
    private static async Task RequestCancellationAsync(CancellationTokenSource source)
    { try { await source.CancelAsync(); } catch (Exception ex) when (ex is ObjectDisposedException or AggregateException) { } }
}
