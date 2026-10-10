using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Clippboard.Core;

// Only short summaries remain in memory. Payloads are encrypted independently on disk.
public sealed class HistoryStore(string directory, IContentProtector protector, HistoryLimits? limits = null) : IHistoryAccess
{
    private HistoryLimits _limits = Validate(limits ?? new());
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, ClipSummary> _items = new();
    public int UnreadableEntries { get; private set; }

    private sealed record StoredSummary(ClipSummary Summary, long ModifiedUtcTicks);
    private static HistoryLimits Validate(HistoryLimits value)
    {
        if (value.MaxEntries < 1 || value.MaxBytes < 1 || value.MaxEntryBytes < 1 || value.RetentionDays is < 1 or > 30)
            throw new ArgumentOutOfRangeException(nameof(value));
        return value;
    }

    private string FileFor(string id)
    {
        if (id.Length != 32 || !id.All(Uri.IsHexDigit)) throw new InvalidDataException("Invalid clip ID.");
        return Path.Combine(directory, id + ".clip");
    }

    public async Task InitializeAsync()
    {
        await _gate.WaitAsync();
        try
        {
            Directory.CreateDirectory(directory);
            _items.Clear();
            UnreadableEntries = 0;
            foreach (var file in Directory.EnumerateFiles(directory, "*.tmp")) File.Delete(file);
            foreach (var file in Directory.EnumerateFiles(directory, "*.clip"))
            {
                try
                {
                    var size = new FileInfo(file).Length;
                    if (size > _limits.MaxEntryBytes) throw new InvalidDataException("Entry is too large.");
                    var info = new FileInfo(file);
                    var cached = await ReadSummaryAsync(file, info);
                    if (cached is not null) { _items[cached.Id] = cached; continue; }
                    var clip = await ReadFileAsync(file);
                    if (!string.Equals(Path.GetFileName(file), clip.Id + ".clip", StringComparison.Ordinal))
                        throw new InvalidDataException("Entry identity mismatch.");
                    _ = FileFor(clip.Id);
                    _items[clip.Id] = clip.Summary(size);
                    await CacheSummaryAsync(_items[clip.Id]);
                }
                catch (Exception ex) when (ex is IOException or JsonException or System.Security.Cryptography.CryptographicException or ArgumentException)
                {
                    // Unreadable history must not silently consume the storage budget forever.
                    UnreadableEntries++;
                    File.Delete(file);
                    File.Delete(Path.ChangeExtension(file, ".meta"));
                }
            }
            foreach (var metadata in Directory.EnumerateFiles(directory, "*.meta"))
                if (!_items.ContainsKey(Path.GetFileNameWithoutExtension(metadata))) File.Delete(metadata);
            Trim(DateTimeOffset.UtcNow);
        }
        finally { _gate.Release(); }
    }

    public async Task<IReadOnlyList<ClipSummary>> ListAsync()
    {
        await _gate.WaitAsync();
        try { Trim(DateTimeOffset.UtcNow); return _items.Values.OrderByDescending(x => x.Pinned).ThenByDescending(x => x.CopiedAt).ToArray(); }
        finally { _gate.Release(); }
    }

    public async Task<bool> AddAsync(Clip clip)
    {
        await _gate.WaitAsync();
        try
        {
            Trim(DateTimeOffset.UtcNow);
            var fingerprint = clip.Fingerprint();
            var duplicate = _items.Values.FirstOrDefault(x => x.Fingerprint == fingerprint);
            if (duplicate is not null) clip = clip with { Id = duplicate.Id, Pinned = duplicate.Pinned };
            var bytes = Encrypt(clip);
            if (bytes.Length > _limits.MaxEntryBytes) return false;
            // Plan eviction first: a rejected addition must not remove existing history.
            var candidates = _items.Values.Where(x => x.Id != clip.Id && !x.Pinned)
                .OrderBy(x => x.CopiedAt).ToList();
            var count = _items.Count - (duplicate is null ? 0 : 1) + 1;
            var total = _items.Values.Where(x => x.Id != clip.Id).Sum(x => x.StoredBytes) + bytes.Length;
            var evictions = new List<string>();
            foreach (var victim in candidates)
            {
                if (count <= _limits.MaxEntries && total <= _limits.MaxBytes) break;
                evictions.Add(victim.Id);
                count--;
                total -= victim.StoredBytes;
            }
            if (count > _limits.MaxEntries || total > _limits.MaxBytes) return false;
            await WriteAsync(clip.Id, bytes);
            foreach (var id in evictions) Remove(id);
            _items[clip.Id] = clip.Summary(bytes.Length);
            await CacheSummaryAsync(_items[clip.Id]);
            return true;
        }
        finally { _gate.Release(); }
    }

    public async Task<Clip?> GetAsync(string id)
    {
        await _gate.WaitAsync();
        try
        {
            Trim(DateTimeOffset.UtcNow);
            return _items.ContainsKey(id) ? await ReadFileAsync(FileFor(id)) : null;
        }
        finally { _gate.Release(); }
    }

