using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Clippboard.Core;

public sealed record RestorePreview(int NewNotes, int AlreadyPresent, int Expired, long EncryptedBytes);
public sealed class HistoryRestore : IDisposable
{
    internal readonly HistoryStore Owner;
    internal readonly string Folder;
    internal readonly long Epoch;
    internal readonly DateTimeOffset Created = DateTimeOffset.UtcNow;
    internal readonly List<ClipSummary> Entries = new();
    internal readonly Dictionary<string, string> Hashes = new();
    internal bool Disposed;
    public RestorePreview Preview { get; internal set; } = new(0, 0, 0, 0);
    internal HistoryRestore(HistoryStore owner, string folder, long epoch) { Owner = owner; Folder = folder; Epoch = epoch; }
    public void Dispose() { if (Disposed) return; Disposed = true; Entries.Clear(); Hashes.Clear(); Owner.CleanupRestoreFolder(Folder); }
}

public sealed partial class HistoryStore
{
    private long _restoreEpoch;
    private sealed record RestoreFile(string Id, string Name, long Length, string Hash);
    private sealed record RestoreJournal(string Folder, List<RestoreFile> Files);
    public async Task<HistoryRestore> StageRestoreAsync(Stream input, ReadOnlyMemory<byte> password,
        CancellationToken cancellation = default, Action<int, int>? progress = null)
    {
        ClipSummary[] existing; HistoryLimits limits; long epoch;
        await _gate.WaitAsync(cancellation);
        try { Trim(DateTimeOffset.UtcNow); existing = _items.Values.ToArray(); limits = _limits; epoch = _restoreEpoch; }
        finally { _gate.Release(); }
        var session = new HistoryRestore(this, "restore-" + Guid.NewGuid().ToString("N"), epoch);
        var root = RestoreFolder(session.Folder); Directory.CreateDirectory(root);
        var expired = 0; var duplicates = 0; long bytes = 0;
        var existingBytes = existing.Sum(item => item.StoredBytes);
        var sourceIds = new HashSet<string>(existing.Select(item => item.Id), StringComparer.OrdinalIgnoreCase);
        try
        {
            await PortableArchive.ReadAsync(input, password, async clip =>
            {
                cancellation.ThrowIfCancellationRequested();
                if (clip.CopiedAt < DateTimeOffset.UtcNow.AddDays(-limits.RetentionDays)) { expired++; return; }
                var summary = clip.Summary(0);
                if (existing.Any(item => PortableArchive.SameContent(item, summary))) { duplicates++; return; }
                var id = clip.Id.ToLowerInvariant();
                if (sourceIds.Contains(id)) do { id = Guid.NewGuid().ToString("N"); } while (sourceIds.Contains(id));
                sourceIds.Add(id); clip = clip with { Id = id };
                var payload = PreparePayload(clip, summary.Fingerprint);
                if (payload.Length > limits.MaxEntryBytes || payload.Length > limits.MaxBytes - bytes - existingBytes
                    || session.Entries.Count + existing.Length >= limits.MaxEntries)
                    throw new InvalidOperationException("This backup does not fit the available history budget. Free space or increase limits before importing.");
                var file = Path.Combine(root, id + (payload.Modern ? ".clip2" : ".clip"));
                session.Hashes.Add(id, PreparedHash(payload));
                await WritePreparedFileAsync(file, payload, false, cancellation);
                bytes += payload.Length; session.Entries.Add(clip.Summary(payload.Length, summary.Fingerprint));
            }, cancellation, progress);
            cancellation.ThrowIfCancellationRequested();
            session.Preview = new(session.Entries.Count, duplicates, expired, bytes); return session;
        }
        catch { session.Dispose(); throw; }
    }
    public async Task<RestorePreview> CommitRestoreAsync(HistoryRestore session, CancellationToken cancellation = default)
    {
        if (session.Owner != this || session.Disposed) throw new InvalidOperationException("Restore preview is no longer available.");
        await _gate.WaitAsync(cancellation);
        var journalWritten = false;
        try
        {
            Trim(DateTimeOffset.UtcNow);
            if (session.Epoch != _restoreEpoch || session.Created < DateTimeOffset.UtcNow.AddMinutes(-10))
                throw new InvalidOperationException("History was cleared or the restore preview expired. Inspect the backup again.");
            var additions = new List<ClipSummary>(); var duplicate = session.Preview.AlreadyPresent; var expired = session.Preview.Expired;
            foreach (var entry in session.Entries)
            {
                if (entry.CopiedAt < DateTimeOffset.UtcNow.AddDays(-_limits.RetentionDays)) { expired++; continue; }
                if (_items.Values.Any(item => PortableArchive.SameContent(item, entry))) { duplicate++; continue; }
                if (_items.Keys.Any(id => string.Equals(id, entry.Id, StringComparison.OrdinalIgnoreCase)))
                    throw new InvalidOperationException("History changed after the preview. Inspect the backup again before restoring.");
                additions.Add(entry);
            }
            var addedBytes = additions.Sum(item => item.StoredBytes);
            if (_items.Count + additions.Count > _limits.MaxEntries || _items.Values.Sum(item => item.StoredBytes) + addedBytes > _limits.MaxBytes
                || additions.Any(item => item.StoredBytes > _limits.MaxEntryBytes))
                throw new InvalidOperationException("Restore no longer fits the history budget. Existing notes were not changed.");
            var folder = RestoreFolder(session.Folder); var files = new List<RestoreFile>();
            foreach (var entry in additions)
            {
                cancellation.ThrowIfCancellationRequested();
                var source = StagedFile(folder, entry.Id);
                await using var content = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 16384, FileOptions.Asynchronous);
                if (content.Length != entry.StoredBytes) throw new InvalidDataException("Staged restore changed after inspection.");
                var hash = Convert.ToHexString(await SHA256.HashDataAsync(content, cancellation));
                if (!session.Hashes.TryGetValue(entry.Id, out var expectedHash) || hash != expectedHash) throw new InvalidDataException("Staged restore changed after inspection.");
                files.Add(new(entry.Id, Path.GetFileName(source), content.Length, hash));
                if (File.Exists(Path.Combine(directory, Path.GetFileName(source)))) throw new InvalidOperationException("A restore destination already exists. Inspect the backup again.");
            }
            if (files.Count == 0) { session.Dispose(); return new(0, duplicate, expired, 0); }
            var marker = Path.Combine(directory, "restore.pending"); var temporary = marker + ".tmp";
            try { await File.WriteAllBytesAsync(temporary, Encrypt(new RestoreJournal(session.Folder, files)), cancellation); cancellation.ThrowIfCancellationRequested(); File.Move(temporary, marker, false); }
            finally { File.Delete(temporary); }
            journalWritten = true;
            foreach (var file in files)
            {
                cancellation.ThrowIfCancellationRequested(); File.Move(Path.Combine(folder, file.Name), Path.Combine(directory, file.Name), false);
            }
            File.Delete(marker); journalWritten = false; // Commit point; startup can now load every complete new record.
            foreach (var entry in additions)
            {
                _items.Add(entry.Id, entry);
                if (File.Exists(Path.Combine(directory, entry.Id + ".clip2"))) _modernPayloads.Add(entry.Id);
            }
            _orderedSnapshot = null;
            foreach (var entry in additions) await CacheSummaryAsync(entry);
            session.Dispose(); return new(additions.Count, duplicate, expired, addedBytes);
        }
        catch
        {
            if (journalWritten) CompletePendingRestore();
            throw;
        }
        finally { _gate.Release(); }
    }
    private static bool ValidRestoreName(string name) => name.StartsWith("restore-", StringComparison.Ordinal) && name.Length == 40 && name[8..].All(Uri.IsHexDigit);
    private static string PreparedHash(PreparedPayload payload)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        if (payload.Image is not null) hash.AppendData(ImageHeader(payload));
        hash.AppendData(payload.Metadata); if (payload.Image is not null) hash.AppendData(payload.Image);
        return Convert.ToHexString(hash.GetHashAndReset());
    }
    private string RestoreFolder(string name)
    {
        if (!ValidRestoreName(name)) throw new InvalidDataException("Invalid restore staging name.");
        var parent = Path.GetFullPath(directory); var folder = Path.GetFullPath(Path.Combine(parent, name));
        if (!string.Equals(Path.GetDirectoryName(folder), parent.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Restore staging escaped history directory.");
        if (Directory.Exists(folder) && (File.GetAttributes(folder) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("Restore staging cannot be a linked directory.");
        return folder;
    }
    private static string StagedFile(string folder, string id)
    {
        var modern = Path.Combine(folder, id + ".clip2"); return File.Exists(modern) ? modern : Path.Combine(folder, id + ".clip");
    }
    internal void CleanupRestoreFolder(string name)
    {
        try
        {
            var folder = RestoreFolder(name); if (!Directory.Exists(folder)) return;
            foreach (var file in Directory.EnumerateFiles(folder))
            {
                var stem = Path.GetFileNameWithoutExtension(file);
                if (Path.GetExtension(file) == ".tmp") stem = Path.GetFileNameWithoutExtension(stem);
                if (stem.Length == 32 && stem.All(Uri.IsHexDigit) && (file.EndsWith(".clip", StringComparison.Ordinal) || file.EndsWith(".clip2", StringComparison.Ordinal) || file.EndsWith(".tmp", StringComparison.Ordinal))) File.Delete(file);
            }
            Directory.Delete(folder); // Never recurse into unexpected folders or junctions.
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException) { /* Encrypted staging cleanup retries at startup; never follow a replaced staging link. */ }
    }
    private void CleanupAbandonedRestores()
    {
        foreach (var folder in Directory.EnumerateDirectories(directory, "restore-*"))
            if (ValidRestoreName(Path.GetFileName(folder))) CleanupRestoreFolder(Path.GetFileName(folder));
    }
    private void CompletePendingRestore()
    {
        var marker = Path.Combine(directory, "restore.pending"); if (!File.Exists(marker)) return;
        if (new FileInfo(marker).Length is <= 0 or > 1024 * 1024) throw new InvalidDataException("Restore recovery journal exceeds its limit.");
        var plain = protector.Unprotect(File.ReadAllBytes(marker)); RestoreJournal journal;
        try { journal = JsonSerializer.Deserialize<RestoreJournal>(plain) ?? throw new InvalidDataException("Empty restore journal."); }
        finally { CryptographicOperations.ZeroMemory(plain); }
        if (journal.Folder is null || journal.Files is not { Count: > 0 and <= PortableArchive.MaxEntries }) throw new InvalidDataException("Invalid restore recovery journal.");
        _ = RestoreFolder(journal.Folder); var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in journal.Files)
        {
            if (file is null || file.Id is null || file.Id.Length != 32 || !file.Id.All(Uri.IsHexDigit) || !ids.Add(file.Id)
                || (file.Name != file.Id + ".clip" && file.Name != file.Id + ".clip2") || file.Length is <= 0 or > 24 * 1024 * 1024
                || file.Hash is null || file.Hash.Length != 64 || !file.Hash.All(Uri.IsHexDigit)) throw new InvalidDataException("Unsafe restore recovery record.");
        }
        // Interrupted imports roll back only their newly introduced ciphertext.
        // Never overwrite or delete an unrelated pre-existing payload.
        foreach (var file in journal.Files)
        {
            var target = Path.Combine(directory, file.Name); if (!File.Exists(target)) continue;
            using (var stream = File.OpenRead(target))
                if (stream.Length != file.Length || Convert.ToHexString(SHA256.HashData(stream)) != file.Hash) throw new InvalidDataException("Restore recovery destination changed; existing data was preserved.");
            File.Delete(target); File.Delete(Path.ChangeExtension(target, ".meta"));
        }
        CleanupRestoreFolder(journal.Folder); File.Delete(marker);
    }
}