    public async Task SetPinnedAsync(string id, bool pinned)
    {
        await _gate.WaitAsync();
        try
        {
            if (!_items.ContainsKey(id)) return;
            var clip = (await ReadFileAsync(FileFor(id))) with { Pinned = pinned };
            var bytes = Encrypt(clip);
            await WriteAsync(id, bytes);
            _items[id] = clip.Summary(bytes.Length);
            await CacheSummaryAsync(_items[id]);
            Trim(DateTimeOffset.UtcNow);
        }
        finally { _gate.Release(); }
    }

    public async Task DeleteAsync(string id)
    {
        await _gate.WaitAsync();
        try { if (_items.ContainsKey(id)) Remove(id); }
        finally { _gate.Release(); }
    }

    public async Task ClearAsync()
    {
        await _gate.WaitAsync();
        try { foreach (var id in _items.Keys.ToArray()) Remove(id); }
        finally { _gate.Release(); }
    }

    public async Task PruneAsync()
    {
        await _gate.WaitAsync();
        try { Trim(DateTimeOffset.UtcNow); }
        finally { _gate.Release(); }
    }

    public async Task<bool> UpdateLimitsAsync(HistoryLimits limits)
    {
        limits = Validate(limits);
        await _gate.WaitAsync();
        try
        {
            var cutoff = DateTimeOffset.UtcNow.AddDays(-limits.RetentionDays);
            var pins = _items.Values.Where(x => x.Pinned && x.CopiedAt >= cutoff).ToArray();
            if (pins.Length > limits.MaxEntries || pins.Sum(x => x.StoredBytes) > limits.MaxBytes) return false;
            _limits = limits;
            Trim(DateTimeOffset.UtcNow);
            return true;
        }
        finally { _gate.Release(); }
    }

    private void Trim(DateTimeOffset now)
    {
        foreach (var item in _items.Values.Where(x => x.CopiedAt < now.AddDays(-_limits.RetentionDays)).ToArray())
            Remove(item.Id);
        foreach (var item in _items.Values.Where(x => !x.Pinned).OrderBy(x => x.CopiedAt).ToArray())
        {
            if (_items.Count <= _limits.MaxEntries && _items.Values.Sum(x => x.StoredBytes) <= _limits.MaxBytes) break;
            Remove(item.Id);
        }
    }

    private void Remove(string id)
    {
        File.Delete(FileFor(id));
        File.Delete(Path.ChangeExtension(FileFor(id), ".meta"));
        _items.Remove(id);
    }

    private async Task<Clip> ReadFileAsync(string file)
    {
        if (new FileInfo(file).Length > _limits.MaxEntryBytes) throw new InvalidDataException("Entry is too large.");
        var bytes = await File.ReadAllBytesAsync(file);
        var plaintext = protector.Unprotect(bytes);
        try { return JsonSerializer.Deserialize<Clip>(plaintext) ?? throw new InvalidDataException("Empty entry."); }
        finally { System.Security.Cryptography.CryptographicOperations.ZeroMemory(plaintext); }
    }

    private byte[] Encrypt<T>(T value)
    {
        var plaintext = JsonSerializer.SerializeToUtf8Bytes(value);
        try { return protector.Protect(plaintext); }
        finally { System.Security.Cryptography.CryptographicOperations.ZeroMemory(plaintext); }
    }

    private async Task<ClipSummary?> ReadSummaryAsync(string payloadFile, FileInfo info)
    {
        var metadata = Path.ChangeExtension(payloadFile, ".meta");
        if (!File.Exists(metadata) || new FileInfo(metadata).Length > 64 * 1024) return null;
        try
        {
            var plaintext = protector.Unprotect(await File.ReadAllBytesAsync(metadata));
            try
            {
                var cached = JsonSerializer.Deserialize<StoredSummary>(plaintext);
                if (cached?.Summary is not { } summary || summary.Id is null || summary.Preview is null || summary.Preview.Length > 320
                    || cached.ModifiedUtcTicks != info.LastWriteTimeUtc.Ticks || summary.StoredBytes != info.Length
                    || summary.Id != Path.GetFileNameWithoutExtension(payloadFile) || !Enum.IsDefined(summary.Kind)) return null;
                _ = FileFor(cached.Summary.Id);
                return cached.Summary;
            }
            finally { System.Security.Cryptography.CryptographicOperations.ZeroMemory(plaintext); }
        }
        catch (Exception ex) when (ex is IOException or JsonException or System.Security.Cryptography.CryptographicException or ArgumentException) { return null; }
    }

    private async Task CacheSummaryAsync(ClipSummary summary)
    {
        var file = FileFor(summary.Id);
        var metadata = Path.ChangeExtension(file, ".meta");
        var temporary = metadata + ".tmp";
        try
        {
            await File.WriteAllBytesAsync(temporary, Encrypt(new StoredSummary(summary, new FileInfo(file).LastWriteTimeUtc.Ticks)));
            File.Move(temporary, metadata, true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* Payload remains authoritative; rebuild the optional summary on restart. */ }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private async Task WriteAsync(string id, byte[] bytes)
    {
        var file = FileFor(id);
        var temporary = file + ".tmp";
        try
        {
            await File.WriteAllBytesAsync(temporary, bytes);
            File.Move(temporary, file, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
